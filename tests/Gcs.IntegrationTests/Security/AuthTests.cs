using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gcs.Contracts.Auth;
using Gcs.Contracts.Commands;
using Gcs.Contracts.Common;
using Gcs.IntegrationTests.Infrastructure;

namespace Gcs.IntegrationTests.Security;

/// <summary>Sign-in, sessions and accounts end to end: password hashing, lockout, refresh rotation, reuse detection.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AuthTests(GcsApiFactory factory)
{
    private const string Password = "a-long-enough-passphrase";
    private readonly HttpClient _admin = factory.CreateAdminClient();
    private readonly HttpClient _anonymous = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_returns_a_short_access_token_a_refresh_token_and_the_user()
    {
        var user = await CreateUserAsync(Roles.Observer);

        var session = await LoginAsync(user.Username, Password);

        session.User.ShouldBe(user with { LastLoginAt = session.User.LastLoginAt });
        session.AccessTokenExpiresAt.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
        session.RefreshTokenExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(11));
        var me = await WithToken(session.AccessToken).GetFromJsonAsync<UserResponse>("/api/v1/auth/me", Ct);
        me!.Username.ShouldBe(user.Username);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_answer()
    {
        var user = await CreateUserAsync(Roles.Observer);

        var wrongPassword = await _anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Username, "not-the-password-at-all"), Ct);
        var unknownUser = await _anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody-" + Guid.NewGuid().ToString("N")[..8], Password), Ct);

        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownUser.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(wrongPassword)).ShouldBe(await ReadProblemAsync(unknownUser));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var user = await CreateUserAsync(Roles.Operator);
        for (var i = 0; i < 5; i++)
        {
            await _anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Username, $"wrong-password-{i:D4}"), Ct);
        }

        var right = await _anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Username, Password), Ct);

        right.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(right)).Code.ShouldBe("auth.locked_out");
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reusing_an_old_one_ends_every_session()
    {
        var user = await CreateUserAsync(Roles.Operator);
        var first = await LoginAsync(user.Username, Password);

        var second = await RefreshAsync(first.RefreshToken);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = (await second.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
        rotated.RefreshToken.ShouldNotBe(first.RefreshToken);

        // Someone replays the first (already used) token: a copy exists, so both copies die.
        var replay = await RefreshAsync(first.RefreshToken);
        var victim = await RefreshAsync(rotated.RefreshToken);

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        victim.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(victim)).Code.ShouldBe("auth.invalid_refresh_token");
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var user = await CreateUserAsync(Roles.Observer);
        var session = await LoginAsync(user.Username, Password);

        (await _anonymous.PostAsJsonAsync("/api/v1/auth/logout", new RefreshRequest(session.RefreshToken), Ct)).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_deactivated_user_can_no_longer_sign_in_or_refresh()
    {
        var user = await CreateUserAsync(Roles.Operator);
        var session = await LoginAsync(user.Username, Password);

        var update = await _admin.PutAsJsonAsync($"/api/v1/users/{user.Id}", new UpdateUserRequest(null, IsActive: false), Ct);

        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Username, Password), Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_ends_other_sessions()
    {
        var user = await CreateUserAsync(Roles.Observer);
        var session = await LoginAsync(user.Username, Password);
        var client = WithToken(session.AccessToken);

        var wrong = await client.PostAsJsonAsync("/api/v1/auth/password", new ChangePasswordRequest("guess-guess-guess", "a-brand-new-passphrase"), Ct);
        var changed = await client.PostAsJsonAsync("/api/v1/auth/password", new ChangePasswordRequest(Password, "a-brand-new-passphrase"), Ct);

        (await ReadProblemAsync(wrong)).Code.ShouldBe("user.password.current_wrong");
        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync(user.Username, "a-brand-new-passphrase")).User.Id.ShouldBe(user.Id);
    }

    [Fact]
    public async Task User_rules_short_passwords_duplicate_names_and_self_demotion_are_refused()
    {
        var shortPassword = await _admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("shorty", "short", Roles.Observer), Ct);
        var user = await CreateUserAsync(Roles.Observer);
        var duplicate = await _admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(user.Username.ToUpperInvariant(), Password, Roles.Observer), Ct);
        var me = await _admin.GetFromJsonAsync<UserResponse>("/api/v1/auth/me", Ct);
        var demoteSelf = await _admin.PutAsJsonAsync($"/api/v1/users/{me!.Id}", new UpdateUserRequest(Roles.Observer, null), Ct);

        (await ReadProblemAsync(shortPassword)).Code.ShouldBe("user.password.length");
        (await ReadProblemAsync(duplicate)).Code.ShouldBe("user.username.in_use");
        (await ReadProblemAsync(demoteSelf)).Code.ShouldBe("user.self_change");
        var users = await _admin.GetFromJsonAsync<PagedResponse<UserResponse>>("/api/v1/users?pageSize=100", Ct);
        users!.Items.ShouldContain(u => u.Username == user.Username);
    }

    [Fact]
    public async Task Forged_or_tampered_tokens_are_rejected()
    {
        var session = await LoginAsync(TestSecrets.AdminUsername, TestSecrets.Password);
        var parts = session.AccessToken.Split('.');

        // Same header and payload, signature replaced; and the classic "alg: none" token with no signature at all.
        var badSignature = $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}";
        var algNone = $"{Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{parts[1]}.";

        (await WithToken(badSignature).GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await WithToken(algNone).GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_audit_log_names_the_signed_in_user_not_a_header_value()
    {
        var pilot = factory.Users.ClientFor("audit-pilot", Roles.Operator);
        var vehicle = await _admin.PostAsJsonAsync("/api/v1/vehicles", TestData.Registration(), Ct);
        var vehicleId = (await vehicle.Content.ReadFromJsonAsync<Gcs.Contracts.Vehicles.VehicleResponse>(Ct))!.Id;

        using var spoofed = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/vehicles/{vehicleId}/commands") { Content = JsonContent.Create(new SendCommandRequest("Land")) };
        spoofed.Headers.Add("X-Operator", "someone-else");
        await pilot.SendAsync(spoofed, Ct);

        var audit = await _admin.GetFromJsonAsync<PagedResponse<CommandAuditResponse>>($"/api/v1/vehicles/{vehicleId}/commands", Ct);
        audit!.Items.ShouldHaveSingleItem().Operator.ShouldBe("audit-pilot");
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _anonymous.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldStartWith("default-src 'none'");
    }

    private async Task<UserResponse> CreateUserAsync(string role)
    {
        var request = new CreateUserRequest($"user-{Guid.NewGuid():N}"[..20], Password, role);
        var response = await _admin.PostAsJsonAsync("/api/v1/users", request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Ct))!;
    }

    private async Task<TokenResponse> LoginAsync(string username, string password)
    {
        var response = await _anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(username, password), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshToken), Ct);

    private HttpClient WithToken(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(string? Code, string? Detail)> ReadProblemAsync(HttpResponseMessage response)
    {
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Ct), cancellationToken: Ct);
        return (problem.RootElement.GetProperty("code").GetString(), problem.RootElement.GetProperty("detail").GetString());
    }

    private static string Base64Url(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
