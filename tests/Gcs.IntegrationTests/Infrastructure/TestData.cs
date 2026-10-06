using Gcs.Contracts.Vehicles;

namespace Gcs.IntegrationTests.Infrastructure;

/// <summary>
/// Unique test values. Tests share one database, so every test registers vehicles with its own callsign and
/// MAVLink system id instead of relying on a clean table.
/// </summary>
internal static class TestData
{
    private static int _lastSystemId;
    private static int _lastCallsign;

    /// <summary>MAVLink allows only 255 ids; tests retire vehicles they do not need, so ids are reused after wrap-around.</summary>
    public static int NextSystemId() => (Interlocked.Increment(ref _lastSystemId) % 255) + 1;

    public static string NextCallsign(string prefix = "TST") => $"{prefix}-{Interlocked.Increment(ref _lastCallsign):D4}";

    public static RegisterVehicleRequest Registration(string? callsign = null, int? systemId = null) => new(
        callsign ?? NextCallsign(),
        systemId ?? NextSystemId(),
        "Px4",
        "Multirotor",
        new ConnectionSettingsDto("Udp", Host: "127.0.0.1", Port: 14550));
}
