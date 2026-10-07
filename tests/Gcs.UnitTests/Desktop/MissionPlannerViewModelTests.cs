using Gcs.Contracts.Missions;
using Gcs.Desktop.ViewModels;

namespace Gcs.UnitTests.Desktop;

public sealed class MissionPlannerViewModelTests
{
    private readonly FakeApi _api = new();
    private readonly MissionPlannerViewModel _planner;
    private VehicleItemViewModel? _vehicle;

    public MissionPlannerViewModelTests()
    {
        _planner = new MissionPlannerViewModel(_api, () => _vehicle);
        _planner.NewMissionCommand.Execute(null);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_new_mission_starts_with_takeoff_and_return_and_click_to_add_on()
    {
        _planner.Items.Select(i => i.Command).ShouldBe(["Takeoff", "ReturnToLaunch"]);
        _planner.Items[0].Altitude.ShouldBe(MissionItemViewModel.DefaultTakeoffAltitude);
        _planner.IsAddingWaypoints.ShouldBeTrue();
        _planner.IsSaved.ShouldBeFalse();
    }

    [Fact]
    public void Map_clicks_insert_waypoints_before_the_final_return_and_keep_the_last_altitude()
    {
        _planner.AddWaypointAt(39.93, 32.85);
        _planner.Items[1].Altitude = 80;
        _planner.AddWaypointAt(39.94, 32.86);

        _planner.Items.Select(i => i.Command).ShouldBe(["Takeoff", "Waypoint", "Waypoint", "ReturnToLaunch"]);
        _planner.Items[2].Altitude.ShouldBe(80);
        _planner.Items.Select(i => i.Index).ShouldBe([0, 1, 2, 3]);
        _planner.SelectedItem.ShouldBe(_planner.Items[2]);
    }

    [Fact]
    public void Distance_follows_the_route_while_editing()
    {
        _planner.AddWaypointAt(39.9000, 32.8500);
        _planner.Distance.ShouldBe("0 m");

        _planner.AddWaypointAt(39.9100, 32.8500); // 0.01° of latitude ≈ 1112 m

        _planner.Distance.ShouldBe("1.11 km");
    }

    [Fact]
    public void Edits_and_reordering_raise_route_changed_so_the_map_redraws()
    {
        _planner.AddWaypointAt(39.90, 32.85);
        _planner.AddWaypointAt(39.91, 32.85);
        var redraws = 0;
        _planner.RouteChanged += (_, _) => redraws++;

        _planner.Items[1].Latitude = 39.95;
        _planner.MoveUpCommand.Execute(null);

        redraws.ShouldBe(2);
        _planner.Items[1].Latitude.ShouldBe(39.91);
        _planner.Items[2].Latitude.ShouldBe(39.95);
    }

    [Fact]
    public void Move_and_remove_are_only_possible_where_they_make_sense()
    {
        _planner.SelectedItem = _planner.Items[0];
        _planner.MoveUpCommand.CanExecute(null).ShouldBeFalse();
        _planner.MoveDownCommand.CanExecute(null).ShouldBeTrue();

        _planner.SelectedItem = _planner.Items[^1];
        _planner.MoveDownCommand.CanExecute(null).ShouldBeFalse();

        _planner.RemoveItemCommand.Execute(null);
        _planner.Items.Count.ShouldBe(1);
        _planner.SelectedItem.ShouldBe(_planner.Items[0]);
    }

    [Fact]
    public async Task Saving_an_unflyable_mission_shows_the_server_issues_on_the_rows()
    {
        _planner.Items.RemoveAt(0); // no takeoff

        await _planner.SaveCommand.ExecuteAsync(null);

        _planner.IsSaved.ShouldBeTrue();
        _planner.IsFlyable.ShouldBeFalse();
        _planner.Issues.ShouldContain("Item 1: The first item must be a takeoff.");
        _planner.Issues.ShouldContain("Add at least one waypoint.");
        _planner.Items[0].Issue.ShouldBe("The first item must be a takeoff.");
    }

    [Fact]
    public async Task Saving_twice_creates_then_updates_with_the_current_version()
    {
        _planner.AddWaypointAt(39.90, 32.85);
        await _planner.SaveCommand.ExecuteAsync(null);
        _planner.Name = "Survey north field";

        await _planner.SaveCommand.ExecuteAsync(null);

        var saved = _api.SavedMissions.Values.ShouldHaveSingleItem();
        saved.Version.ShouldBe(2);
        saved.Name.ShouldBe("Survey north field");
        _planner.IsFlyable.ShouldBeTrue();
        _planner.Missions.ShouldHaveSingleItem().Id.ShouldBe(saved.Id);
        _planner.SelectedMission!.Id.ShouldBe(saved.Id);
    }

    [Fact]
    public async Task A_stale_version_is_reported_instead_of_overwriting_someone_elses_change()
    {
        _planner.AddWaypointAt(39.90, 32.85);
        await _planner.SaveCommand.ExecuteAsync(null);
        var id = _api.SavedMissions.Keys.Single();
        _api.SavedMissions[id] = _api.SavedMissions[id] with { Version = 7 }; // edited elsewhere

        await _planner.SaveCommand.ExecuteAsync(null);

        _planner.Status.ShouldBe("The mission was changed by someone else.");
        _api.SavedMissions[id].Version.ShouldBe(7);
    }

    [Fact]
    public async Task Upload_needs_a_selected_vehicle()
    {
        _planner.AddWaypointAt(39.90, 32.85);

        await _planner.UploadCommand.ExecuteAsync(null);

        _planner.Status.ShouldBe("Select a vehicle in the Flight tab first.");
        _api.Uploads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Upload_saves_first_then_sends_the_mission_to_the_selected_vehicle()
    {
        _vehicle = new VehicleItemViewModel(DesktopTestData.Vehicle("UAV-01"));
        _planner.AddWaypointAt(39.90, 32.85);

        await _planner.UploadCommand.ExecuteAsync(null);

        var (missionId, vehicleId) = _api.Uploads.ShouldHaveSingleItem();
        missionId.ShouldBe(_api.SavedMissions.Keys.Single());
        vehicleId.ShouldBe(_vehicle.Id);
        _planner.Status!.ShouldStartWith("Uploaded to UAV-01");
    }

    [Fact]
    public async Task An_unflyable_mission_is_saved_but_not_uploaded()
    {
        _vehicle = new VehicleItemViewModel(DesktopTestData.Vehicle("UAV-01"));

        await _planner.UploadCommand.ExecuteAsync(null); // no waypoints

        _api.SavedMissions.ShouldHaveSingleItem();
        _api.Uploads.ShouldBeEmpty();
        _planner.Status.ShouldBe("Saved as draft (see issues).");
    }

    [Fact]
    public async Task Reading_from_the_vehicle_replaces_the_plan_as_an_unsaved_copy()
    {
        _vehicle = new VehicleItemViewModel(DesktopTestData.Vehicle("UAV-01"));
        _api.OnVehicle.AddRange([new MissionItemDto("Takeoff", Altitude: 20), new MissionItemDto("Waypoint", 39.9, 32.8, 40), new MissionItemDto("Land")]);

        await _planner.DownloadFromVehicleCommand.ExecuteAsync(null);

        _planner.Items.Select(i => i.Command).ShouldBe(["Takeoff", "Waypoint", "Land"]);
        _planner.Name.ShouldBe("From UAV-01");
        _planner.IsSaved.ShouldBeFalse();
    }

    [Fact]
    public async Task Selecting_a_saved_mission_loads_it_and_archive_removes_it()
    {
        _planner.AddWaypointAt(39.90, 32.85);
        await _planner.SaveCommand.ExecuteAsync(null);
        _planner.NewMissionCommand.Execute(null);
        await _planner.LoadMissionsAsync(Ct);

        _planner.SelectedMission = _planner.Missions[0];

        _planner.Items.Count.ShouldBe(3);
        _planner.IsSaved.ShouldBeTrue();

        await _planner.ArchiveCommand.ExecuteAsync(null);

        _api.SavedMissions.ShouldBeEmpty();
        _planner.Missions.ShouldBeEmpty();
        _planner.Status.ShouldBe("Mission archived.");
    }

    [Theory]
    [InlineData("39.9334", 39.9334)]
    [InlineData("39,9334", 39.9334)]
    [InlineData("", null)]
    public void Number_boxes_accept_dot_or_comma_and_empty_means_not_set(string text, double? expected)
    {
        NullableDoubleConverter.Instance.ConvertBack(text, typeof(double?), null, System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))
            .ShouldBe(expected);
    }
}
