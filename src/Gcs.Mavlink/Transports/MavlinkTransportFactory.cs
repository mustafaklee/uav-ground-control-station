using System.Net;
using Gcs.Domain.Vehicles;
using Gcs.Mavlink.Simulation;

namespace Gcs.Mavlink.Transports;

/// <summary>Maps a vehicle's <see cref="ConnectionSettings"/> to a transport.</summary>
internal sealed class MavlinkTransportFactory(TimeProvider time, UdpEndpointHub udp) : IMavlinkTransportFactory
{
    public IMavlinkTransport Create(ConnectionSettings settings, byte systemId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Transport switch
        {
            // UDP listens locally: Host is the local address to bind (0.0.0.0 = all interfaces), Port the local port.
            // Vehicles on the same port share one socket and are told apart by system id (ADR-019).
            TransportType.Udp => udp.Create(new IPEndPoint(ResolveBindAddress(settings.Host!), settings.Port!.Value), systemId),
            TransportType.Tcp => new TcpMavlinkTransport(settings.Host!, settings.Port!.Value),
            TransportType.Simulator => new SimulatorMavlinkTransport(new SimulatedVehicleOptions { SystemId = systemId }, time),
            TransportType.Serial => throw new NotSupportedException("Serial links are not supported yet (planned together with radio modems)."),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Transport, "Unknown transport."),
        };
    }

    internal static IPAddress ResolveBindAddress(string host) =>
        IPAddress.TryParse(host, out var address) ? address
        : string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
        : throw new NotSupportedException($"UDP listen address must be an IP address, got '{host}'.");
}
