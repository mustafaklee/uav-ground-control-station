using Gcs.Mavlink.Simulation;

namespace Gcs.Mavlink.Transports;

/// <summary>
/// A transport with a simulated vehicle on the other end, running inside the GCS process.
/// Selecting transport "Simulator" for a vehicle gives a fully working link without any network or hardware.
/// </summary>
public sealed class SimulatorMavlinkTransport : IMavlinkTransport
{
    private readonly InMemoryLink _link = new();
    private readonly SimulatedVehicleRunner _runner;
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;

    public SimulatorMavlinkTransport(SimulatedVehicleOptions options, TimeProvider time)
    {
        _runner = new SimulatedVehicleRunner(new SimulatedVehicle(options), _link.VehicleSide, time);
        Description = $"simulator://sysid-{options.SystemId}";
    }

    public string Description { get; }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        _running ??= Task.Run(() => _runner.RunAsync(_stop.Token), CancellationToken.None);
        return _link.GcsSide.OpenAsync(cancellationToken);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        _link.GcsSide.SendAsync(data, cancellationToken);

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        _link.GcsSide.ReceiveAsync(buffer, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_running is not null)
        {
            await _running;
        }

        _stop.Dispose();
    }
}
