using System.Net;
using System.Net.Http.Json;
using Gcs.Contracts.Health;
using Gcs.IntegrationTests.Infrastructure;

namespace Gcs.IntegrationTests.Health;

public sealed class UnreachableDependenciesTests(UnreachableDependenciesApiFactory factory)
    : IClassFixture<UnreachableDependenciesApiFactory>
{
    [Fact]
    public async Task Api_stays_alive_when_dependencies_are_down()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_reports_unavailable_when_dependencies_are_down()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        var report = await response.Content.ReadFromJsonAsync<HealthReportResponse>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        report.ShouldNotBeNull();
        report.Status.ShouldBe("Unhealthy");
        report.Checks.ShouldAllBe(c => c.Status == "Unhealthy");
    }
}
