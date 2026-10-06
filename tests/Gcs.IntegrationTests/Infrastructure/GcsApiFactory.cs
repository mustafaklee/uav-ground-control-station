using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Gcs.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real API in memory against real PostgreSQL and RabbitMQ containers.
/// Requires a running Docker engine (Docker Desktop locally, the GitHub runner in CI).
/// </summary>
public sealed class GcsApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(ContainerImages.Postgres).Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder(ContainerImages.RabbitMq).Build();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var rabbitUri = new Uri(_rabbitMq.GetConnectionString());
        var credentials = rabbitUri.UserInfo.Split(':', 2);

        builder.UseEnvironment(TestEnvironments.Testing);
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
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
