using System.Threading.Channels;
using Gcs.Application.Abstractions;
using Gcs.Application.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gcs.Infrastructure.LinkEvents;

/// <summary>
/// Fans out link state changes: to operator clients (live push) and, for meaningful transitions, to the outbox
/// (→ RabbitMQ: VehicleConnected, VehicleLinkLost, VehicleLinkFaulted, VehicleDisconnected).
/// The MAVLink layer only enqueues; delivery happens here, on a background loop, so a slow database or client
/// can never stall a vehicle link.
/// </summary>
internal sealed partial class VehicleLinkEventDispatcher(
    IServiceScopeFactory scopes,
    ILiveUpdatePublisher publisher,
    TimeProvider time,
    ILogger<VehicleLinkEventDispatcher> logger) : BackgroundService, IVehicleLinkEventSink
{
    private const int QueueCapacity = 10_000;

    private readonly Channel<(VehicleLinkStatus Status, ConnectionState Previous)> _queue =
        Channel.CreateBounded<(VehicleLinkStatus, ConnectionState)>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public void StateChanged(VehicleLinkStatus status, ConnectionState previous) => _queue.Writer.TryWrite((status, previous));

    /// <summary>Maps a transition to the integration event other services care about, if any.</summary>
    internal static IDomainEvent? ToEvent(VehicleLinkStatus status, ConnectionState previous, DateTimeOffset at) =>
        (previous, status.State) switch
        {
            (not ConnectionState.Connected, ConnectionState.Connected) => new VehicleConnected(status.VehicleId, at),
            (ConnectionState.Connected, ConnectionState.Reconnecting) => new VehicleLinkLost(status.VehicleId, "Heartbeat timeout.", at),
            (_, ConnectionState.Faulted) => new VehicleLinkFaulted(status.VehicleId, status.FaultReason ?? "Unknown.", at),
            (not ConnectionState.Disconnected, ConnectionState.Disconnected) => new VehicleDisconnected(status.VehicleId, at),
            _ => null,
        };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (status, previous) in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await DeliverAsync(status, previous, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task DeliverAsync(VehicleLinkStatus status, ConnectionState previous, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishLinkStatusAsync(VehicleLinkMapping.ToResponse(status.VehicleId, status), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPushFailed(ex, status.VehicleId.Value);
        }

        if (ToEvent(status, previous, time.GetUtcNow()) is not { } linkEvent)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IEventOutbox>().EnqueueAsync([linkEvent], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database is down: the live push already happened; the integration event is lost and logged.
            LogOutboxFailed(ex, linkEvent.GetType().Name, status.VehicleId.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pushing link status of vehicle {VehicleId} failed")]
    private partial void LogPushFailed(Exception exception, Guid vehicleId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing {EventType} for vehicle {VehicleId} to the outbox failed")]
    private partial void LogOutboxFailed(Exception exception, string eventType, Guid vehicleId);
}
