using System.Net.Sockets;

namespace Gcs.Mavlink.Transports;

/// <summary>
/// MAVLink over a TCP client connection (e.g. ArduPilot SITL on port 5760, or a TCP serial bridge).
/// Unlike UDP, a broken connection is noticed by the socket itself; reopening reconnects.
/// </summary>
public sealed class TcpMavlinkTransport(string host, int port) : IMavlinkTransport
{
    private readonly TcpClient _client = new() { NoDelay = true };
    private NetworkStream? _stream;

    public string Description { get; } = $"tcp://{host}:{port}";

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(host, port, cancellationToken);
        _stream = _client.GetStream();
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        await Stream.WriteAsync(data, cancellationToken);

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = await Stream.ReadAsync(buffer, cancellationToken);
        return read > 0 ? read : throw new IOException($"Connection to {Description} was closed by the remote side.");
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private NetworkStream Stream => _stream ?? throw new InvalidOperationException("Transport is not open.");
}
