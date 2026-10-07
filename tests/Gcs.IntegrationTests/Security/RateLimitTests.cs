using System.Net;
using System.Net.Http.Json;
using Gcs.Contracts.Auth;
using Gcs.Contracts.Commands;
using Gcs.IntegrationTests.Infrastructure;

namespace Gcs.IntegrationTests.Security;

/// <summary>
/// Rate limits with tiny budgets. The limiter answers before the endpoint runs, so most of these tests need no database:
/// they use the factory whose dependencies are unreachable.
/// </summary>
public sealed class RateLimitTests(UnreachableDependenciesApiFactory factory) : IClassFixture<UnreachableDependenciesApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_attempts_are_limited_per_address_with_a_retry_after_hint()
    {
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:AuthPerMinute", "3"));
        using var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("guesser", $"guess-number-{i:D4}"), Ct);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter.ShouldNotBeNull();
                (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("http.rate_limited");
            }
        }

        statuses[..3].ShouldNotContain(HttpStatusCode.TooManyRequests);
        statuses[3].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_global_limit_applies_to_every_endpoint_but_not_to_health_probes()
    {
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:GlobalPerMinute", "2"));
        using var client = limited.CreateClient();

        var info = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            info.Add((await client.GetAsync(new Uri("/api/v1/system/info", UriKind.Relative), Ct)).StatusCode);
        }

        var health = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);

        info.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

/// <summary>The per-user command budget needs a signed-in operator, so it runs against the full API.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CommandRateLimitTests(GcsApiFactory factory)
{
    [Fact]
    public async Task Commands_are_limited_per_user()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:CommandsPerMinute", "2"));
        var users = new TestUsers(limited);
        using var pilot = users.ClientFor("rate-pilot", Roles.Operator);
        using var other = users.ClientFor("rate-other", Roles.Operator);
        var path = $"/api/v1/vehicles/{Guid.NewGuid()}/commands";

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await pilot.PostAsJsonAsync(path, new SendCommandRequest("Land"), ct)).StatusCode);
        }

        var otherUser = await other.PostAsJsonAsync(path, new SendCommandRequest("Land"), ct);

        statuses.ShouldBe([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests]);
        otherUser.StatusCode.ShouldBe(HttpStatusCode.NotFound); // a separate budget per user
    }
}
