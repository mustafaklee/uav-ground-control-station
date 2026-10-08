using Gcs.Domain.Vehicles;

namespace Gcs.Application.Abstractions;

/// <summary>
/// What the link layer observes about the network (ADR-019): which local endpoint a vehicle's link uses, and every
/// system heard on each endpoint, registered or not. Implemented by Gcs.Mavlink.
/// </summary>
public interface INetworkTopologySource
{
    /// <summary>The endpoint a vehicle's link uses, named exactly as <see cref="GetObservedEndpoints"/> names it, e.g. <c>udp://0.0.0.0:14550</c>.</summary>
    string EndpointOf(ConnectionSettings connection, MavlinkSystemId systemId);

    /// <summary>Open endpoints that can hear more than one system (shared UDP ports) and what they heard.</summary>
    IReadOnlyList<ObservedEndpoint> GetObservedEndpoints();
}

public sealed record ObservedEndpoint(string Endpoint, IReadOnlyList<HeardSystemInfo> Heard);

/// <param name="SystemId">MAVLink system id.</param>
/// <param name="Remote">Address it sends from.</param>
/// <param name="LastHeardAt">When it was last heard.</param>
/// <param name="Datagrams">Datagrams received from it.</param>
public sealed record HeardSystemInfo(byte SystemId, string Remote, DateTimeOffset LastHeardAt, long Datagrams);

/// <summary>
/// Radios on the vehicle links: the "MANET abstraction" of ADR-019. The GCS does not route over a radio network; it
/// observes it. The first implementation reads RADIO_STATUS from MAVLink (point-to-point telemetry radios). A mesh radio
/// would be another implementation that asks the radio itself (SNMP, REST) for its nodes and their neighbours.
/// </summary>
public interface IRadioNetworkProvider
{
    IReadOnlyList<RadioNode> GetRadioNodes();
}

/// <param name="Id">Stable id of the radio node.</param>
/// <param name="Kind">What reports it, e.g. <c>mavlink-radio</c>.</param>
/// <param name="Endpoint">The GCS endpoint behind which the radio sits.</param>
/// <param name="VehicleId">The vehicle the radio carries, when known.</param>
/// <param name="Status">Signal, noise and errors at this node.</param>
/// <param name="Neighbours">Nodes this one hears directly, with the signal it hears them at.</param>
public sealed record RadioNode(
    string Id,
    string Kind,
    string Endpoint,
    VehicleId? VehicleId,
    RadioLinkStatus Status,
    IReadOnlyList<RadioNeighbour> Neighbours);

public sealed record RadioNeighbour(string NodeId, int? Rssi);
