using Gcs.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Gcs.Messaging;

public static class DependencyInjection
{
    public const string HealthCheckName = "rabbitmq";

    public static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddOptions<RabbitMqOptions>()
            .BindConfiguration(RabbitMqOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();

        services.AddHealthChecks()
            .AddCheck<RabbitMqHealthCheck>(HealthCheckName, tags: [HealthCheckTags.Ready]);

        return services;
    }
}
