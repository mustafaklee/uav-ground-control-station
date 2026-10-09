using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Gcs.Contracts.Vehicles;

namespace Gcs.Desktop.ViewModels;

/// <summary>One row of the vehicle list: identity plus live link state and link quality (Phase 12).</summary>
public sealed partial class VehicleItemViewModel(VehicleResponse vehicle) : ObservableObject
{
    public Guid Id { get; } = vehicle.Id;

    public string Callsign { get; } = vehicle.Callsign;

    public string Description { get; } = $"{vehicle.Autopilot} {vehicle.Type} · sysid {vehicle.MavlinkSystemId} · {vehicle.Connection.Transport}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(CanConnect), nameof(CanDisconnect), nameof(IsTransitioning), nameof(IsFaulted))]
    private string _linkState = "Disconnected";

    [ObservableProperty]
    private string? _faultReason;

    /// <summary>Lost, Poor, Fair or Good (ADR-019); colours the quality line.</summary>
    [ObservableProperty]
    private string _linkGrade = "Lost";

    /// <summary>e.g. "Good · 12 ms · loss 0.4 % · 41 msg/s · RSSI 182"; empty while not connected.</summary>
    [ObservableProperty]
    private string _linkSummary = string.Empty;

    /// <summary>The live connection to the backend is down: the values shown are the last ones received.</summary>
    [ObservableProperty]
    private bool _isStale;

    public bool IsConnected => LinkState == "Connected";

    /// <summary>Connecting or reconnecting: shown in the warning colour.</summary>
    public bool IsTransitioning => LinkState is "Connecting" or "Reconnecting";

    public bool IsFaulted => LinkState == "Faulted";

    /// <summary>Connecting is possible when there is no active link (never connected, disconnected or faulted).</summary>
    public bool CanConnect => LinkState is "Disconnected" or "Faulted";

    public bool CanDisconnect => !CanConnect;

    public void Apply(VehicleLinkResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        LinkState = status.State;
        FaultReason = status.FaultReason;
        LinkGrade = status.Quality.Grade;
        LinkSummary = status.State != "Connected" ? string.Empty
            : status.Quality.LastFrameAt is null ? WaitingForData
            : Summarize(status.Quality);
    }

    /// <summary>Connected, but no frame counted yet: "Lost · 0 msg/s" would contradict the state next to it.</summary>
    public const string WaitingForData = "Waiting for data";

    internal static string Summarize(LinkQualityDto quality)
    {
        var parts = new List<string> { quality.Grade };
        if (quality.RoundTripMilliseconds is { } roundTrip)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{roundTrip:0} ms"));
        }

        parts.Add(string.Create(CultureInfo.InvariantCulture, $"loss {quality.RecentPacketLossRatio * 100:0.#} %"));
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"{quality.MessagesPerSecond:0} msg/s"));
        if (quality.Radio is { } radio)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"RSSI {radio.Rssi}/{radio.RemoteRssi}"));
        }

        return string.Join(" · ", parts);
    }
}
