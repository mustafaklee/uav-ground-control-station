using System.Text.Json;
using Gcs.Application;
using Gcs.Contracts.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gcs.Api.Endpoints;

internal static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: no dependency checks. A database outage must not make the orchestrator restart a healthy process.
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteResponseAsync,
        });

        // Readiness: the instance can serve traffic only when PostgreSQL and RabbitMQ are reachable.
        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HealthCheckTags.Ready),
            ResponseWriter = WriteResponseAsync,
        });

        return endpoints;
    }

    private static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        var response = new HealthReportResponse(
            report.Status.ToString(),
            report.TotalDuration.TotalMilliseconds,
            [.. report.Entries.Select(entry => new HealthCheckEntryResponse(
                entry.Key,
                entry.Value.Status.ToString(),
                entry.Value.Duration.TotalMilliseconds,
                entry.Value.Description))]);

        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(context.Response.Body, response, JsonOptions, context.RequestAborted);
    }
}
