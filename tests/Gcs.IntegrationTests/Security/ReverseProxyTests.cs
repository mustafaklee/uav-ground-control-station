using System.Net;
using System.Net.Http.Json;
using Gcs.Contracts.Auth;
using Gcs.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Gcs.IntegrationTests.Security;

/// <summary>
/// Behind Nginx every connection comes from the proxy. The per-address login limit must still see each client separately,
/// and only a trusted proxy may say who the client is.
/// </summary>
public sealed class ReverseProxyTests(UnreachableDependenciesApiFactory factory) : IClassFixture<UnreachableDependenciesApiFactory>
{
    private static readonly IPAddress Proxy = IPAddress.Parse("172.30.0.10");
    private static readonly IPAddress Stranger = IPAddress.Parse("192.0.2.9");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Behind_a_trusted_proxy_each_client_address_gets_its_own_login_budget()
    {
        await using var api = BehindProxy(connectingFrom: Proxy);
        using var client = api.CreateClient();

        var first = await LoginStatusesAsync(client, forwardedFor: "203.0.113.1", attempts: 3);
        var second = await LoginStatusesAsync(client, forwardedFor: "203.0.113.2", attempts: 1);

        first[..2].ShouldNotContain(HttpStatusCode.TooManyRequests);
        first[2].ShouldBe(HttpStatusCode.TooManyRequests);
        second[0].ShouldNotBe(HttpStatusCode.TooManyRequests); // a different client, a fresh budget
    }

    [Fact]
    public async Task Forwarded_headers_from_an_untrusted_address_are_ignored()
    {
        await using var api = BehindProxy(connectingFrom: Stranger);
        using var client = api.CreateClient();

        // Changing the header on every attempt would dodge the limit if the API believed it.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.AddRange(await LoginStatusesAsync(client, forwardedFor: $"203.0.113.{i + 10}", attempts: 1));
        }

        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private WebApplicationFactory<Program> BehindProxy(IPAddress connectingFrom) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("RateLimiting:AuthPerMinute", "2");
        b.UseSetting("ReverseProxy:Enabled", "true");
        b.UseSetting("ReverseProxy:TrustedNetworks:0", "172.30.0.0/24");
        b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new RemoteAddressFilter(connectingFrom)));
    });

    private static async Task<List<HttpStatusCode>> LoginStatusesAsync(HttpClient client, string forwardedFor, int attempts)
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < attempts; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest("guesser", $"guess-number-{i:D4}")),
            };
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            request.Headers.Add("X-Forwarded-Proto", "https");
            statuses.Add((await client.SendAsync(request, Ct)).StatusCode);
        }

        return statuses;
    }

    /// <summary>The in-memory test server has no TCP connection; this plays the address the request arrives from.</summary>
    private sealed class RemoteAddressFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, RequestDelegate nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
