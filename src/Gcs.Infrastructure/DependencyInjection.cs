using Gcs.Application.Abstractions;
using Gcs.Infrastructure.LinkEvents;
using Gcs.Infrastructure.Outbox;
using Gcs.Mavlink;
using Gcs.Messaging;
using Gcs.Persistence;
using Gcs.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gcs.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Composition root for all adapters (database, broker, MAVLink, telemetry). The API calls only this method,
    /// so it never has to know about EF Core or RabbitMQ types directly.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPersistence(configuration);
        services.AddMessaging();
        services.AddTelemetry();
        services.AddMavlink();

        services.AddOptions<OutboxDispatcherOptions>()
            .BindConfiguration(OutboxDispatcherOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHostedService<OutboxDispatcher>();

        // Link state changes: one dispatcher instance is both the sink the MAVLink layer writes to and the background loop.
        services.TryAddSingleton<ILiveUpdatePublisher, NullLiveUpdatePublisher>();
        services.AddSingleton<VehicleLinkEventDispatcher>();
        services.AddSingleton<IVehicleLinkEventSink>(sp => sp.GetRequiredService<VehicleLinkEventDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<VehicleLinkEventDispatcher>());
        services.AddHostedService<VehicleLinkRestorer>();

        return services;
    }

    /// <summary>Startup work that must finish before the API accepts requests (e.g. development migrations).</summary>
    public static Task InitializeInfrastructureAsync(this IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateDatabaseIfEnabledAsync(cancellationToken);
}
