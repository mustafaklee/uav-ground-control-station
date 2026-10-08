using System.Net;
using Gcs.Application.Abstractions;
using Gcs.Domain.Vehicles;
using Gcs.Mavlink.Connections;
using Gcs.Mavlink.Transports;

namespace Gcs.Mavlink.Network;

/// <summary>
/// The network as the MAVLink layer sees it (ADR-019): endpoint names that match the shared UDP sockets, what each
/// socket heard, and the telemetry radios that report RADIO_STATUS on a vehicle link.
/// </summary>
internal sealed class MavlinkNetworkTopology(UdpEndpointHub udp, VehicleLinkManager links) : INetworkTopologySource, IRadioNetworkProvider
{
    public const string RadioKind = "mavlink-radio";

    public string EndpointOf(ConnectionSettings connection, MavlinkSystemId systemId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Transport switch
        {
            TransportType.Udp => $"udp://{new IPEndPoint(MavlinkTransportFactory.ResolveBindAddress(connection.Host!), connection.Port!.Value)}",
            TransportType.Tcp => $"tcp://{connection.Host}:{connection.Port}",
            TransportType.Serial => $"serial://{connection.SerialPortName}",
            _ => $"simulator://system-{systemId.Value}",
        };
    }

    public IReadOnlyList<ObservedEndpoint> GetObservedEndpoints() =>
        [.. udp.Snapshot().Select(e => new ObservedEndpoint(
            e.Endpoint,
            [.. e.Heard.Select(h => new HeardSystemInfo(h.SystemId, h.Remote, h.LastHeardAt, h.Datagrams))]))];

    /// <summary>
    /// One node per vehicle whose link carries RADIO_STATUS: the radio at the GCS end, with the vehicle's radio as its
    /// only neighbour (a point-to-point telemetry radio pair). The ground radio hears the air radio at <c>rssi</c>;
    /// <c>remrssi</c> is how the air radio hears the ground, and stays in the node's status.
    /// </summary>
    public IReadOnlyList<RadioNode> GetRadioNodes() =>
        [.. links.GetLinks()
            .Where(l => l.Status.Quality.Radio is not null)
            .Select(l => new RadioNode(
                $"radio-{l.Target.VehicleId.Value:N}",
                RadioKind,
                EndpointOf(l.Target.Connection, l.Target.SystemId),
                l.Target.VehicleId,
                l.Status.Quality.Radio!,
                [new RadioNeighbour($"radio-{l.Target.VehicleId.Value:N}-air", l.Status.Quality.Radio!.Rssi)]))];
}
