using Gcs.Application.Abstractions;
using Gcs.Contracts.Vehicles;

namespace Gcs.Infrastructure.LinkEvents;

/// <summary>Used when the host has no live channel (e.g. a worker without SignalR). The API replaces it.</summary>
internal sealed class NullLiveUpdatePublisher : ILiveUpdatePublisher
{
    public Task PublishTelemetryAsync(TelemetryResponse telemetry, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishLinkStatusAsync(VehicleLinkResponse status, CancellationToken cancellationToken) => Task.CompletedTask;
}
