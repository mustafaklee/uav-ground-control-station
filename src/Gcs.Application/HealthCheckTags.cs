namespace Gcs.Application;

/// <summary>
/// Tags used to group health checks. Liveness only says "the process is running";
/// readiness also checks external dependencies (database, broker) before traffic is sent to the instance.
/// </summary>
public static class HealthCheckTags
{
    public const string Ready = "ready";

    /// <summary>
    /// Operational checks (vehicle links, outbox backlog, history writes). They report Degraded, never block traffic,
    /// and appear only on the authenticated <c>/health/details</c> endpoint.
    /// </summary>
    public const string Monitoring = "monitoring";
}
