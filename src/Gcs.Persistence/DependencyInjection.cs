using Gcs.Application;
using Gcs.Application.Abstractions;
using Gcs.Persistence.Commands;
using Gcs.Persistence.Missions;
using Gcs.Persistence.Outbox;
using Gcs.Persistence.Telemetry;
using Gcs.Persistence.Users;
using Gcs.Persistence.Vehicles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gcs.Persistence;

public static partial class DependencyInjection
{
    public const string HealthCheckName = "postgres";

    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PersistenceOptions>()
            .BindConfiguration(PersistenceOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString(PersistenceOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{PersistenceOptions.ConnectionStringName}' is not configured.");
        }

        services.AddDbContext<GcsDbContext>((serviceProvider, options) =>
        {
            var persistence = serviceProvider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            Configure(options, connectionString, persistence.CommandTimeoutSeconds, persistence.MaxRetryCount);
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IVehicleRepository, VehicleRepository>();
        services.AddScoped<IVehicleQueries, VehicleQueries>();
        services.AddScoped<IMissionRepository, MissionRepository>();
        services.AddScoped<IMissionQueries, MissionQueries>();
        services.AddScoped<ICommandAuditLog, CommandAuditLog>();
        services.AddScoped<ICommandAuditQueries, CommandAuditQueries>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IOutboxStore, OutboxStore>();
        services.AddScoped<IEventOutbox, EventOutbox>();
        services.AddScoped<ITelemetryHistoryStore, TelemetryHistoryStore>();

        services.AddHealthChecks()
            .AddDbContextCheck<GcsDbContext>(HealthCheckName, tags: [HealthCheckTags.Ready]);

        return services;
    }

    /// <summary>Applies pending migrations if <see cref="PersistenceOptions.ApplyMigrationsOnStartup"/> is enabled.</summary>
    public static async Task MigrateDatabaseIfEnabledAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<PersistenceOptions>>().Value;
        if (!options.ApplyMigrationsOnStartup)
        {
            return;
        }

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DependencyInjection));
        var db = scope.ServiceProvider.GetRequiredService<GcsDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        LogApplyingMigrations(logger, pending.Count);
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Shared by the runtime registration and the design-time factory used by <c>dotnet ef</c>.</summary>
    internal static void Configure(DbContextOptionsBuilder options, string connectionString, int commandTimeoutSeconds, int maxRetryCount)
    {
        options
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.CommandTimeout(commandTimeoutSeconds);
                npgsql.EnableRetryOnFailure(maxRetryCount);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", GcsDbContext.Schema);
            })
            .UseSnakeCaseNamingConvention();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} pending database migration(s) on startup")]
    private static partial void LogApplyingMigrations(ILogger logger, int count);
}
