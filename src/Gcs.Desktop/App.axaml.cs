using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Gcs.Desktop.Services;
using Gcs.Desktop.ViewModels;
using Gcs.Desktop.Views;

namespace Gcs.Desktop;

/// <summary>
/// Composition root of the desktop client, wired by hand. Flow:
/// <code>
/// sign-in window ──signed in──► main window ──sign out / session ended──► sign-in window ...
///        └──closed──► exit                └──closed──► sign out, exit
/// </code>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The Avalonia Application lives as long as the process; the session is disposed on exit.")]
public sealed partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private GcsClientOptions _options = null!;
    private AuthSession _session = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _options = GcsClientOptions.FromEnvironment(desktop.Args ?? []);
            _session = new AuthSession(new HttpClient { BaseAddress = _options.ApiBaseUrl, Timeout = TimeSpan.FromSeconds(15) }, TimeProvider.System);

            // Windows come and go (sign-in, main, sign-in again); the app ends only when we say so.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => _session.Dispose();
            ShowLogin();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowLogin(string? reason = null)
    {
        var viewModel = new LoginViewModel(_session, _options.ApiBaseUrl) { ErrorMessage = reason };
        var login = new LoginWindow { DataContext = viewModel };
        var signedIn = false;
        viewModel.SignedIn += (_, _) =>
        {
            signedIn = true;
            ShowMain();
            login.Close();
        };
        login.Closed += (_, _) =>
        {
            if (!signedIn)
            {
                _desktop!.Shutdown();
            }
        };
        _desktop!.MainWindow = login;
        login.Show();
    }

    private void ShowMain()
    {
        var user = _session.User!;

        // 30 s: a command may wait for its ACK through several retries before the API answers.
        var http = new HttpClient(new AuthenticatingHandler(_session) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = _options.ApiBaseUrl,
            Timeout = TimeSpan.FromSeconds(30),
        };
        var realtime = new RealtimeClient(_options.ApiBaseUrl, () => _session.GetAccessTokenAsync(CancellationToken.None));
        MainWindow? window = null;
        var viewModel = new MainWindowViewModel(
            new GcsApiClient(http), realtime, new AvaloniaUiDispatcher(), _options.ApiBaseUrl,
            new DialogConfirmationService(() => window), user.Username, user.Role);
        window = new MainWindow { DataContext = viewModel };
        window.Opened += async (_, _) => await viewModel.InitializeAsync(CancellationToken.None);

        // Keep control alive while this GCS runs: renew at a third of the server's 60 s lease.
        var renew = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        renew.Tick += async (_, _) => await viewModel.Commands.RenewAsync(CancellationToken.None);
        renew.Start();

        string? nextLogin = null;
        var leaving = false;
        void Leave(string? reason)
        {
            if (leaving)
            {
                return;
            }

            leaving = true;
            nextLogin = reason ?? string.Empty;
            window.Close();
        }

        EventHandler ended = (_, _) => Dispatcher.UIThread.Post(() => Leave("Your session ended. Sign in again."));
        _session.Ended += ended;
        viewModel.SignOutRequested += (_, _) => Leave(null);
        window.Closed += async (_, _) =>
        {
            _session.Ended -= ended;
            renew.Stop();
            await _session.LogoutAsync(CancellationToken.None);
            await realtime.DisposeAsync();
            http.Dispose();
            if (nextLogin is null)
            {
                _desktop!.Shutdown(); // the operator closed the main window
            }
            else
            {
                ShowLogin(nextLogin.Length == 0 ? null : nextLogin);
            }
        };

        _desktop!.MainWindow = window;
        window.Show();
    }
}
