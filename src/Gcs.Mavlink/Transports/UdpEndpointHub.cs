using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Gcs.Mavlink.Protocol;

namespace Gcs.Mavlink.Transports;

/// <summary>What one shared UDP endpoint has heard: for the network topology (ADR-019).</summary>
/// <param name="Endpoint">The local endpoint, e.g. <c>udp://0.0.0.0:14550</c>.</param>
/// <param name="ClaimedSystemIds">System ids with an open vehicle link on this endpoint.</param>
/// <param name="Heard">Every system id that sent something, claimed or not.</param>
public sealed record UdpEndpointSnapshot(string Endpoint, IReadOnlyList<byte> ClaimedSystemIds, IReadOnlyList<HeardSystem> Heard);

/// <param name="SystemId">MAVLink system id of the sender.</param>
/// <param name="Remote">Address and port it sends from.</param>
/// <param name="LastHeardAt">When its last datagram arrived.</param>
/// <param name="Datagrams">Datagrams received from it while the endpoint was open.</param>
public sealed record HeardSystem(byte SystemId, string Remote, DateTimeOffset LastHeardAt, long Datagrams);

/// <summary>
/// UDP listening sockets shared by every vehicle link on the same local endpoint (ADR-019). Several vehicles may send
/// to one port, as PX4 multi-vehicle SITL and many radios do; each datagram goes to the link that claimed the system id
/// of its first frame, and replies go to the address that system id last sent from.
/// <code>
///   UAV 1 (sysid 1) ─┐                         ┌─► link of vehicle A (sysid 1)
///   UAV 2 (sysid 2) ─┼─► 0.0.0.0:14550 ─ hub ──┼─► link of vehicle B (sysid 2)
///   UAV 7 (sysid 7) ─┘                         └─► nobody: counted as "heard, not registered"
/// </code>
/// A socket is bound when the first link opens it and closed when the last one leaves. Thread-safe.
/// </summary>
public sealed class UdpEndpointHub(TimeProvider time)
{
    private readonly Dictionary<string, SharedEndpoint> _endpoints = [];
    private readonly Lock _gate = new();

