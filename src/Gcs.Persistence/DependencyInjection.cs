using Gcs.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gcs.Persistence;

public static class DependencyInjection
{
    public const string HealthCheckName = "postgres";

    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PersistenceOptions>()
            .BindConfiguration(PersistenceOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{PersistenceOptions.ConnectionStringName}' is not configured.");

        services.AddDbContext<GcsDbContext>((serviceProvider, options) =>
        {
            var persistence = serviceProvider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.CommandTimeout(persistence.CommandTimeoutSeconds);
                npgsql.EnableRetryOnFailure(persistence.MaxRetryCount);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", GcsDbContext.Schema);
            });
        });

        services.AddHealthChecks()
            .AddDbContextCheck<GcsDbContext>(HealthCheckName, tags: [HealthCheckTags.Ready]);

        return services;
    }
}
