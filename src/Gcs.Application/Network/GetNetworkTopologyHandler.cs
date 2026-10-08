using Gcs.Application.Abstractions;
using Gcs.Application.Vehicles;
using Gcs.Contracts.Network;
using Gcs.Contracts.Vehicles;

namespace Gcs.Application.Network;

/// <summary>
/// Use case: the network as the GCS sees it (ADR-019). Every active vehicle link grouped by the endpoint it uses, the
/// systems each shared endpoint hears that no vehicle claims, and the radios on the links.
/// </summary>
public sealed class GetNetworkTopologyHandler(
    IVehicleRepository vehicles,
    IVehicleLinkManager links,
    INetworkTopologySource topology,
    IEnumerable<IRadioNetworkProvider> radios,
    TimeProvider time)
{
    public async Task<NetworkTopologyResponse> HandleAsync(CancellationToken cancellationToken)
    {
        var observed = topology.GetObservedEndpoints().ToDictionary(e => e.Endpoint, e => e.Heard);
        var endpoints = new Dictionary<string, (string Transport, List<TopologyVehicleDto> Vehicles, HashSet<int> Claimed)>();

        foreach (var status in links.GetAll())
        {
            var vehicle = await vehicles.GetByIdAsync(status.VehicleId, cancellationToken);
            if (vehicle is null)
            {
                continue; // retired while its link was being torn down
            }

            var endpoint = topology.EndpointOf(vehicle.Connection, vehicle.SystemId);
            if (!endpoints.TryGetValue(endpoint, out var entry))
            {
                entry = (vehicle.Connection.Transport.ToString(), [], []);
                endpoints[endpoint] = entry;
            }

            var remote = observed.GetValueOrDefault(endpoint)?.FirstOrDefault(h => h.SystemId == vehicle.SystemId.Value)?.Remote;
            entry.Vehicles.Add(new TopologyVehicleDto(
                vehicle.Id.Value,
                vehicle.Callsign.Value,
                vehicle.SystemId.Value,
                status.State.ToString(),
                VehicleLinkMapping.ToDto(status.Quality),
                remote));
            entry.Claimed.Add(vehicle.SystemId.Value);
        }

        // Shared endpoints whose vehicles all disconnected are closed, so every observed endpoint has a vehicle; the
        // check stays in case a provider reports endpoints of its own.
        foreach (var endpoint in observed.Keys.Where(e => !endpoints.ContainsKey(e)))
        {
            endpoints[endpoint] = ("Udp", [], []);
        }

        return new NetworkTopologyResponse(
            time.GetUtcNow(),
            [.. endpoints.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new LinkEndpointDto(
                e.Key,
                e.Value.Transport,
                [.. e.Value.Vehicles.OrderBy(v => v.SystemId)],
                [.. (observed.GetValueOrDefault(e.Key) ?? [])
                    .Where(h => !e.Value.Claimed.Contains(h.SystemId))
                    .Select(h => new HeardSystemDto(h.SystemId, h.Remote, h.LastHeardAt, h.Datagrams))]))],
            [.. radios.SelectMany(r => r.GetRadioNodes()).Select(ToDto)]);
    }

    private static RadioNodeDto ToDto(RadioNode node) => new(
        node.Id,
        node.Kind,
        node.Endpoint,
        node.VehicleId?.Value,
        new RadioStatusDto(
            node.Status.Rssi, node.Status.RemoteRssi, node.Status.Noise, node.Status.RemoteNoise,
            node.Status.ReceiveErrors, node.Status.Corrected, node.Status.TxBufferPercent, node.Status.ReceivedAt),
        [.. node.Neighbours.Select(n => new RadioNeighbourDto(n.NodeId, n.Rssi))]);
}
