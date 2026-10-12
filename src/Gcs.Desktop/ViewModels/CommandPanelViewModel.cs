using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gcs.Contracts.Commands;
using Gcs.Desktop.Services;

namespace Gcs.Desktop.ViewModels;

/// <summary>
/// Vehicle control for the selected vehicle: take/release control (the command lease), the six commands, the answer to
/// the last command and the recent audit log. Critical commands (ARM, DISARM, TAKEOFF, mode change) ask for confirmation
/// first; LAND and RTL do not, because they are the operator's emergency reaction and must be one click.
/// </summary>
public sealed partial class CommandPanelViewModel : ObservableObject
{
    private readonly IGcsApiClient _api;
    private readonly IConfirmationService _confirmation;
    private readonly Func<VehicleItemViewModel?> _selectedVehicle;

    public CommandPanelViewModel(
        IGcsApiClient api,
        IConfirmationService confirmation,
        Func<VehicleItemViewModel?> selectedVehicle,
        string operatorName,
        bool mayCommand = true)
    {
        _api = api;
        _confirmation = confirmation;
        _selectedVehicle = selectedVehicle;
        OperatorName = operatorName;
        MayCommand = mayCommand;
        History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
    }

    public bool HasHistory => History.Count > 0;

    public string OperatorName { get; }

    /// <summary>False for observers and maintenance staff: their role has no Command permission.</summary>
    public bool MayCommand { get; }

    public ObservableCollection<string> FlightModes { get; } = [];

