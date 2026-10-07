using System.Net.Http.Headers;
using System.Net.Http.Json;
using Gcs.Contracts.Auth;

namespace Gcs.Desktop.Services;

/// <summary>The signed-in operator's session. An interface so view models can be tested without a server.</summary>
public interface IAuthSession
{
    /// <summary>Raised when the session can no longer be renewed (expired, revoked, account deactivated): sign in again.</summary>
    event EventHandler? Ended;

    UserResponse? User { get; }

    Task LoginAsync(string username, string password, CancellationToken cancellationToken);

    /// <summary>A valid access token, renewed first if it is about to expire; null when signed out.</summary>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);

    Task LogoutAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the tokens in memory only, never on disk: closing the GCS ends the session, and nothing on the laptop can be
/// copied to sign in elsewhere. The access token is renewed a minute before it expires; the refresh token is single use,
/// so the newest one always replaces the old.
/// </summary>
public sealed class AuthSession(HttpClient http, TimeProvider time) : IAuthSession, IDisposable
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TokenResponse? _tokens;

    public event EventHandler? Ended;

    public UserResponse? User => _tokens?.User;

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("api/v1/auth/login", new LoginRequest(username, password), cancellationToken);
        await ApiProblemException.ThrowIfFailedAsync(response, cancellationToken);
        _tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_tokens is not { } current || current.AccessTokenExpiresAt - time.GetUtcNow() > RenewBefore)
        {
            return _tokens?.AccessToken;
        }

        // One renewal at a time: two parallel requests must not both spend the same single-use refresh token.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tokens is { } latest && latest.AccessTokenExpiresAt - time.GetUtcNow() > RenewBefore)
            {
                return latest.AccessToken; // another caller renewed while we waited
            }

            using var response = await http.PostAsJsonAsync("api/v1/auth/refresh", new RefreshRequest(current.RefreshToken), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                End();
                return null;
            }

            _tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            return _tokens?.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (_tokens is { } current)
        {
            _tokens = null;
            try
            {
                using var response = await http.PostAsJsonAsync("api/v1/auth/logout", new RefreshRequest(current.RefreshToken), cancellationToken);
            }
            catch (HttpRequestException)
            {
                // Offline: the refresh token simply expires on the server.
            }
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        http.Dispose();
    }

    /// <summary>Called when the server rejects our token although we just renewed it.</summary>
    public void End()
    {
        _tokens = null;
        Ended?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Adds the bearer token to every API request; a 401 means the session is over and ends it.</summary>
public sealed class AuthenticatingHandler(AuthSession session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await session.GetAccessTokenAsync(cancellationToken) is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            session.End();
        }

        return response;
    }
}
