using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Gcs.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real API in memory against real PostgreSQL and RabbitMQ containers.
/// Requires a running Docker engine (Docker Desktop locally, the GitHub runner in CI).
/// Migrations are applied on startup, exactly like local development.
/// </summary>
public sealed class GcsApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(ContainerImages.Postgres).Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder(ContainerImages.RabbitMq).Build();

    public GcsApiFactory()
    {
        Users = new TestUsers(this);
    }

    /// <summary>Signs clients in as users of any role (see <see cref="TestUsers"/>).</summary>
    public TestUsers Users { get; }

    /// <summary>A client signed in as the bootstrap administrator, who has every permission.</summary>
    public HttpClient CreateAdminClient() => Users.ClientFor(TestSecrets.AdminUsername, Gcs.Contracts.Auth.Roles.Administrator);

    /// <summary>AMQP URI of the test broker, for tests that consume published events.</summary>
    public Uri RabbitMqUri => new(_rabbitMq.GetConnectionString());

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var rabbitUri = RabbitMqUri;
        var credentials = rabbitUri.UserInfo.Split(':', 2);

        builder.UseEnvironment(TestEnvironments.Testing);
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("Persistence:ApplyMigrationsOnStartup", "true");
        builder.UseSetting("Outbox:PollingIntervalMilliseconds", "200");

        // Security: a per-run signing key and bootstrap administrator; limits high enough for the whole suite.
        TestSecurity.Configure(builder);

        // Short link timings so loss detection and bounded retries finish within seconds.
        builder.UseSetting("Mavlink:HeartbeatTimeoutMilliseconds", "1500");
        builder.UseSetting("Mavlink:ConnectTimeoutMilliseconds", "5000");
        builder.UseSetting("Mavlink:MaxReconnectAttempts", "2");
        builder.UseSetting("Mavlink:ReconnectBaseDelayMilliseconds", "200");
        builder.UseSetting("Mavlink:WatchdogIntervalMilliseconds", "100");

        // Commands time out quickly: 3 attempts x 0.3 s.
        builder.UseSetting("Mavlink:CommandAckTimeoutMilliseconds", "300");
        builder.UseSetting("Mavlink:CommandMaxRetries", "2");

        // Sample and flush history quickly so tests see stored samples within seconds.
        builder.UseSetting("Telemetry:HistorySampleIntervalMilliseconds", "200");
        builder.UseSetting("Telemetry:HistoryFlushIntervalMilliseconds", "300");
        builder.UseSetting("RabbitMq:HostName", rabbitUri.Host);
        builder.UseSetting("RabbitMq:Port", rabbitUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("RabbitMq:UserName", Uri.UnescapeDataString(credentials[0]));
        builder.UseSetting("RabbitMq:Password", Uri.UnescapeDataString(credentials[1]));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbitMq.DisposeAsync();
    }
}

/// <summary>All test classes in this collection share one API instance and one set of containers.</summary>
[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<GcsApiFactory>
{
    public const string Name = "api";
}
