using System.Diagnostics;
using System.Reflection;
using Gcs.Application.Diagnostics;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace Gcs.Api.Observability;

/// <summary>
/// Where traces, metrics and logs go. Without an OTLP endpoint everything is still instrumented (and visible to
/// in-process listeners and tests) but nothing is exported. With one, all three signals go to any OpenTelemetry
/// backend: the Aspire dashboard in docker-compose, or Jaeger/Prometheus/Grafana/Seq behind a collector later.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    public string ServiceName { get; set; } = "gcs-api";

    /// <summary>OTLP/gRPC endpoint, e.g. <c>http://localhost:4317</c>. Empty: do not export.</summary>
    public string? OtlpEndpoint { get; set; }

    public Uri? OtlpUri => Uri.TryCreate(OtlpEndpoint, UriKind.Absolute, out var uri) ? uri : null;

    public static string ServiceVersion =>
        typeof(ObservabilityOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}

public static class ObservabilityExtensions
{
    public static ObservabilityOptions GetObservabilityOptions(this IConfiguration configuration) =>
        configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();

    public static IServiceCollection AddGcsObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetObservabilityOptions();
        var otlp = options.OtlpUri;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                options.ServiceName, serviceVersion: ObservabilityOptions.ServiceVersion, serviceInstanceId: Environment.MachineName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(GcsTracing.SourceName)
                    .AddAspNetCoreInstrumentation(aspnet =>
                    {
                        // Probes run every few seconds; tracing them only buries the interesting requests.
                        aspnet.Filter = http => !http.Request.Path.StartsWithSegments("/health");
                        aspnet.RecordException = true;
                    })
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .SetSampler(new ParentBasedSampler(new BackgroundNoiseSampler()));
                if (otlp is not null)
                {
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = otlp);
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(GcsMetrics.MeterName, "Npgsql")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (otlp is not null)
                {
                    metrics.AddOtlpExporter(exporter => exporter.Endpoint = otlp);
                }
            });

        return services;
    }

    /// <summary>Logs to OTLP as well, so the dashboard shows each span's log lines next to it.</summary>
    public static LoggerConfiguration WriteToOtlpIfConfigured(this LoggerConfiguration logger, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var options = configuration.GetObservabilityOptions();
        return options.OtlpUri is not { } otlp
            ? logger
            : logger.WriteTo.OpenTelemetry(sink =>
            {
                sink.Endpoint = otlp.ToString();
                sink.Protocol = OtlpProtocol.Grpc;
                sink.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = options.ServiceName,
                    ["service.version"] = ObservabilityOptions.ServiceVersion,
                };
            });
    }
}

/// <summary>
/// Drops traces that would start with a database or HTTP client call. Those come from background loops (outbox polling
/// every second, telemetry history batches) and would flood the trace view with thousands of one-span traces.
/// Work worth seeing in the background starts its own parent span first (e.g. "VehicleRegistered publish"), and its
/// children are kept because the parent-based sampler follows the parent's decision.
/// </summary>
internal sealed class BackgroundNoiseSampler : Sampler
{
    private static readonly SamplingResult Drop = new(SamplingDecision.Drop);
    private static readonly SamplingResult Keep = new(SamplingDecision.RecordAndSample);

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.Kind == ActivityKind.Client ? Drop : Keep;
}
