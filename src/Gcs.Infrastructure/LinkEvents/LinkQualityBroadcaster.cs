using Gcs.Application.Abstractions;
using Gcs.Application.Vehicles;
using Gcs.Domain.Vehicles.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gcs.Infrastructure.LinkEvents;

/// <summary>
/// Pushes the link quality of every active link to operator clients every <see cref="Interval"/> (ADR-019). State changes
/// are pushed the moment they happen by <see cref="VehicleLinkEventDispatcher"/>; quality changes continuously, so it is
/// sampled instead: one small message per vehicle every two seconds.
/// </summary>
internal sealed partial class LinkQualityBroadcaster(
    IVehicleLinkManager links,
    ILiveUpdatePublisher live,
    TimeProvider time,
    ILogger<LinkQualityBroadcaster> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await BroadcastAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    internal async Task BroadcastAsync(CancellationToken cancellationToken)
    {
        foreach (var status in links.GetAll().Where(s => s.State != ConnectionState.Disconnected))
        {
            try
            {
                await live.PublishLinkQualityAsync(VehicleLinkMapping.ToResponse(status.VehicleId, status), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed push must not stop the others or the loop; the next tick tries again.
                LogPublishFailed(ex, status.VehicleId.Value);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pushing link quality of vehicle {VehicleId} failed")]
    private partial void LogPublishFailed(Exception exception, Guid vehicleId);
}
