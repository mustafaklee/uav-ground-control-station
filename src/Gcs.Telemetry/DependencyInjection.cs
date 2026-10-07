using Gcs.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Gcs.Telemetry;

public static class DependencyInjection
{
    public static IServiceCollection AddTelemetry(this IServiceCollection services)
    {
        // One store instance serves both roles: the link layer writes, the API reads.
        services.AddSingleton<LatestTelemetryStore>();
        services.AddSingleton<ITelemetrySink>(sp => sp.GetRequiredService<LatestTelemetryStore>());
        services.AddSingleton<ITelemetryService>(sp => sp.GetRequiredService<LatestTelemetryStore>());
        return services;
    }
}
