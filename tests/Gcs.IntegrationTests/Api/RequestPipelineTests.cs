using System.Net;
using System.Net.Http.Json;
using Gcs.Contracts.Diagnostics;
using Gcs.IntegrationTests.Infrastructure;

namespace Gcs.IntegrationTests.Api;

/// <summary>
/// Cross-cutting HTTP behaviour that does not need real dependencies, so it runs against the lightweight factory.
/// </summary>
public sealed class RequestPipelineTests(UnreachableDependenciesApiFactory factory)
    : IClassFixture<UnreachableDependenciesApiFactory>
{
    private const string CorrelationHeader = "X-Correlation-ID";

    [Fact]
    public async Task Supplied_correlation_id_is_echoed_back()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(CorrelationHeader, "test-correlation-42");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues(CorrelationHeader).ShouldBe(["test-correlation-42"]);
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_missing()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        response.Headers.GetValues(CorrelationHeader).Single().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Versioned_system_info_endpoint_returns_service_metadata()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/system/info", UriKind.Relative), TestContext.Current.CancellationToken);
        var info = await response.Content.ReadFromJsonAsync<SystemInfoResponse>(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        info.ShouldNotBeNull();
        info.Service.ShouldBe("gcs-api");
        info.Environment.ShouldBe(TestEnvironments.Testing);
        response.Headers.GetValues("api-supported-versions").ShouldContain("1.0");
    }

    [Fact]
    public async Task Unknown_api_version_is_rejected()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v99/system/info", UriKind.Relative), TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeFalse();
    }
}
