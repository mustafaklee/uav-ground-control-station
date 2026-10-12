using Gcs.Desktop.Services;
using Gcs.Desktop.ViewModels;

namespace Gcs.UnitTests.Desktop;

public sealed class MainWindowViewModelTests : IAsyncDisposable
{
    private readonly FakeApi _api = new();
    private readonly FakeRealtime _realtime = new();
    private readonly MainWindowViewModel _viewModel;

    public MainWindowViewModelTests()
    {
        _viewModel = new MainWindowViewModel(_api, _realtime, new ImmediateDispatcher(), new Uri("http://localhost:8080/"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _realtime.DisposeAsync();

    [Fact]
    public async Task Initialize_starts_the_live_connection_and_loads_the_fleet_sorted_with_link_states()
    {
        var bravo = DesktopTestData.Vehicle("UAV-02");
        var alpha = DesktopTestData.Vehicle("UAV-01");
        _api.Vehicles.AddRange([bravo, alpha]);
        _api.LinkStates[alpha.Id] = "Connected";

        await _viewModel.InitializeAsync(Ct);

        _realtime.Calls.ShouldContain("start");
        _viewModel.Vehicles.Select(v => v.Callsign).ShouldBe(["UAV-01", "UAV-02"]);
        _viewModel.Vehicles[0].LinkState.ShouldBe("Connected");
        _viewModel.SelectedVehicle.ShouldBe(_viewModel.Vehicles[0]);
    }

    [Fact]
    public async Task Initialize_prepares_a_blank_mission_without_turning_on_click_to_add()
    {
        await _viewModel.InitializeAsync(Ct);

        _viewModel.Planner.Items.Select(i => i.Command).ShouldBe(["Takeoff", "ReturnToLaunch"]);
        _viewModel.Planner.IsAddingWaypoints.ShouldBeFalse();
    }

    [Fact]
    public async Task Selecting_a_vehicle_moves_the_telemetry_subscription_and_shows_its_latest_state()
    {
        var a = DesktopTestData.Vehicle("UAV-01");
        var b = DesktopTestData.Vehicle("UAV-02");
        _api.Vehicles.AddRange([a, b]);
        _api.Latest[b.Id] = DesktopTestData.Telemetry(b.Id, latitude: 40.1);
        await _viewModel.InitializeAsync(Ct);

        _viewModel.SelectedVehicle = _viewModel.Vehicles[1];

        _realtime.Calls.ShouldContain($"unsubscribe {a.Id}");
        _realtime.Calls[^1].ShouldBe($"subscribe {b.Id}");
        _viewModel.Telemetry.Latitude.ShouldBe("40.100000°");
    }

    [Fact]
    public async Task Live_telemetry_updates_only_the_selected_vehicle()
    {
        var a = DesktopTestData.Vehicle("UAV-01");
        var b = DesktopTestData.Vehicle("UAV-02");
        _api.Vehicles.AddRange([a, b]);
        await _viewModel.InitializeAsync(Ct);

        _realtime.RaiseTelemetry(DesktopTestData.Telemetry(b.Id, latitude: 41));
        _viewModel.Telemetry.Latitude.ShouldBe(TelemetryViewModel.NoValue);

        _realtime.RaiseTelemetry(DesktopTestData.Telemetry(a.Id, latitude: 39.5));
        _viewModel.Telemetry.Latitude.ShouldBe("39.500000°");
        _viewModel.Telemetry.FlightMode.ShouldBe("AUTO.LOITER");
    }

    [Fact]
    public async Task Link_status_push_updates_the_list_and_toggles_connect_and_disconnect()
    {
        var a = DesktopTestData.Vehicle("UAV-01");
        _api.Vehicles.Add(a);
        await _viewModel.InitializeAsync(Ct);
        _viewModel.ConnectCommand.CanExecute(null).ShouldBeTrue();
        _viewModel.DisconnectCommand.CanExecute(null).ShouldBeFalse();

        _realtime.RaiseLinkStatus(FakeApi.Link(a.Id, "Connected"));

        _viewModel.Vehicles[0].IsConnected.ShouldBeTrue();
        _viewModel.ConnectCommand.CanExecute(null).ShouldBeFalse();
        _viewModel.DisconnectCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Connect_and_disconnect_call_the_api_for_the_selected_vehicle()
    {
        var a = DesktopTestData.Vehicle("UAV-01");
        _api.Vehicles.Add(a);
        await _viewModel.InitializeAsync(Ct);

        await _viewModel.ConnectCommand.ExecuteAsync(null);
        _realtime.RaiseLinkStatus(FakeApi.Link(a.Id, "Connected"));
        await _viewModel.DisconnectCommand.ExecuteAsync(null);

        _api.Calls.ShouldBe([("connect", a.Id), ("disconnect", a.Id)]);
    }

    [Fact]
    public async Task Unreachable_backend_is_reported_instead_of_crashing()
    {
        _api.Unreachable = true;

        await _viewModel.InitializeAsync(Ct);

        _viewModel.ErrorMessage.ShouldNotBeNull().ShouldContain("http://localhost:8080/");
        _viewModel.Vehicles.ShouldBeEmpty();
    }

    [Fact]
    public void Backend_connection_state_is_shown_in_the_status_bar()
    {
        _realtime.RaiseState(BackendConnectionState.Reconnecting);

        _viewModel.BackendStatus.ShouldBe("Backend: Reconnecting");
    }

    [Fact]
    public async Task Link_rows_are_marked_not_live_while_the_backend_connection_is_down()
    {
        _api.Vehicles.Add(DesktopTestData.Vehicle("UAV-01"));
        _realtime.RaiseState(BackendConnectionState.Connected);
        await _viewModel.InitializeAsync(Ct);
        _viewModel.Vehicles[0].IsStale.ShouldBeFalse();

        _realtime.RaiseState(BackendConnectionState.Reconnecting);
        _viewModel.Vehicles[0].IsStale.ShouldBeTrue();
        _viewModel.BackendStateText.ShouldBe("Reconnecting");

        _realtime.RaiseState(BackendConnectionState.Connected);
        _viewModel.Vehicles[0].IsStale.ShouldBeFalse();
        _viewModel.BackendStateText.ShouldBe("Online");
    }

    [Fact]
    public void The_navigation_rail_switches_pages_and_click_to_add_opens_the_plan()
    {
        _viewModel.IsFlightPage.ShouldBeTrue();

        _viewModel.ShowPageCommand.Execute(AppPage.Mission);
        _viewModel.IsMissionPage.ShouldBeTrue();
        _viewModel.IsFlightPage.ShouldBeFalse();
        _viewModel.PageTitle.ShouldBe("Mission planning");

        _viewModel.IsFlightPage = true;
        _viewModel.CurrentPage.ShouldBe(AppPage.Flight);

        _viewModel.Planner.IsAddingWaypoints = true;
        _viewModel.CurrentPage.ShouldBe(AppPage.Mission);
    }

    [Fact]
    public void A_backend_error_can_be_dismissed()
    {
        _viewModel.ErrorMessage = "Cannot reach the backend.";

        _viewModel.DismissErrorCommand.Execute(null);

        _viewModel.ErrorMessage.ShouldBeNull();
    }
}
