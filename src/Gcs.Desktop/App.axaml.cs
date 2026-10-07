using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            var http = new HttpClient { BaseAddress = options.ApiBaseUrl, Timeout = TimeSpan.FromSeconds(10) };
            var realtime = new RealtimeClient(options.ApiBaseUrl);
            var viewModel = new MainWindowViewModel(new GcsApiClient(http), realtime, new AvaloniaUiDispatcher(), options.ApiBaseUrl);

            var window = new MainWindow { DataContext = viewModel };
            window.Opened += async (_, _) => await viewModel.InitializeAsync(CancellationToken.None);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) =>
            {
                await realtime.DisposeAsync();
                http.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
