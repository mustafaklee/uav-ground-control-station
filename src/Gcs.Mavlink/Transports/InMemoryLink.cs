using System.Threading.Channels;

namespace Gcs.Mavlink.Transports;

/// <summary>
/// A pair of connected in-process transports: what one side sends, the other receives. Used by the simulator
/// transport and by tests, so the full MAVLink stack can run without sockets.
/// </summary>
public sealed class InMemoryLink
{
    private readonly Channel<byte[]> _toVehicle = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _toGcs = Channel.CreateUnbounded<byte[]>();

    public InMemoryLink()
    {
        GcsSide = new Endpoint("memory://gcs", _toVehicle.Writer, _toGcs.Reader, this);
        VehicleSide = new Endpoint("memory://vehicle", _toGcs.Writer, _toVehicle.Reader, this);
    }

    public IMavlinkTransport GcsSide { get; }

    public IMavlinkTransport VehicleSide { get; }

    /// <summary>When true, everything sent in either direction is dropped, as if the radio link was cut.</summary>
    public bool IsCut { get; set; }

    private sealed class Endpoint(string description, ChannelWriter<byte[]> outgoing, ChannelReader<byte[]> incoming, InMemoryLink link)
        : IMavlinkTransport
    {
        public string Description => description;

        public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            link.IsCut ? ValueTask.CompletedTask : outgoing.WriteAsync(data.ToArray(), cancellationToken);

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var data = await incoming.ReadAsync(cancellationToken);
            data.CopyTo(buffer);
            return data.Length;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
