using Gcs.Messaging;
using Gcs.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gcs.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Composition root for all adapters (database, broker, later MAVLink and telemetry). The API calls only this method,
    /// so it never has to know about EF Core or RabbitMQ types directly.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPersistence(configuration);
        services.AddMessaging();
        return services;
    }
}
