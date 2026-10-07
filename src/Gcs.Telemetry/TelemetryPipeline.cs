using Gcs.Application.Abstractions;
using Gcs.Domain.Telemetry;
using Gcs.Domain.Vehicles;
using Gcs.Telemetry.History;

namespace Gcs.Telemetry;

/// <summary>
/// Entry point for decoded telemetry (the link layer's <see cref="ITelemetrySink"/>):
/// <code>
/// update ─► latest-state store (always, in memory)
///        └► history buffer     (sampled, queued, written to PostgreSQL in batches)
/// Live push to clients happens separately in TelemetryBroadcaster, throttled.
/// </code>
/// Everything here is non-blocking, because it runs on the vehicle link's receive loop.
/// </summary>
internal sealed class TelemetryPipeline(LatestTelemetryStore store, TelemetryHistoryBuffer history) : ITelemetrySink
{
    public void Publish(VehicleId vehicleId, TelemetryUpdate update)
    {
        var snapshot = store.Apply(vehicleId, update);
        history.Offer(snapshot);
    }
}
