using Gcs.Domain.Telemetry;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.Mavlink.Translation;

/// <summary>
/// Converts MAVLink messages (wire units) into domain telemetry (SI units, degrees). This is the only place that
/// knows that latitude arrives in 1e-7 degrees or altitude in millimetres.
/// </summary>
public static class TelemetryTranslator
{
    private const double E7 = 1e7;
    private const double MillimetresPerMetre = 1000.0;
    private const ushort UnknownVoltage = ushort.MaxValue;
    private const double MillivoltsPerVolt = 1000.0;
    private const double CentiampsPerAmp = 100.0;

    /// <summary>Returns null for messages that carry no telemetry (commands, acknowledgements).</summary>
    public static TelemetryUpdate? Translate(IMavlinkMessage message, DateTimeOffset receivedAt) => message switch
    {
        HeartbeatMessage hb => new TelemetryUpdate(receivedAt, Flight: new FlightState(
            hb.IsArmed, FlightModeDecoder.Decode(hb.Autopilot, hb.Type, hb.BaseMode, hb.CustomMode))),

        GlobalPositionIntMessage gp => new TelemetryUpdate(receivedAt, Position: new GeoPosition(
            gp.LatitudeE7 / E7,
            gp.LongitudeE7 / E7,
            gp.AltitudeMslMillimetres / MillimetresPerMetre,
            gp.RelativeAltitudeMillimetres / MillimetresPerMetre)),

        AttitudeMessage at => new TelemetryUpdate(receivedAt, Attitude: new AttitudeAngles(
            RadiansToDegrees(at.Roll), RadiansToDegrees(at.Pitch), NormalizeHeading(RadiansToDegrees(at.Yaw)))),

        VfrHudMessage hud => new TelemetryUpdate(receivedAt, Motion: new MotionState(
            hud.Groundspeed, hud.Airspeed, hud.Climb, NormalizeHeading(hud.Heading))),

        SysStatusMessage sys => new TelemetryUpdate(receivedAt, Battery: new BatteryState(
            sys.VoltageBatteryMillivolts == UnknownVoltage ? null : sys.VoltageBatteryMillivolts / MillivoltsPerVolt,
            sys.CurrentBatteryCentiamps < 0 ? null : sys.CurrentBatteryCentiamps / CentiampsPerAmp,
            sys.BatteryRemainingPercent < 0 ? null : sys.BatteryRemainingPercent)),

        GpsRawIntMessage gps => new TelemetryUpdate(receivedAt, Gps: new GpsState(ToGpsFix(gps.FixType), gps.SatellitesVisible)),

        _ => null,
    };

    private static double RadiansToDegrees(float radians) => radians * 180.0 / Math.PI;

    private static double NormalizeHeading(double degrees) => ((degrees % 360) + 360) % 360;

    private static GpsFix ToGpsFix(GpsFixType fix) => fix switch
    {
        GpsFixType.NoGps => GpsFix.None,
        GpsFixType.NoFix => GpsFix.NoFix,
        GpsFixType.Fix2D => GpsFix.Fix2D,
        GpsFixType.Fix3D or GpsFixType.Static or GpsFixType.Ppp => GpsFix.Fix3D,
        GpsFixType.Dgps => GpsFix.Dgps,
        GpsFixType.RtkFloat => GpsFix.RtkFloat,
        GpsFixType.RtkFixed => GpsFix.RtkFixed,
        _ => GpsFix.None,
    };
}
