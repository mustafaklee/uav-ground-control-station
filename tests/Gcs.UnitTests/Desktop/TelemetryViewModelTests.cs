using Gcs.Contracts.Vehicles;
using Gcs.Desktop.Services;
using Gcs.Desktop.ViewModels;

namespace Gcs.UnitTests.Desktop;

public sealed class TelemetryViewModelTests
{
    [Fact]
    public void Values_not_yet_reported_show_a_dash_never_zero()
    {
        var telemetry = new TelemetryViewModel();

        telemetry.Latitude.ShouldBe(TelemetryViewModel.NoValue);
        telemetry.Battery.ShouldBe(TelemetryViewModel.NoValue);
        telemetry.Position.ShouldBeNull();
    }

    [Fact]
    public void Telemetry_is_formatted_with_units()
    {
        var telemetry = new TelemetryViewModel();

        telemetry.Apply(DesktopTestData.Telemetry(Guid.NewGuid()));

        telemetry.Latitude.ShouldBe("39.925500°");
        telemetry.AltitudeRelative.ShouldBe("100.0 m");
        telemetry.GroundSpeed.ShouldBe("12.0 m/s");
        telemetry.ClimbRate.ShouldBe("-0.4 m/s");
        telemetry.Heading.ShouldBe("117°");
        telemetry.Roll.ShouldBe("5.6°");
        telemetry.Battery.ShouldBe("80%");
        telemetry.BatteryVoltage.ShouldBe("16.40 V");
        telemetry.Gps.ShouldBe("Fix3D · 14 sats");
        telemetry.Armed.ShouldBe("ARMED");
        telemetry.IsArmed.ShouldBeTrue();
        telemetry.HeadingDegrees.ShouldBe(117);
    }

    [Fact]
    public void Low_battery_is_flagged()
    {
        var telemetry = new TelemetryViewModel();

        telemetry.Apply(DesktopTestData.Telemetry(Guid.NewGuid(), battery: 18));

        telemetry.IsBatteryLow.ShouldBeTrue();
    }

    [Fact]
    public void Partial_updates_keep_previously_known_values()
    {
        var telemetry = new TelemetryViewModel();
        var id = Guid.NewGuid();
        telemetry.Apply(DesktopTestData.Telemetry(id));

        telemetry.Apply(new TelemetryResponse(id, DateTimeOffset.UnixEpoch, null, null, null, new BatteryDto(15.9, 14, 70), null, null));

        telemetry.Latitude.ShouldBe("39.925500°");
        telemetry.Battery.ShouldBe("70%");
    }

    [Fact]
    public void Reset_clears_everything_for_the_next_vehicle()
    {
        var telemetry = new TelemetryViewModel();
        telemetry.Apply(DesktopTestData.Telemetry(Guid.NewGuid()));

        telemetry.Reset();

        telemetry.Latitude.ShouldBe(TelemetryViewModel.NoValue);
        telemetry.IsArmed.ShouldBeFalse();
        telemetry.Position.ShouldBeNull();
    }

    [Theory]
    [InlineData(new string[0], "http://localhost:8080/")]
    [InlineData(new[] { "--api", "http://10.0.0.5:9000" }, "http://10.0.0.5:9000/")]
    [InlineData(new[] { "--api", "not a url" }, "http://localhost:8080/")]
    public void Api_address_comes_from_the_command_line_or_defaults(string[] args, string expected)
    {
        GcsClientOptions.FromEnvironment(args).ApiBaseUrl.ToString().ShouldBe(expected);
    }
}
