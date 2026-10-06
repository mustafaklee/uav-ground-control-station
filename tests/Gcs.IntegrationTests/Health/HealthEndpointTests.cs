using System.Net;
using System.Net.Http.Json;
using Gcs.Contracts.Health;
using Gcs.IntegrationTests.Infrastructure;

namespace Gcs.IntegrationTests.Health;

public sealed class HealthEndpointTests(GcsApiFactory factory) : IClassFixture<GcsApiFactory>
{
    [Fact]
    public async Task Liveness_is_healthy_without_running_dependency_checks()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        var report = await response.Content.ReadFromJsonAsync<HealthReportResponse>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        report.ShouldNotBeNull();
        report.Status.ShouldBe("Healthy");
        report.Checks.ShouldBeEmpty();
    }

    [Fact]
    public async Task Readiness_is_healthy_when_postgres_and_rabbitmq_are_reachable()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var report = await response.Content.ReadFromJsonAsync<HealthReportResponse>(TestContext.Current.CancellationToken);

        report.ShouldNotBeNull();
        report.Checks.Select(c => (c.Name, c.Status)).ShouldBe(
            [("postgres", "Healthy"), ("rabbitmq", "Healthy")],
            ignoreOrder: true);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
