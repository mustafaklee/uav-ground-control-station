using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Gcs.Contracts.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gcs.IntegrationTests.Infrastructure;

/// <summary>
/// Test-only secrets, generated per test run: nothing here is a real credential, and nothing is stored in the repo.
/// </summary>
internal static class TestSecrets
{
    public const string AdminUsername = "admin";
    public const string Password = "integration-test-password";

    public static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}

internal static class TestSecurity
{
    public static void Configure(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SigningKey", TestSecrets.SigningKey);
        builder.UseSetting("Security:BootstrapAdministrator:Username", TestSecrets.AdminUsername);
        builder.UseSetting("Security:BootstrapAdministrator:Password", TestSecrets.Password);
        builder.UseSetting("RateLimiting:GlobalPerMinute", "100000");
        builder.UseSetting("RateLimiting:AuthPerMinute", "10000");
        builder.UseSetting("RateLimiting:CommandsPerMinute", "10000");
    }
}

/// <summary>
/// Signs test clients in as users with a given role. Users are created on first use through the real user API
/// (as the bootstrap administrator), so the tests exercise exactly the flow an administrator would.
/// </summary>
public sealed class TestUsers(WebApplicationFactory<Program> factory)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _tokens = new();

    /// <summary>A client that sends a bearer token for <paramref name="username"/>, created with <paramref name="role"/> if needed.</summary>
    public HttpClient ClientFor(string username, string role)
    {
        var client = factory.CreateDefaultClient(new BearerHandler(() => TokenAsync(username, role)));
        return client;
    }

    public Task<string> TokenAsync(string username, string role) =>
        _tokens.GetOrAdd($"{username}|{role}", _ => new Lazy<Task<string>>(() => SignInAsync(username, role))).Value;

    private async Task<string> SignInAsync(string username, string role)
    {
        using var anonymous = factory.CreateClient();
        if (username != TestSecrets.AdminUsername)
        {
            var adminToken = await TokenAsync(TestSecrets.AdminUsername, Roles.Administrator);
            using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users")
            {
                Content = JsonContent.Create(new CreateUserRequest(username, TestSecrets.Password, role)),
            };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var created = await anonymous.SendAsync(create);
            if (created.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
            {
                throw new InvalidOperationException($"Creating test user {username} failed: {created.StatusCode} {await created.Content.ReadAsStringAsync()}");
            }
        }

        // The bootstrap administrator is created in the background at startup: wait for it.
        for (var attempt = 0; ; attempt++)
        {
            var login = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(username, TestSecrets.Password));
            if (login.IsSuccessStatusCode)
            {
                return (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
            }

            if (attempt >= 50)
            {
                throw new InvalidOperationException($"Login as {username} failed: {login.StatusCode} {await login.Content.ReadAsStringAsync()}");
            }

            await Task.Delay(200);
        }
    }

    private sealed class BearerHandler(Func<Task<string>> token) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization ??= new AuthenticationHeaderValue("Bearer", await token());
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
