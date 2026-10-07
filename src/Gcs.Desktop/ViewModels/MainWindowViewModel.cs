using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gcs.Contracts.Vehicles;
using Gcs.Desktop.Services;

namespace Gcs.Desktop.ViewModels;

/// <summary>
/// The GCS main screen: vehicle list, selected vehicle's live telemetry and controls, connect/disconnect, backend status.
/// It only talks to <see cref="IGcsApiClient"/> and <see cref="IRealtimeClient"/>, so it is tested without a server.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IGcsApiClient _api;
    private readonly IRealtimeClient _realtime;
    private readonly IUiDispatcher _ui;

    public MainWindowViewModel(
        IGcsApiClient api,
        IRealtimeClient realtime,
        IUiDispatcher ui,
        Uri apiBaseUrl,
        IConfirmationService? confirmation = null,
        string operatorName = "operator",
        string role = "Operator")
    {
        _api = api;
        _realtime = realtime;
        _ui = ui;
        ServerAddress = apiBaseUrl.ToString();
        Planner = new MissionPlannerViewModel(api, () => SelectedVehicle);
        // Only roles with the Command permission get working control buttons; the server enforces it regardless.
        var mayCommand = role is "Operator" or "Administrator";
        Commands = new CommandPanelViewModel(api, confirmation ?? new DenyAllConfirmation(), () => SelectedVehicle, operatorName, mayCommand);
        CurrentUser = $"{operatorName} ({role})";

        _realtime.TelemetryReceived += telemetry => _ui.Post(() => OnTelemetry(telemetry));
        _realtime.LinkStatusReceived += status => _ui.Post(() => OnLinkStatus(status));
        _realtime.CommandLeaseReceived += lease => _ui.Post(() => Commands.Apply(lease));
        _realtime.ConnectionStateChanged += state => _ui.Post(() => BackendState = state);
    }

    public string Title { get; } = "UAV Ground Control Station";

    public string ServerAddress { get; }

    /// <summary>"name (Role)" for the toolbar.</summary>
    public string CurrentUser { get; }

    /// <summary>Raised by the Sign out button; the app ends the session and shows the sign-in window.</summary>
    public event EventHandler? SignOutRequested;

    public ObservableCollection<VehicleItemViewModel> Vehicles { get; } = [];

    public TelemetryViewModel Telemetry { get; } = new();

    public MissionPlannerViewModel Planner { get; }

    public CommandPanelViewModel Commands { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackendStatus))]
    private BackendConnectionState _backendState = BackendConnectionState.Disconnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand))]
    private VehicleItemViewModel? _selectedVehicle;

    [ObservableProperty]
    private string? _errorMessage;

    public string BackendStatus => $"Backend: {BackendState}";

    /// <summary>Loads the fleet, starts the live connection. Called once when the window opens.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _realtime.StartAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
        try
        {
            await Planner.LoadMissionsAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Reported by RefreshAsync already; the planner list fills on the next save or refresh.
        }

        if (Planner.Items.Count == 0)
        {
            Planner.StartBlankMission();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var vehicles = await _api.GetActiveVehiclesAsync(cancellationToken);
            var selectedId = SelectedVehicle?.Id;
            Vehicles.Clear();
            foreach (var vehicle in vehicles.OrderBy(v => v.Callsign, StringComparer.Ordinal))
            {
                var item = new VehicleItemViewModel(vehicle);
                item.Apply(await _api.GetLinkAsync(vehicle.Id, cancellationToken));
                Vehicles.Add(item);
            }

            SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == selectedId) ?? Vehicles.FirstOrDefault();
            ErrorMessage = null;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Cannot reach the backend at {ServerAddress}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SignOut() => SignOutRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync(CancellationToken cancellationToken) =>
        RunAsync(() => _api.ConnectAsync(SelectedVehicle!.Id, cancellationToken));

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task DisconnectAsync(CancellationToken cancellationToken) =>
        RunAsync(() => _api.DisconnectAsync(SelectedVehicle!.Id, cancellationToken));

    private bool CanConnect() => SelectedVehicle?.CanConnect == true;

    private bool CanDisconnect() => SelectedVehicle?.CanDisconnect == true;

    /// <summary>Selecting another vehicle moves the telemetry subscription to it.</summary>
    async partial void OnSelectedVehicleChanged(VehicleItemViewModel? oldValue, VehicleItemViewModel? newValue)
    {
        Telemetry.Reset();
        await Commands.LoadAsync(CancellationToken.None);
        try
        {
            if (oldValue is not null)
            {
                await _realtime.UnsubscribeVehicleAsync(oldValue.Id, CancellationToken.None);
            }

            if (newValue is not null)
            {
                await _realtime.SubscribeVehicleAsync(newValue.Id, CancellationToken.None);
                if (await _api.GetLatestTelemetryAsync(newValue.Id, CancellationToken.None) is { } latest)
                {
                    Telemetry.Apply(latest);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void OnTelemetry(TelemetryResponse telemetry)
    {
        if (telemetry.VehicleId == SelectedVehicle?.Id)
        {
            Telemetry.Apply(telemetry);
        }
    }

    private void OnLinkStatus(VehicleLinkResponse status)
    {
        var vehicle = Vehicles.FirstOrDefault(v => v.Id == status.VehicleId);
        if (vehicle is null)
        {
            return;
        }

        vehicle.Apply(status);
        if (vehicle == SelectedVehicle)
        {
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            Commands.OnLinkChanged();
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            ErrorMessage = null;
        }
        catch (ApiProblemException ex)
        {
            ErrorMessage = ex.Describe();
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}

/// <summary>Used when no dialog is available (tests, headless): critical commands are never confirmed implicitly.</summary>
internal sealed class DenyAllConfirmation : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);
}
