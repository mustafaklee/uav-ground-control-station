using Gcs.Application.Abstractions;
using Gcs.Application.Vehicles;
using Gcs.Domain.Vehicles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gcs.Telemetry.Live;

/// <summary>
/// Pushes the latest telemetry to subscribed clients at a fixed rate (default 5 Hz per vehicle), and only for vehicles
/// whose snapshot changed since the last push. Decoupled from the receive loop on purpose: a slow or stalled client
/// can delay a push, but never the reception of vehicle messages.
/// </summary>
internal sealed partial class TelemetryBroadcaster(
    LatestTelemetryStore store,
    ILiveUpdatePublisher publisher,
    IOptions<TelemetryOptions> options,
    TimeProvider time,
    ILogger<TelemetryBroadcaster> logger) : BackgroundService
{
    private readonly Dictionary<VehicleId, DateTimeOffset> _lastSent = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.BroadcastIntervalMilliseconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await BroadcastChangesAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <returns>Number of vehicles pushed.</returns>
    internal async Task<int> BroadcastChangesAsync(CancellationToken cancellationToken)
    {
        var pushed = 0;
        foreach (var snapshot in store.All())
        {
            if (_lastSent.TryGetValue(snapshot.VehicleId, out var last) && snapshot.UpdatedAt <= last)
            {
                continue;
            }

            try
            {
                await publisher.PublishTelemetryAsync(TelemetryMapping.ToResponse(snapshot), cancellationToken);
                _lastSent[snapshot.VehicleId] = snapshot.UpdatedAt;
                pushed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPushFailed(ex, snapshot.VehicleId.Value);
            }
        }

        return pushed;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pushing live telemetry for vehicle {VehicleId} failed")]
    private partial void LogPushFailed(Exception exception, Guid vehicleId);
}
