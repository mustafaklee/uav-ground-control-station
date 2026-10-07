using Gcs.Application;
using Gcs.Application.Abstractions;
using Gcs.Telemetry.History;
using Gcs.Telemetry.Live;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gcs.Telemetry;

public static class DependencyInjection
{
    public static IServiceCollection AddTelemetry(this IServiceCollection services)
    {
        services.AddOptions<TelemetryOptions>()
            .BindConfiguration(TelemetryOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<LatestTelemetryStore>();
        services.AddSingleton<ITelemetryService>(sp => sp.GetRequiredService<LatestTelemetryStore>());
        services.AddSingleton<TelemetryHistoryBuffer>();
        services.AddSingleton<ITelemetrySink, TelemetryPipeline>();

        services.AddHostedService<TelemetryBroadcaster>();
        services.AddHostedService<TelemetryHistoryWriter>();
        services.AddHostedService<TelemetryRetentionService>();
        services.AddSingleton<TelemetryHistoryHealthCheck>();
        services.AddHealthChecks().AddCheck<TelemetryHistoryHealthCheck>("telemetry-history", tags: [HealthCheckTags.Monitoring]);
        return services;
    }
}
