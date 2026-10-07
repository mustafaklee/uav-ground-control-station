using System.Net;
using System.Net.Http.Json;
using Gcs.Contracts.Auth;
using Gcs.Desktop.Services;
using Gcs.Desktop.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Gcs.UnitTests.Desktop;

public sealed class AuthSessionTests : IDisposable
{
    private static readonly UserResponse Pilot = new(Guid.NewGuid(), "pilot", "Operator", true, null, DateTimeOffset.UnixEpoch);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly ScriptedServer _server = new();
    private readonly AuthSession _session;

    public AuthSessionTests()
    {
        _session = new AuthSession(new HttpClient(_server) { BaseAddress = new Uri("http://gcs/") }, _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _session.Dispose();

    [Fact]
    public async Task The_access_token_is_reused_until_a_minute_before_it_expires_then_renewed_once()
    {
        _server.Next = Tokens("access-1", "refresh-1");
        await _session.LoginAsync("pilot", "a-long-passphrase", Ct);

        (await _session.GetAccessTokenAsync(Ct)).ShouldBe("access-1");
        _time.Advance(TimeSpan.FromMinutes(14.5));
        _server.Next = Tokens("access-2", "refresh-2");
        var parallel = await Task.WhenAll(_session.GetAccessTokenAsync(Ct), _session.GetAccessTokenAsync(Ct));

        parallel.ShouldBe(["access-2", "access-2"]);
        _server.Requests.Count(r => r.EndsWith("refresh", StringComparison.Ordinal)).ShouldBe(1);
        _server.LastBody.ShouldContain("refresh-1");
    }

    [Fact]
    public async Task A_refused_refresh_ends_the_session()
    {
        _server.Next = Tokens("access-1", "refresh-1");
        await _session.LoginAsync("pilot", "a-long-passphrase", Ct);
        var ended = false;
        _session.Ended += (_, _) => ended = true;

        _time.Advance(TimeSpan.FromMinutes(15));
        _server.Next = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        (await _session.GetAccessTokenAsync(Ct)).ShouldBeNull();
        ended.ShouldBeTrue();
        _session.User.ShouldBeNull();
    }

    [Fact]
    public async Task The_login_screen_shows_the_servers_reason_and_clears_the_password()
    {
        var viewModel = new LoginViewModel(_session, new Uri("http://gcs/")) { Username = "pilot", Password = "wrong-password-123" };
        _server.Next = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent.Create(new { code = "auth.locked_out", detail = "Too many wrong passwords." }),
        };

        await viewModel.SignInCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.ShouldBe("Too many wrong passwords.");
        viewModel.Password.ShouldBeEmpty();
        viewModel.SignInCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_successful_login_raises_signed_in_with_the_user()
    {
        var viewModel = new LoginViewModel(_session, new Uri("http://gcs/")) { Username = " pilot ", Password = "a-long-passphrase" };
        var signedIn = false;
        viewModel.SignedIn += (_, _) => signedIn = true;
        _server.Next = Tokens("access-1", "refresh-1");

        await viewModel.SignInCommand.ExecuteAsync(null);

        signedIn.ShouldBeTrue();
        _session.User.ShouldBe(Pilot);
        _server.LastBody.ShouldContain("\"username\":\"pilot\"");
    }

    private HttpResponseMessage Tokens(string access, string refresh) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new TokenResponse(access, _time.GetUtcNow().AddMinutes(15), refresh, _time.GetUtcNow().AddHours(12), Pilot)),
    };

    private sealed class ScriptedServer : HttpMessageHandler
    {
        public HttpResponseMessage Next { get; set; } = new(HttpStatusCode.NotFound);

        public List<string> Requests { get; } = [];

        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            await Task.Yield();
            return Next;
        }
    }
}
