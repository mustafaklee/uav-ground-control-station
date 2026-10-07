using Gcs.Domain.Vehicles;

namespace Gcs.UnitTests.Domain.Vehicles;

public sealed class VehicleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(5);

    [Fact]
    public void Registering_creates_an_active_vehicle_at_version_1_and_raises_an_event()
    {
        var vehicle = NewVehicle();

        vehicle.Status.ShouldBe(VehicleStatus.Active);
        vehicle.Version.ShouldBe(Vehicle.InitialVersion);
        vehicle.CreatedAt.ShouldBe(T0);
        var registered = vehicle.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<VehicleRegistered>();
        registered.VehicleId.ShouldBe(vehicle.Id);
        registered.Callsign.Value.ShouldBe("UAV-01");
    }

    [Fact]
    public void Updating_with_the_current_version_changes_fields_and_bumps_the_version()
    {
        var vehicle = NewVehicle();
        vehicle.ClearDomainEvents();

        var result = vehicle.Update(Callsign("UAV-02"), SystemId(2), AutopilotType.ArduPilot, VehicleType.FixedWing,
            ConnectionSettings.Tcp("10.0.0.5", 5760).Value, expectedVersion: 1, T1);

        result.IsSuccess.ShouldBeTrue();
        vehicle.Callsign.Value.ShouldBe("UAV-02");
        vehicle.Autopilot.ShouldBe(AutopilotType.ArduPilot);
        vehicle.Version.ShouldBe(2);
        vehicle.UpdatedAt.ShouldBe(T1);
        vehicle.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<VehicleUpdated>().Version.ShouldBe(2);
    }

    [Fact]
    public void Updating_with_an_outdated_version_is_rejected_and_changes_nothing()
    {
        var vehicle = NewVehicle();

        var result = vehicle.Update(Callsign("UAV-02"), SystemId(1), AutopilotType.Px4, VehicleType.Multirotor,
            ConnectionSettings.Simulator(), expectedVersion: 7, T1);

        result.Error.ShouldBe(VehicleErrors.VersionMismatch);
        vehicle.Callsign.Value.ShouldBe("UAV-01");
        vehicle.Version.ShouldBe(1);
    }

    [Fact]
    public void Updating_with_identical_values_does_not_bump_version_or_raise_an_event()
    {
        var vehicle = NewVehicle();
        vehicle.ClearDomainEvents();

        var result = vehicle.Update(vehicle.Callsign, vehicle.SystemId, vehicle.Autopilot, vehicle.Type,
            ConnectionSettings.Udp("127.0.0.1", 14550).Value, expectedVersion: 1, T1);

        result.IsSuccess.ShouldBeTrue();
        vehicle.Version.ShouldBe(1);
        vehicle.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Retiring_marks_the_vehicle_retired_and_raises_an_event()
    {
        var vehicle = NewVehicle();
        vehicle.ClearDomainEvents();

        vehicle.Retire(expectedVersion: null, T1).IsSuccess.ShouldBeTrue();

        vehicle.IsRetired.ShouldBeTrue();
        vehicle.Version.ShouldBe(2);
        vehicle.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<VehicleRetired>();
    }

    [Fact]
    public void Retiring_twice_is_harmless()
    {
        var vehicle = NewVehicle();
        vehicle.Retire(null, T1);
        vehicle.ClearDomainEvents();

        vehicle.Retire(null, T1).IsSuccess.ShouldBeTrue();

        vehicle.Version.ShouldBe(2);
        vehicle.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Retired_vehicle_cannot_be_updated()
    {
        var vehicle = NewVehicle();
        vehicle.Retire(null, T1);

        var result = vehicle.Update(Callsign("UAV-09"), SystemId(9), AutopilotType.Px4, VehicleType.Multirotor,
            ConnectionSettings.Simulator(), expectedVersion: vehicle.Version, T1);

        result.Error.ShouldBe(VehicleErrors.Retired);
    }

    [Fact]
    public void Retiring_with_an_outdated_version_is_rejected()
    {
        var vehicle = NewVehicle();

        vehicle.Retire(expectedVersion: 5, T1).Error.ShouldBe(VehicleErrors.VersionMismatch);
        vehicle.IsRetired.ShouldBeFalse();
    }

    [Fact]
    public void Requesting_and_releasing_a_link_does_not_change_the_registration_version()
    {
        var vehicle = NewVehicle();

        vehicle.RequestLink().IsSuccess.ShouldBeTrue();
        vehicle.LinkRequested.ShouldBeTrue();
        vehicle.ReleaseLink();

        vehicle.LinkRequested.ShouldBeFalse();
        vehicle.Version.ShouldBe(Vehicle.InitialVersion);
    }

    [Fact]
    public void Retiring_clears_the_link_request_and_a_retired_vehicle_cannot_request_one()
    {
        var vehicle = NewVehicle();
        vehicle.RequestLink();

        vehicle.Retire(null, T1);

        vehicle.LinkRequested.ShouldBeFalse();
        vehicle.RequestLink().Error.ShouldBe(VehicleErrors.Retired);
    }

    private static Vehicle NewVehicle() => Vehicle.Register(
        Callsign("UAV-01"),
        SystemId(1),
        AutopilotType.Px4,
        VehicleType.Multirotor,
        ConnectionSettings.Udp("127.0.0.1", 14550).Value,
        T0);

    private static Callsign Callsign(string value) => Gcs.Domain.Vehicles.Callsign.Create(value).Value;

    private static MavlinkSystemId SystemId(int value) => MavlinkSystemId.Create(value).Value;
}
