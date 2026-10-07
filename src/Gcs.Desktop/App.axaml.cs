using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Gcs.Contracts.Commands;
using Gcs.Desktop.Services;
using Gcs.Desktop.ViewModels;
using Gcs.Desktop.Views;

namespace Gcs.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root of the desktop client: a handful of objects, wired by hand.
            var options = GcsClientOptions.FromEnvironment(desktop.Args ?? []);

            // 30 s: a command may wait for its ACK through several retries before the API answers.
            var http = new HttpClient { BaseAddress = options.ApiBaseUrl, Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add(CommandHeaders.Operator, options.OperatorName);
            var realtime = new RealtimeClient(options.ApiBaseUrl);
            MainWindow? window = null;
            var viewModel = new MainWindowViewModel(
                new GcsApiClient(http), realtime, new AvaloniaUiDispatcher(), options.ApiBaseUrl,
                new DialogConfirmationService(() => window), options.OperatorName);

            window = new MainWindow { DataContext = viewModel };
            window.Opened += async (_, _) => await viewModel.InitializeAsync(CancellationToken.None);
            desktop.MainWindow = window;

            // Keep control alive while this GCS runs: renew at a third of the server's 60 s lease.
            var renew = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            renew.Tick += async (_, _) => await viewModel.Commands.RenewAsync(CancellationToken.None);
            renew.Start();

            desktop.ShutdownRequested += async (_, _) =>
            {
                renew.Stop();
                await realtime.DisposeAsync();
                http.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