    public ObservableCollection<CommandAuditResponse> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasControl), nameof(ControlText))]
    [NotifyCanExecuteChangedFor(nameof(TakeControlCommand), nameof(ReleaseControlCommand))]
    private CommandLeaseResponse? _lease;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(ArmCommand), nameof(DisarmCommand), nameof(TakeoffCommand), nameof(SetModeCommand), nameof(TakeControlCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private decimal _takeoffAltitude = 10;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetModeCommand))]
    private string? _selectedMode;

    [ObservableProperty]
    private string? _lastResult;

    [ObservableProperty]
    private bool _lastResultIsError;

    partial void OnLeaseChanged(CommandLeaseResponse? value) => OnLinkChanged();

    /// <summary>True when this operator holds the lease. The server checks again on every command.</summary>
    public bool HasControl => Lease?.Holder == OperatorName;

    public string ControlText => Lease?.Holder switch
    {
        null when !MayCommand => "Nobody controls this vehicle. Your role cannot command vehicles.",
        null => "Nobody controls this vehicle.",
        var holder when holder == OperatorName => $"You ({OperatorName}) control this vehicle.",
        var holder => $"Controlled by {holder} until {Lease.ExpiresAt?.ToLocalTime():HH:mm:ss}.",
    };

    /// <summary>Loads lease, flight modes and recent commands for the newly selected vehicle.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Lease = null;
        LastResult = null;
        FlightModes.Clear();
        History.Clear();
        if (_selectedVehicle() is not { } vehicle)
        {
            return;
        }

        await RunAsync(async () =>
        {
            Lease = await _api.GetCommandLeaseAsync(vehicle.Id, cancellationToken);
            foreach (var mode in await _api.GetFlightModesAsync(vehicle.Id, cancellationToken))
            {
                FlightModes.Add(mode);
            }

            SelectedMode = FlightModes.FirstOrDefault();
            await RefreshHistoryAsync(vehicle.Id, cancellationToken);
        }, reportSuccess: false);
    }

    /// <summary>A lease change pushed by the server (someone took or released control).</summary>
    public void Apply(CommandLeaseResponse lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.VehicleId == _selectedVehicle()?.Id)
        {
            Lease = lease;
        }
    }

    /// <summary>Called periodically: keeps control alive while this GCS is running. Losing it is shown, not hidden.</summary>
    public async Task RenewAsync(CancellationToken cancellationToken)
    {
        if (!HasControl || _selectedVehicle() is not { } vehicle)
        {
            return;
        }

        try
        {
            Lease = await _api.AcquireCommandLeaseAsync(vehicle.Id, cancellationToken);
        }
        catch (ApiProblemException ex)
        {
            Lease = null;
            Report($"Control lost: {ex.Message}", isError: true);
        }
        catch (HttpRequestException ex)
        {
            Report($"Could not renew control: {ex.Message}", isError: true);
        }
    }

    /// <summary>The selected vehicle's link changed: commands can only be sent over a connected link.</summary>
    public void OnLinkChanged()
    {
        ArmCommand.NotifyCanExecuteChanged();
        DisarmCommand.NotifyCanExecuteChanged();
        TakeoffCommand.NotifyCanExecuteChanged();
        LandCommand.NotifyCanExecuteChanged();
        ReturnToLaunchCommand.NotifyCanExecuteChanged();
        SetModeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanTakeControl))]
    private Task TakeControlAsync(CancellationToken cancellationToken) =>
        RunAsync(async () => Lease = await _api.AcquireCommandLeaseAsync(_selectedVehicle()!.Id, cancellationToken), reportSuccess: false);

    [RelayCommand(CanExecute = nameof(HasControl))]
    private Task ReleaseControlAsync(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            var vehicle = _selectedVehicle()!;
            await _api.ReleaseCommandLeaseAsync(vehicle.Id, cancellationToken);
            Lease = new CommandLeaseResponse(vehicle.Id, null, null, null);
        }, reportSuccess: false);

    [RelayCommand(CanExecute = nameof(CanSendCritical))]
    private Task ArmAsync(CancellationToken cancellationToken) =>
        SendAsync(new SendCommandRequest("Arm", Confirm: true), "ARM", "The motors will start. Make sure nobody is near the propellers.", cancellationToken);

    [RelayCommand(CanExecute = nameof(CanSendCritical))]
    private Task DisarmAsync(CancellationToken cancellationToken) =>
        SendAsync(new SendCommandRequest("Disarm", Confirm: true), "DISARM", "The motors will stop. In the air the vehicle would fall; autopilots refuse this in flight.", cancellationToken);

    [RelayCommand(CanExecute = nameof(CanSendCritical))]
    private Task TakeoffAsync(CancellationToken cancellationToken) =>
        SendAsync(
            new SendCommandRequest("Takeoff", Altitude: (double)TakeoffAltitude, Confirm: true),
            "TAKEOFF",
            string.Create(CultureInfo.InvariantCulture, $"The vehicle will climb to {TakeoffAltitude:0.#} m above home."),
            cancellationToken);

    [RelayCommand(CanExecute = nameof(CanSendSetMode))]
    private Task SetModeAsync(CancellationToken cancellationToken) =>
        SendAsync(new SendCommandRequest("SetMode", Mode: SelectedMode, Confirm: true), $"MODE {SelectedMode}", $"Switch the flight mode to {SelectedMode}.", cancellationToken);

    // No confirmation and no busy check: an operator must be able to send LAND or RTL at any moment.
    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task LandAsync(CancellationToken cancellationToken) => SendAsync(new SendCommandRequest("Land"), "LAND", null, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task ReturnToLaunchAsync(CancellationToken cancellationToken) =>
        SendAsync(new SendCommandRequest("ReturnToLaunch"), "RTL", null, cancellationToken);

    private bool CanTakeControl() => MayCommand && _selectedVehicle() is not null && !HasControl && !IsBusy;

    private bool CanSend() => HasControl && _selectedVehicle()?.IsConnected == true;

    private bool CanSendCritical() => CanSend() && !IsBusy;

    private bool CanSendSetMode() => CanSendCritical() && SelectedMode is not null;

    private async Task SendAsync(SendCommandRequest command, string label, string? warning, CancellationToken cancellationToken)
    {
        var vehicle = _selectedVehicle()!;
        if (warning is not null && !await _confirmation.ConfirmAsync($"{label} {vehicle.Callsign}?", warning, label))
        {
            Report($"{label} cancelled.", isError: false);
            return;
        }

        IsBusy = true;
        try
        {
            await RunAsync(async () =>
            {
                try
                {
                    var answer = await _api.SendCommandAsync(vehicle.Id, command, cancellationToken);
                    Report($"{label}: accepted by {vehicle.Callsign} ({answer.Attempts} attempt(s)).", isError: false);
                }
                finally
                {
                    await RefreshHistoryAsync(vehicle.Id, cancellationToken);
                }
            }, reportSuccess: false, failurePrefix: $"{label}: ");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshHistoryAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        var history = await _api.GetCommandHistoryAsync(vehicleId, cancellationToken);
        History.Clear();
        foreach (var entry in history)
        {
            History.Add(entry);
        }
    }

    private async Task RunAsync(Func<Task> action, bool reportSuccess, string failurePrefix = "")
    {
        try
        {
            await action();
            if (reportSuccess)
            {
                Report(null, isError: false);
            }
        }
        catch (ApiProblemException ex)
        {
            Report(failurePrefix + ex.Describe(), isError: true);
        }
        catch (HttpRequestException ex)
        {
            Report(failurePrefix + ex.Message, isError: true);
        }
    }

    private void Report(string? message, bool isError)
    {
        LastResult = message;
        LastResultIsError = isError;
    }
}
