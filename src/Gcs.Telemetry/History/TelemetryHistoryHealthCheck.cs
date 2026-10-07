using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gcs.Telemetry.History;

/// <summary>
/// History samples dropped since the previous check mean the database cannot keep up (or is down). Live telemetry is
/// unaffected, so this is Degraded: an operator still flies, but the flight record has gaps.
/// </summary>
internal sealed class TelemetryHistoryHealthCheck(TelemetryHistoryBuffer buffer) : IHealthCheck
{
    private long _lastDropped;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var dropped = buffer.DroppedSamples;
        var sinceLastCheck = dropped - Interlocked.Exchange(ref _lastDropped, dropped);
        var data = new Dictionary<string, object> { ["droppedTotal"] = dropped, ["droppedSinceLastCheck"] = sinceLastCheck };
        return Task.FromResult(sinceLastCheck > 0
            ? HealthCheckResult.Degraded($"{sinceLastCheck} history sample(s) dropped since the last check.", data: data)
            : HealthCheckResult.Healthy("History is being written.", data));
    }
}
