using Gcs.Application.Abstractions;
using Gcs.Domain.Vehicles.Connections;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Gcs.Infrastructure.Health;

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>The oldest unpublished event may wait this long before the outbox is reported Degraded.</summary>
    public int OutboxDegradedAfterSeconds { get; init; } = 60;

    /// <summary>... and this long before it is Unhealthy (consumers are missing events).</summary>
    public int OutboxUnhealthyAfterSeconds { get; init; } = 600;
}

/// <summary>
/// "Are the vehicles we should be talking to actually talking to us?" A lost or faulted link makes the GCS Degraded,
/// not Unhealthy: the API itself still works, and restarting it would not bring a vehicle back. Monitoring alerts on it.
/// </summary>
internal sealed class VehicleLinksHealthCheck(IVehicleLinkManager links) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var all = links.GetAll();
        var data = new Dictionary<string, object>
        {
            ["total"] = all.Count,
            ["connected"] = all.Count(l => l.State == ConnectionState.Connected),
        };

        var troubled = all.Where(l => l.State is ConnectionState.Reconnecting or ConnectionState.Faulted or ConnectionState.Connecting).ToList();
        foreach (var link in troubled)
        {
            data[link.VehicleId.Value.ToString()] = link.FaultReason is null ? link.State.ToString() : $"{link.State}: {link.FaultReason}";
        }

        return Task.FromResult(troubled.Count == 0
            ? HealthCheckResult.Healthy($"{all.Count} link(s), all connected.", data)
            : HealthCheckResult.Degraded($"{troubled.Count} of {all.Count} link(s) are not connected.", data: data));
    }
}

/// <summary>
/// The outbox should drain within seconds. Events piling up means the broker is down or the dispatcher is stuck;
/// nothing is lost (they wait in PostgreSQL), but other systems are getting stale information.
/// </summary>
internal sealed class OutboxBacklogHealthCheck(IOutboxStore outbox, IOptions<MonitoringOptions> options, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var backlog = await outbox.GetBacklogAsync(cancellationToken);
        var age = backlog.OldestOccurredAt is { } oldest ? clock.GetUtcNow() - oldest : TimeSpan.Zero;
        var data = new Dictionary<string, object> { ["pending"] = backlog.Pending, ["oldestAgeSeconds"] = Math.Round(age.TotalSeconds) };
        var settings = options.Value;

        if (age > TimeSpan.FromSeconds(settings.OutboxUnhealthyAfterSeconds))
        {
            return HealthCheckResult.Unhealthy($"{backlog.Pending} event(s) waiting, the oldest for {age.TotalMinutes:0} min.", data: data);
        }

        return age > TimeSpan.FromSeconds(settings.OutboxDegradedAfterSeconds)
            ? HealthCheckResult.Degraded($"{backlog.Pending} event(s) waiting, the oldest for {age.TotalSeconds:0} s.", data: data)
            : HealthCheckResult.Healthy($"{backlog.Pending} event(s) waiting.", data);
    }
}
