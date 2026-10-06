namespace Gcs.Simulation;

/// <summary>
/// Placeholder host for the simulated vehicle. In Phase 3 it will emit MAVLink HEARTBEAT and telemetry over UDP
/// so the GCS can be developed and tested without PX4 SITL or real hardware.
/// </summary>
internal sealed partial class SimulatorHost(ILogger<SimulatorHost> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted();
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Vehicle simulator started (MAVLink output arrives in Phase 3)")]
    private partial void LogStarted();
}
