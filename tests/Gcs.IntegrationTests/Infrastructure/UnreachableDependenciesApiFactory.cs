using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gcs.IntegrationTests.Infrastructure;

/// <summary>
/// API wired to dependencies that do not exist. Used to prove the process still starts and reports
/// "not ready" instead of crashing when PostgreSQL or RabbitMQ is down. Needs no Docker.
/// </summary>
public sealed class UnreachableDependenciesApiFactory : WebApplicationFactory<Program>
{
    // Port 1 (tcpmux) is reserved and never listens on developer machines or CI runners.
    private const string ClosedPort = "1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(TestEnvironments.Testing);
        builder.UseSetting("ConnectionStrings:Postgres", $"Host=127.0.0.1;Port={ClosedPort};Database=gcs;Username=gcs;Password=unused;Timeout=2");
        builder.UseSetting("Persistence:MaxRetryCount", "0");
        builder.UseSetting("RabbitMq:HostName", "127.0.0.1");
        builder.UseSetting("RabbitMq:Port", ClosedPort);
        builder.UseSetting("RabbitMq:UserName", "gcs");
        builder.UseSetting("RabbitMq:Password", "unused");
        builder.UseSetting("RabbitMq:ConnectionTimeoutSeconds", "2");
    }
}
