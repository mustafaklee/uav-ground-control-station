using System.Net;
using System.Net.Http.Json;
using Gcs.Contracts.Auth;
using Gcs.IntegrationTests.Infrastructure;

namespace Gcs.IntegrationTests.Security;

/// <summary>
/// The authorization matrix, endpoint by endpoint and role by role. "Allowed" means the request got past authorization:
/// any answer except 401/403 (usually 404 or 400, because the ids are random and the bodies empty, so nothing changes).
/// If an endpoint is added without a policy, the fallback policy still demands sign-in, and this table must be extended.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class AuthorizationMatrixTests(GcsApiFactory factory)
{
    private static readonly string Id = Guid.NewGuid().ToString();
    private static readonly string[] AllRoles = [Roles.Observer, Roles.Operator, Roles.Maintenance, Roles.Administrator];
    private static readonly string[] Everyone = AllRoles;
    private static readonly string[] Pilots = [Roles.Operator, Roles.Administrator];
    private static readonly string[] Linkers = [Roles.Operator, Roles.Maintenance, Roles.Administrator];
    private static readonly string[] Fleet = [Roles.Maintenance, Roles.Administrator];
    private static readonly string[] Admins = [Roles.Administrator];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Method, path, roles that may call it.</summary>
    public static TheoryData<string, string, string[]> Endpoints => new()
    {
        { "GET", "/api/v1/vehicles", Everyone },
        { "GET", $"/api/v1/vehicles/{Id}", Everyone },
        { "POST", "/api/v1/vehicles", Fleet },
        { "PUT", $"/api/v1/vehicles/{Id}", Fleet },
        { "DELETE", $"/api/v1/vehicles/{Id}", Fleet },
        { "GET", $"/api/v1/vehicles/{Id}/connection", Everyone },
        { "POST", $"/api/v1/vehicles/{Id}/connection", Linkers },
        { "DELETE", $"/api/v1/vehicles/{Id}/connection", Linkers },
        { "GET", $"/api/v1/vehicles/{Id}/telemetry", Everyone },
        { "GET", $"/api/v1/vehicles/{Id}/telemetry/history", Everyone },
        { "GET", "/api/v1/network/topology", Everyone },
        { "GET", "/api/v1/missions", Everyone },
        { "GET", $"/api/v1/missions/{Id}", Everyone },
        { "POST", "/api/v1/missions", Pilots },
        { "PUT", $"/api/v1/missions/{Id}", Pilots },
        { "DELETE", $"/api/v1/missions/{Id}", Pilots },
        { "POST", $"/api/v1/missions/{Id}/upload", Pilots },
        { "GET", $"/api/v1/vehicles/{Id}/mission", Pilots },
        { "GET", $"/api/v1/vehicles/{Id}/command-lease", Everyone },
        { "POST", $"/api/v1/vehicles/{Id}/command-lease", Pilots },
        { "DELETE", $"/api/v1/vehicles/{Id}/command-lease", Pilots },
        { "GET", $"/api/v1/vehicles/{Id}/flight-modes", Everyone },
        { "POST", $"/api/v1/vehicles/{Id}/commands", Pilots },
        { "GET", $"/api/v1/vehicles/{Id}/commands", Everyone },
        { "GET", "/api/v1/auth/me", Everyone },
        { "GET", "/api/v1/users", Admins },
        { "GET", $"/api/v1/users/{Id}", Admins },
        { "POST", "/api/v1/users", Admins },
        { "PUT", $"/api/v1/users/{Id}", Admins },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Each_role_can_call_exactly_the_endpoints_its_permissions_allow(string method, string path, string[] allowed)
    {
        foreach (var role in AllRoles)
        {
            using var client = factory.Users.ClientFor($"matrix-{role.ToLowerInvariant()}", role);
            var response = await SendAsync(client, method, path);

            if (allowed.Contains(role))
            {
                response.StatusCode.ShouldNotBeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden], $"{role} {method} {path}");
            }
            else
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{role} {method} {path}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Without_a_token_every_endpoint_answers_401(string method, string path, string[] allowed)
    {
        allowed.ShouldNotBeEmpty();
        using var anonymous = factory.CreateClient();

        var response = await SendAsync(anonymous, method, path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{method} {path}");
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/api/v1/system/info")]
    public async Task Probes_and_service_info_stay_anonymous(string path)
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        return client.SendAsync(request, Ct);
    }
}
