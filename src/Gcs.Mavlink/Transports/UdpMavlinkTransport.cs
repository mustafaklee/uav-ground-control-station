using System.Net;
using System.Net.Sockets;

namespace Gcs.Mavlink.Transports;

/// <summary>
/// MAVLink over UDP, the usual link for SITL, companion computers and IP radios.
/// <list type="bullet">
/// <item><b>Listen</b> (GCS side, like QGroundControl's "udpin"): bind a local port and wait for the vehicle to send;
/// replies go to the address the vehicle last sent from. PX4 SITL sends to port 14550 by default.</item>
/// <item><b>Connect</b> (vehicle/simulator side, "udpout"): send to a known GCS address.</item>
/// </list>
/// UDP has no connection: "connected" only ever means "datagrams are arriving", which is why the link layer relies on
/// heartbeats to detect a dead link.
/// </summary>
public sealed class UdpMavlinkTransport : IMavlinkTransport
{
    // Windows reports an ICMP "port unreachable" from a previous send as an exception on the next receive.
    // Disabling SIO_UDP_CONNRESET keeps a listening socket alive when the vehicle side goes away.
    private const int SioUdpConnReset = -1744830452;

    private readonly IPEndPoint? _localEndPoint;
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private IPEndPoint? _remoteEndPoint;

    private UdpMavlinkTransport(IPEndPoint? localEndPoint, IPEndPoint? remoteEndPoint, string description)
    {
        _localEndPoint = localEndPoint;
        _remoteEndPoint = remoteEndPoint;
        Description = description;
    }

    public string Description { get; }

    /// <summary>The port actually bound (useful when listening on port 0 in tests).</summary>
    public int LocalPort => (_socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    public static UdpMavlinkTransport Listen(IPEndPoint localEndPoint)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        return new UdpMavlinkTransport(localEndPoint, null, $"udp-listen://{localEndPoint}");
    }

    public static UdpMavlinkTransport Connect(IPEndPoint remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        return new UdpMavlinkTransport(null, remoteEndPoint, $"udp-connect://{remoteEndPoint}");
    }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }

        _socket.Bind(_localEndPoint ?? new IPEndPoint(IPAddress.Any, 0));
        return Task.CompletedTask;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        // A listening GCS cannot send before the vehicle has spoken: it does not know where the vehicle is yet.
        var remote = _remoteEndPoint;
        if (remote is null)
        {
            return;
        }

        await _socket.SendToAsync(data, SocketFlags.None, remote, cancellationToken);
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
            var sender = (IPEndPoint)result.RemoteEndPoint;
            if (_localEndPoint is not null)
            {
                _remoteEndPoint = sender; // listen mode: answer whoever is talking to us
            }
            else if (!sender.Equals(_remoteEndPoint))
            {
                continue; // connect mode: ignore datagrams from anyone but the configured peer
            }

            return result.ReceivedBytes;
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