    /// <summary>A transport for one vehicle on a shared local endpoint. Opening it claims <paramref name="systemId"/>.</summary>
    public IMavlinkTransport Create(IPEndPoint localEndPoint, byte systemId)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        return new SharedUdpTransport(this, localEndPoint, systemId);
    }

    public IReadOnlyList<UdpEndpointSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return [.. _endpoints.Values.Select(e => e.Snapshot())];
        }
    }

    private SharedEndpoint.Claim Attach(IPEndPoint local, byte systemId)
    {
        lock (_gate)
        {
            var key = Key(local);
            if (_endpoints.TryGetValue(key, out var existing) && existing.TryJoin(systemId) is { } claim)
            {
                return claim;
            }

            // None yet, or the last link just left and it is closing: open a fresh socket.
            var endpoint = SharedEndpoint.Open(local, time, () => Forget(key));
            _endpoints[key] = endpoint;
            return endpoint.TryJoin(systemId)!;
        }
    }

    private void Forget(string key)
    {
        lock (_gate)
        {
            if (_endpoints.TryGetValue(key, out var endpoint) && endpoint.IsClosed)
            {
                _endpoints.Remove(key);
            }
        }
    }

    private static string Key(IPEndPoint local) => $"udp://{local}";

    /// <summary>One bound socket and the links that share it.</summary>
    private sealed class SharedEndpoint
    {
        // Windows reports an ICMP "port unreachable" from a previous send as an exception on the next receive.
        private const int SioUdpConnReset = -1744830452;
        private const int MaxDatagram = 65_507;

        // Per link: a bounded queue, so a link that stopped reading cannot grow memory without limit.
        private const int QueueCapacity = 1024;

        private readonly Socket _socket;
        private readonly string _name;
        private readonly TimeProvider _time;
        private readonly Action _onClosed;
        private readonly Lock _gate = new();
        private readonly Dictionary<byte, Channel<byte[]>> _links = [];
        private readonly Dictionary<byte, Heard> _heard = [];
        private bool _closed;

        private SharedEndpoint(Socket socket, string name, TimeProvider time, Action onClosed)
        {
            _socket = socket;
            _name = name;
            _time = time;
            _onClosed = onClosed;
        }

        public bool IsClosed
        {
            get
            {
                lock (_gate)
                {
                    return _closed;
                }
            }
        }

        public static SharedEndpoint Open(IPEndPoint local, TimeProvider time, Action onClosed)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
                }

                socket.Bind(local); // a port taken by another program fails here, and the link reports it
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            var endpoint = new SharedEndpoint(socket, Key(local), time, onClosed);
            _ = Task.Run(endpoint.ReceiveLoopAsync);
            return endpoint;
        }

        /// <summary>Joins the endpoint; null when it is already closing (the caller opens a new one).</summary>
        public Claim? TryJoin(byte systemId)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return null;
                }

                if (_links.ContainsKey(systemId))
                {
                    throw new InvalidOperationException(
                        $"System id {systemId} already has a link on {_name}; two vehicles on one port need different system ids.");
                }

                var inbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest, // old telemetry is worth less than new
                    SingleReader = true,
                });
                _links[systemId] = inbox;
                return new Claim(this, systemId, inbox.Reader);
            }
        }

        public UdpEndpointSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new UdpEndpointSnapshot(
                    _name,
                    [.. _links.Keys.Order()],
                    [.. _heard.OrderBy(h => h.Key).Select(h => new HeardSystem(h.Key, h.Value.Remote.ToString(), h.Value.LastHeardAt, h.Value.Datagrams))]);
            }
        }

        public async ValueTask SendAsync(byte systemId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            IPEndPoint? remote;
            lock (_gate)
            {
                remote = _heard.TryGetValue(systemId, out var heard) ? heard.Remote : null;
            }

            // Like a single-vehicle listener: nothing can be sent before the vehicle has spoken.
            if (remote is not null)
            {
                await _socket.SendToAsync(data, SocketFlags.None, remote, cancellationToken);
            }
        }

        private void Leave(byte systemId)
        {
            lock (_gate)
            {
                if (_links.Remove(systemId, out var inbox))
                {
                    inbox.Writer.TryComplete();
                }

                if (_links.Count > 0 || _closed)
                {
                    return;
                }

                _closed = true;
            }

            _socket.Dispose(); // ends the receive loop
            _onClosed();
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[MaxDatagram];
            try
            {
                while (true)
                {
                    var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0));
                    Route(buffer.AsSpan(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException || (ex is SocketException && IsClosed))
            {
                // closed by the last link leaving
            }
            catch (SocketException ex)
            {
                // The socket broke: every link's receive fails, its watchdog ends the session and reconnecting opens a new socket.
                lock (_gate)
                {
                    _closed = true;
                    foreach (var inbox in _links.Values)
                    {
                        inbox.Writer.TryComplete(ex);
                    }
                }

                _socket.Dispose();
                _onClosed();
            }
        }

        private void Route(ReadOnlySpan<byte> datagram, IPEndPoint sender)
        {
            if (MavlinkWire.PeekSystemId(datagram) is not { } systemId)
            {
                return; // not MAVLink
            }

            var copy = datagram.ToArray();
            lock (_gate)
            {
                ref var heard = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_heard, systemId, out _);
                heard = new Heard(sender, _time.GetUtcNow(), heard.Datagrams + 1);

                if (_links.TryGetValue(systemId, out var inbox))
                {
                    inbox.Writer.TryWrite(copy);
                    return;
                }

                // An unclaimed system from the same address as a vehicle, typically its telemetry radio reporting
                // RADIO_STATUS under the radio's own system id: hand it to that vehicle's link.
                foreach (var (claimed, link) in _links)
                {
                    if (_heard.TryGetValue(claimed, out var vehicle) && vehicle.Remote.Equals(sender))
                    {
                        link.Writer.TryWrite(copy);
                    }
                }
            }
        }

        private record struct Heard(IPEndPoint Remote, DateTimeOffset LastHeardAt, long Datagrams);

        /// <summary>One link's membership: its inbox, and leaving when the link closes.</summary>
        public sealed class Claim(SharedEndpoint endpoint, byte systemId, ChannelReader<byte[]> inbox)
        {
            public ChannelReader<byte[]> Inbox => inbox;

            public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
                endpoint.SendAsync(systemId, data, cancellationToken);

            public void Leave() => endpoint.Leave(systemId);
        }
    }

    /// <summary>The transport one vehicle link sees: an ordinary <see cref="IMavlinkTransport"/> over the shared socket.</summary>
    private sealed class SharedUdpTransport(UdpEndpointHub hub, IPEndPoint local, byte systemId) : IMavlinkTransport
    {
        private SharedEndpoint.Claim? _claim;

        public string Description { get; } = $"udp-listen://{local} (system {systemId})";

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            _claim = hub.Attach(local, systemId);
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            _claim?.SendAsync(data, cancellationToken) ?? ValueTask.CompletedTask;

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var claim = _claim ?? throw new InvalidOperationException("The transport is not open.");
            var datagram = await claim.Inbox.ReadAsync(cancellationToken);
            var length = Math.Min(datagram.Length, buffer.Length);
            datagram.AsMemory(0, length).CopyTo(buffer);
            return length;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _claim, null)?.Leave();
            return ValueTask.CompletedTask;
        }
    }
}
