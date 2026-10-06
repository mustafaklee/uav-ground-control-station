using Gcs.Domain.Vehicles;

namespace Gcs.UnitTests.Domain.Vehicles;

public sealed class VehicleValueObjectTests
{
    [Theory]
    [InlineData("UAV-01", "UAV-01")]
    [InlineData("uav-01", "UAV-01")]
    [InlineData("  Alpha7  ", "ALPHA7")]
    [InlineData("A-B-C", "A-B-C")]
    public void Valid_callsign_is_normalized_to_upper_case(string input, string expected)
    {
        var result = Callsign.Create(input);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "vehicle.callsign.length")]
    [InlineData("", "vehicle.callsign.length")]
    [InlineData("AB", "vehicle.callsign.length")]
    [InlineData("UAV 01", "vehicle.callsign.format")]
    [InlineData("-UAV", "vehicle.callsign.format")]
    [InlineData("UAV--01", "vehicle.callsign.format")]
    [InlineData("UAV_01", "vehicle.callsign.format")]
    [InlineData("İHA-01", "vehicle.callsign.format")]
    public void Invalid_callsign_is_rejected_with_a_specific_error(string? input, string expectedCode)
    {
        var result = Callsign.Create(input);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void Callsign_longer_than_maximum_is_rejected()
    {
        Callsign.Create(new string('A', Callsign.MaxLength + 1)).IsSuccess.ShouldBeFalse();
        Callsign.Create(new string('A', Callsign.MaxLength)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(255)]
    public void System_id_within_range_is_accepted(int value)
    {
        MavlinkSystemId.Create(value).Value.Value.ShouldBe((byte)value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(256)]
    public void System_id_outside_range_is_rejected(int value)
    {
        MavlinkSystemId.Create(value).Error!.Code.ShouldBe("vehicle.system_id.range");
    }

    [Fact]
    public void Udp_settings_keep_host_and_port()
    {
        var settings = ConnectionSettings.Udp(" 192.168.1.10 ", 14550).Value;

        settings.Transport.ShouldBe(TransportType.Udp);
        settings.Host.ShouldBe("192.168.1.10");
        settings.Port.ShouldBe(14550);
        settings.SerialPortName.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, 14550, "vehicle.connection.host")]
    [InlineData("", 14550, "vehicle.connection.host")]
    [InlineData("my host", 14550, "vehicle.connection.host")]
    [InlineData("localhost", 0, "vehicle.connection.port")]
    [InlineData("localhost", 65536, "vehicle.connection.port")]
    public void Invalid_network_settings_are_rejected(string? host, int port, string expectedCode)
    {
        ConnectionSettings.Tcp(host, port).Error!.Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void Serial_settings_require_a_supported_baud_rate()
    {
        ConnectionSettings.Serial("COM3", 57600).IsSuccess.ShouldBeTrue();
        ConnectionSettings.Serial("COM3", 12345).Error!.Code.ShouldBe("vehicle.connection.baud_rate");
        ConnectionSettings.Serial(" ", 57600).Error!.Code.ShouldBe("vehicle.connection.serial_port");
    }

    [Fact]
    public void Settings_with_same_values_are_equal()
    {
        ConnectionSettings.Udp("localhost", 14550).Value.ShouldBe(ConnectionSettings.Udp("localhost", 14550).Value);
        ConnectionSettings.Udp("localhost", 14550).Value.ShouldNotBe(ConnectionSettings.Tcp("localhost", 14550).Value);
    }
}
