using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Gcs.Contracts.Missions;

namespace Gcs.Desktop.ViewModels;

/// <summary>One editable row of the mission. The server validates the values; the UI only shapes them.</summary>
public sealed partial class MissionItemViewModel : ObservableObject
{
    public const double DefaultAltitude = 50;
    public const double DefaultTakeoffAltitude = 30;

    public static readonly IReadOnlyList<string> Commands = ["Takeoff", "Waypoint", "Loiter", "ReturnToLaunch", "Land"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(HasPosition), nameof(Label))]
    private string _command = "Waypoint";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Number))]
    private int _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(HasPosition))]
    private double? _latitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(HasPosition))]
    private double? _longitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Label))]
    private double? _altitude;

    [ObservableProperty]
    private double? _holdSeconds;

    [ObservableProperty]
    private double? _speed;

    /// <summary>Validation message from the server for this row, if any.</summary>
    [ObservableProperty]
    private string? _issue;

    public bool HasPosition => Latitude is not null && Longitude is not null;

    /// <summary>"3 · Waypoint · 50 m" for the list.</summary>
    public string Title => Altitude is { } alt
        ? string.Create(CultureInfo.InvariantCulture, $"{Index + 1} · {Command} · {alt:0} m")
        : string.Create(CultureInfo.InvariantCulture, $"{Index + 1} · {Command}");

    /// <summary>1-based position in the plan, as on the map.</summary>
    public int Number => Index + 1;

    /// <summary>"Waypoint · 50 m": the title without the number, for rows that show the number separately.</summary>
    public string Label => Altitude is { } alt
        ? string.Create(CultureInfo.InvariantCulture, $"{Command} · {alt:0} m")
        : Command;

    public static MissionItemViewModel From(MissionItemDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new MissionItemViewModel
        {
            Command = dto.Command ?? "Waypoint",
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            Altitude = dto.Altitude,
            HoldSeconds = dto.HoldSeconds,
            Speed = dto.Speed,
        };
    }

    public MissionItemDto ToDto() => new(Command, Latitude, Longitude, Altitude, HoldSeconds is 0 ? null : HoldSeconds, Speed);
}
