using Gcs.Domain.Vehicles;

namespace Gcs.Mavlink.Transports;

/// <summary>
/// Moves raw bytes to and from a vehicle. Knows nothing about MAVLink: framing and parsing happen above it,
/// so UDP, TCP, serial, radio modems and the in-process simulator are interchangeable.
/// </summary>
public interface IMavlinkTransport : IAsyncDisposable
{
    /// <summary>Human readable endpoint for logs, e.g. <c>udp://0.0.0.0:14550</c>.</summary>
    string Description { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>Sends one buffer. Datagram transports may drop it silently (UDP gives no delivery guarantee).</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Waits for incoming bytes and returns how many were written to <paramref name="buffer"/>.</summary>
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>Creates the transport that matches a vehicle's connection settings.</summary>
public interface IMavlinkTransportFactory
{
    /// <param name="settings">Where and how to reach the vehicle.</param>
    /// <param name="systemId">The vehicle's MAVLink system id (the in-process simulator impersonates it).</param>
    IMavlinkTransport Create(ConnectionSettings settings, byte systemId);
}
