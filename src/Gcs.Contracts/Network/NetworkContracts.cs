using Gcs.Contracts.Vehicles;

namespace Gcs.Contracts.Network;

/// <summary>
/// <c>GET /api/v1/network/topology</c> (ADR-019): the GCS's link endpoints, the vehicles behind each with their link
/// quality, systems heard but not registered, and the radios on the links.
/// </summary>
public sealed record NetworkTopologyResponse(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<LinkEndpointDto> Endpoints,
    IReadOnlyList<RadioNodeDto> Radios);

/// <summary>
/// One endpoint of the GCS, e.g. <c>udp://0.0.0.0:14550</c>, <c>tcp://10.0.0.5:5760</c>, <c>simulator://system-3</c>.
/// <c>Transport</c> is Udp, Tcp, Serial or Simulator. <c>Vehicles</c>: registered vehicles with an active link here.
/// <c>UnregisteredSystems</c>: MAVLink systems heard here that no linked vehicle claims (a vehicle not yet added, or a radio).
/// </summary>
public sealed record LinkEndpointDto(
    string Id,
    string Transport,
    IReadOnlyList<TopologyVehicleDto> Vehicles,
    IReadOnlyList<HeardSystemDto> UnregisteredSystems);

/// <summary>A vehicle on an endpoint. <c>Remote</c>: the address it sends from, when the endpoint knows it (shared UDP).</summary>
public sealed record TopologyVehicleDto(
    Guid VehicleId,
    string Callsign,
    int SystemId,
    string State,
    LinkQualityDto Quality,
    string? Remote);

public sealed record HeardSystemDto(int SystemId, string Remote, DateTimeOffset LastHeardAt, long Datagrams);

/// <summary>
/// A radio node. <c>Kind</c>: what reports it, e.g. <c>mavlink-radio</c>. <c>Endpoint</c>: the GCS endpoint behind which
/// the radio sits. <c>VehicleId</c>: the vehicle at the other end, when known.
/// </summary>
public sealed record RadioNodeDto(
    string Id,
    string Kind,
    string Endpoint,
    Guid? VehicleId,
    RadioStatusDto Status,
    IReadOnlyList<RadioNeighbourDto> Neighbours);

/// <summary>A node this one hears directly; <c>Rssi</c> is the signal it hears it at, in the radio's units.</summary>
public sealed record RadioNeighbourDto(string NodeId, int? Rssi);
