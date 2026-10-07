using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Gcs.Contracts.Vehicles;

namespace Gcs.Desktop.ViewModels;

/// <summary>
/// Formatted telemetry for the instrument panel and status bar. Formatting lives here, not in the view, so it is
/// unit tested and the view only binds strings. Missing values show as "—" (never 0, which would be a lie).
/// </summary>
public sealed partial class TelemetryViewModel : ObservableObject
{
    public const string NoValue = "—";
    private const int LowBatteryPercent = 25;

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [ObservableProperty] private string _latitude = NoValue;
    [ObservableProperty] private string _longitude = NoValue;
    [ObservableProperty] private string _altitudeMsl = NoValue;
    [ObservableProperty] private string _altitudeRelative = NoValue;
    [ObservableProperty] private string _groundSpeed = NoValue;
    [ObservableProperty] private string _airSpeed = NoValue;
    [ObservableProperty] private string _climbRate = NoValue;
    [ObservableProperty] private string _heading = NoValue;
    [ObservableProperty] private string _roll = NoValue;
    [ObservableProperty] private string _pitch = NoValue;
    [ObservableProperty] private string _yaw = NoValue;
    [ObservableProperty] private string _battery = NoValue;
    [ObservableProperty] private string _batteryVoltage = NoValue;
    [ObservableProperty] private string _gps = NoValue;
    [ObservableProperty] private string _flightMode = NoValue;
    [ObservableProperty] private string _armed = NoValue;
    [ObservableProperty] private bool _isArmed;
    [ObservableProperty] private bool _isBatteryLow;
    [ObservableProperty] private string _lastUpdate = NoValue;

    /// <summary>Raw position for the map (null until the first fix).</summary>
    [ObservableProperty] private PositionDto? _position;

    /// <summary>Heading in degrees for the map marker (null until known).</summary>
    [ObservableProperty] private double? _headingDegrees;

    public void Apply(TelemetryResponse telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        if (telemetry.Position is { } p)
        {
            Position = p;
            Latitude = p.Latitude.ToString("F6", Culture) + "°";
            Longitude = p.Longitude.ToString("F6", Culture) + "°";
            AltitudeMsl = $"{p.AltitudeMsl.ToString("F1", Culture)} m";
            AltitudeRelative = $"{p.RelativeAltitude.ToString("F1", Culture)} m";
        }

        if (telemetry.Motion is { } m)
        {
            GroundSpeed = $"{m.GroundSpeed.ToString("F1", Culture)} m/s";
            AirSpeed = $"{m.AirSpeed.ToString("F1", Culture)} m/s";
            ClimbRate = $"{m.ClimbRate.ToString("+0.0;-0.0;0.0", Culture)} m/s";
            Heading = $"{m.Heading.ToString("F0", Culture)}°";
            HeadingDegrees = m.Heading;
        }

        if (telemetry.Attitude is { } a)
        {
            Roll = $"{a.Roll.ToString("F1", Culture)}°";
            Pitch = $"{a.Pitch.ToString("F1", Culture)}°";
            Yaw = $"{a.Yaw.ToString("F0", Culture)}°";
            HeadingDegrees ??= a.Yaw;
        }

        if (telemetry.Battery is { } b)
        {
            Battery = b.RemainingPercent is { } pct ? $"{pct}%" : NoValue;
            BatteryVoltage = b.Voltage is { } v ? $"{v.ToString("F2", Culture)} V" : NoValue;
            IsBatteryLow = b.RemainingPercent is < LowBatteryPercent;
        }

        if (telemetry.Gps is { } g)
        {
            Gps = $"{g.Fix} · {g.SatellitesVisible} sats";
        }

        if (telemetry.Flight is { } f)
        {
            FlightMode = f.FlightMode;
            IsArmed = f.Armed;
            Armed = f.Armed ? "ARMED" : "DISARMED";
        }

        LastUpdate = telemetry.UpdatedAt.ToLocalTime().ToString("HH:mm:ss", Culture);
    }

    /// <summary>Clears everything (another vehicle was selected).</summary>
    public void Reset()
    {
        Latitude = Longitude = AltitudeMsl = AltitudeRelative = NoValue;
        GroundSpeed = AirSpeed = ClimbRate = Heading = Roll = Pitch = Yaw = NoValue;
        Battery = BatteryVoltage = Gps = FlightMode = Armed = LastUpdate = NoValue;
        IsArmed = IsBatteryLow = false;
        Position = null;
        HeadingDegrees = null;
    }
}
