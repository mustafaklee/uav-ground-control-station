using CommunityToolkit.Mvvm.ComponentModel;
using Gcs.Contracts.Vehicles;

namespace Gcs.Desktop.ViewModels;

/// <summary>One row of the vehicle list: identity plus live link state.</summary>
public sealed partial class VehicleItemViewModel(VehicleResponse vehicle) : ObservableObject
{
    public Guid Id { get; } = vehicle.Id;

    public string Callsign { get; } = vehicle.Callsign;

    public string Description { get; } = $"{vehicle.Autopilot} {vehicle.Type} · sysid {vehicle.MavlinkSystemId} · {vehicle.Connection.Transport}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(CanConnect), nameof(CanDisconnect))]
    private string _linkState = "Disconnected";

    [ObservableProperty]
    private string? _faultReason;

    public bool IsConnected => LinkState == "Connected";

    /// <summary>Connecting is possible when there is no active link (never connected, disconnected or faulted).</summary>
    public bool CanConnect => LinkState is "Disconnected" or "Faulted";

    public bool CanDisconnect => !CanConnect;

    public void Apply(VehicleLinkResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        LinkState = status.State;
        FaultReason = status.FaultReason;
    }
}
