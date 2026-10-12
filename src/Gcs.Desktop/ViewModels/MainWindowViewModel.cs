using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gcs.Contracts.Vehicles;
using Gcs.Desktop.Services;

namespace Gcs.Desktop.ViewModels;

/// <summary>The pages of the main window's navigation rail. The map stays; the side panel shows the page.</summary>
public enum AppPage
{
    Flight,
    Mission,
}

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
        OperatorName = operatorName;
        Role = role;
        Initials = operatorName.Length > 0 ? operatorName[..1].ToUpperInvariant() : "?";

        Vehicles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVehicles));

        // Clicking the map adds waypoints to the plan, so turning that on from the map shows the plan.
        Planner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MissionPlannerViewModel.IsAddingWaypoints) && Planner.IsAddingWaypoints)
            {
                CurrentPage = AppPage.Mission;
            }
        };

        _realtime.TelemetryReceived += telemetry => _ui.Post(() => OnTelemetry(telemetry));
        _realtime.LinkStatusReceived += status => _ui.Post(() => OnLinkStatus(status));
        _realtime.CommandLeaseReceived += lease => _ui.Post(() => Commands.Apply(lease));
        _realtime.ConnectionStateChanged += state => _ui.Post(() => BackendState = state);
    }

    public string Title { get; } = "UAV Ground Control Station";

    public string ServerAddress { get; }

    /// <summary>"name (Role)" for the toolbar.</summary>
    public string CurrentUser { get; }

    public string OperatorName { get; }

    public string Role { get; }

    /// <summary>First letter of the operator's name, for the account button.</summary>
    public string Initials { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlightPage), nameof(IsMissionPage), nameof(PageTitle), nameof(PageSubtitle))]
    private AppPage _currentPage = AppPage.Flight;

    /// <summary>Two-way for the navigation rail's radio buttons; only "true" switches, the other button clears itself.</summary>
    public bool IsFlightPage
    {
        get => CurrentPage == AppPage.Flight;
        set
        {
            if (value)
            {
                CurrentPage = AppPage.Flight;
            }
        }
    }

    public bool IsMissionPage
    {
        get => CurrentPage == AppPage.Mission;
        set
        {
            if (value)
            {
                CurrentPage = AppPage.Mission;
            }
        }
    }

    public string PageTitle => CurrentPage == AppPage.Flight ? "Flight operations" : "Mission planning";

    public string PageSubtitle => CurrentPage == AppPage.Flight
        ? "Live telemetry, link status and vehicle control"
        : "Plan, check and transfer missions";

    /// <summary>Raised by the Sign out button; the app ends the session and shows the sign-in window.</summary>
    public event EventHandler? SignOutRequested;

    public ObservableCollection<VehicleItemViewModel> Vehicles { get; } = [];

    public TelemetryViewModel Telemetry { get; } = new();

    public MissionPlannerViewModel Planner { get; }

    public CommandPanelViewModel Commands { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackendStatus), nameof(BackendStateText), nameof(IsBackendConnected))]
    private BackendConnectionState _backendState = BackendConnectionState.Disconnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedVehicle))]
    private VehicleItemViewModel? _selectedVehicle;

    [ObservableProperty]
    private string? _errorMessage;

    public string BackendStatus => $"Backend: {BackendState}";

    /// <summary>For the status pill: "Online", "Connecting", "Reconnecting", "Offline".</summary>
    public string BackendStateText => BackendState switch
    {
        BackendConnectionState.Connected => "Online",
        BackendConnectionState.Disconnected => "Offline",
        var state => state.ToString(),
    };

    public bool IsBackendConnected => BackendState == BackendConnectionState.Connected;

    public bool HasSelectedVehicle => SelectedVehicle is not null;

    public bool HasVehicles => Vehicles.Count > 0;

    /// <summary>
    /// Without a live connection the link rows stop updating; they say so instead of showing old values as current.
    /// </summary>
    partial void OnBackendStateChanged(BackendConnectionState value)
    {
        foreach (var vehicle in Vehicles)
        {
            vehicle.IsStale = value != BackendConnectionState.Connected;
        }
    }

    [RelayCommand]
    private void ShowPage(AppPage page) => CurrentPage = page;

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

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
                var item = new VehicleItemViewModel(vehicle) { IsStale = BackendState != BackendConnectionState.Connected };
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
