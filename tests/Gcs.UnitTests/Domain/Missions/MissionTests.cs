using Gcs.Domain.Missions;
using Gcs.Domain.Vehicles;

namespace Gcs.UnitTests.Domain.Missions;

public sealed class MissionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MissionCommand.Waypoint, null, null, 50.0, null, null, "mission.item.position_required")]
    [InlineData(MissionCommand.Waypoint, 39.9, null, 50.0, null, null, "mission.item.position_incomplete")]
    [InlineData(MissionCommand.Waypoint, 91.0, 32.8, 50.0, null, null, "mission.item.position_range")]
    [InlineData(MissionCommand.Waypoint, 39.9, 32.8, null, null, null, "mission.item.altitude_required")]
    [InlineData(MissionCommand.Waypoint, 39.9, 32.8, 1.0, null, null, "mission.item.altitude_range")]
    [InlineData(MissionCommand.Waypoint, 39.9, 32.8, 501.0, null, null, "mission.item.altitude_range")]
    [InlineData(MissionCommand.Waypoint, 39.9, 32.8, 50.0, -1.0, null, "mission.item.hold")]
    [InlineData(MissionCommand.Waypoint, 39.9, 32.8, 50.0, null, 41.0, "mission.item.speed")]
    [InlineData(MissionCommand.Loiter, 39.9, 32.8, 50.0, null, null, "mission.item.hold")]
    [InlineData(MissionCommand.Takeoff, null, null, null, null, null, "mission.item.altitude_required")]
    [InlineData(MissionCommand.ReturnToLaunch, 39.9, 32.8, null, null, null, "mission.item.rtl_position")]
    public void Invalid_items_are_rejected_with_a_specific_code(
        MissionCommand command, double? lat, double? lon, double? alt, double? hold, double? speed, string expectedCode)
    {
        MissionItem.Create(command, lat, lon, alt, hold, speed).Error!.Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void Valid_items_of_every_kind_can_be_created()
    {
        MissionItem.Create(MissionCommand.Takeoff, altitude: 30).IsSuccess.ShouldBeTrue();
        MissionItem.Create(MissionCommand.Waypoint, 39.9, 32.8, 50, holdSeconds: 5, speed: 10).IsSuccess.ShouldBeTrue();
        MissionItem.Create(MissionCommand.Loiter, 39.9, 32.8, 50, holdSeconds: 30).IsSuccess.ShouldBeTrue();
        MissionItem.Create(MissionCommand.ReturnToLaunch).IsSuccess.ShouldBeTrue();
        MissionItem.Create(MissionCommand.Land).IsSuccess.ShouldBeTrue();
        MissionItem.Create(MissionCommand.Land, 39.9, 32.8).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_well_formed_mission_has_no_issues()
    {
        var mission = NewMission(Takeoff(), Waypoint(39.9255, 32.8662), Waypoint(39.9265, 32.8672), Rtl());

        mission.Validate().ShouldBeEmpty();
    }

    [Fact]
    public void Structural_problems_are_reported_with_the_item_they_belong_to()
    {
        var mission = NewMission(Waypoint(39.9255, 32.8662), Takeoff(), Rtl(), Waypoint(39.9265, 32.8672));

        var issues = mission.Validate();

        issues.Select(i => (i.ItemIndex, i.Code)).ShouldBe(
        [
            (0, "mission.first_not_takeoff"),
            (3, "mission.last_not_terminal"),
            (1, "mission.takeoff_not_first"),
            (2, "mission.terminal_not_last"),
        ], ignoreOrder: true);
    }

    [Fact]
    public void A_mission_without_waypoints_is_not_flyable()
    {
        NewMission(Takeoff(), Land()).Validate().ShouldHaveSingleItem().Code.ShouldBe("mission.no_waypoints");
    }

    [Fact]
    public void An_empty_draft_is_allowed_but_not_flyable()
    {
        var mission = NewMission();

        mission.Validate().ShouldHaveSingleItem().Code.ShouldBe("mission.too_short");
    }

    [Fact]
    public void Total_distance_follows_the_positioned_items()
    {
        // 0.001° of latitude ≈ 111.2 m; two legs north.
        var mission = NewMission(Takeoff(), Waypoint(39.925, 32.866), Waypoint(39.926, 32.866), Waypoint(39.927, 32.866), Rtl());

        mission.TotalDistanceMetres.ShouldBe(222.4, tolerance: 0.5);
    }

    [Fact]
    public void Updating_bumps_the_version_and_requires_the_current_one()
    {
        var mission = NewMission(Takeoff(), Waypoint(39.9255, 32.8662), Rtl());

        mission.Update("Renamed", [Takeoff(), Rtl()], expectedVersion: 1, T0).IsSuccess.ShouldBeTrue();
        mission.Version.ShouldBe(2);
        mission.Name.ShouldBe("Renamed");
        mission.Update("Again", [], expectedVersion: 1, T0).Error.ShouldBe(MissionErrors.VersionMismatch);
    }

    [Fact]
    public void Archived_missions_cannot_be_changed()
    {
        var mission = NewMission(Takeoff(), Rtl());
        mission.Archive(null, T0);

        mission.Update("x", [], mission.Version, T0).Error.ShouldBe(MissionErrors.Archived);
    }

    [Fact]
    public void Successful_uploads_raise_an_event_and_do_not_change_the_version()
    {
        var mission = NewMission(Takeoff(), Waypoint(39.9255, 32.8662), Rtl());
        mission.ClearDomainEvents();
        var vehicle = VehicleId.New();

        mission.RecordUpload(vehicle, succeeded: true, error: null, T0);

        mission.LastUpload.ShouldBe(new MissionUpload(vehicle, true, T0, null));
        mission.Version.ShouldBe(Mission.InitialVersion);
        mission.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<MissionUploaded>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Mission_needs_a_name(string name)
    {
        Mission.Create(name, [], T0).Error.ShouldBe(MissionErrors.NameLength);
    }

    private static Mission NewMission(params MissionItem[] items) => Mission.Create("Survey A", items, T0).Value;

    private static MissionItem Takeoff() => MissionItem.Create(MissionCommand.Takeoff, altitude: 30).Value;

    private static MissionItem Waypoint(double lat, double lon) => MissionItem.Create(MissionCommand.Waypoint, lat, lon, 50).Value;

    private static MissionItem Rtl() => MissionItem.Create(MissionCommand.ReturnToLaunch).Value;

    private static MissionItem Land() => MissionItem.Create(MissionCommand.Land).Value;
}
