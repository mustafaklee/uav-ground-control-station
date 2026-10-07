using System.Net;
using Gcs.Contracts.Commands;
using Gcs.Desktop.Services;
using Gcs.Desktop.ViewModels;

namespace Gcs.UnitTests.Desktop;

public sealed class CommandPanelViewModelTests : IAsyncDisposable
{
    private readonly FakeApi _api = new() { Operator = "pilot-1" };
    private readonly FakeRealtime _realtime = new();
    private readonly ScriptedConfirmation _confirmation = new();
    private readonly MainWindowViewModel _main;

    public CommandPanelViewModelTests()
    {
        _main = new MainWindowViewModel(_api, _realtime, new ImmediateDispatcher(), new Uri("http://localhost:8080/"), _confirmation, "pilot-1");
    }

    private CommandPanelViewModel Panel => _main.Commands;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _realtime.DisposeAsync();

    [Fact]
    public async Task Commands_are_disabled_until_this_operator_takes_control_of_a_connected_vehicle()
    {
        var vehicle = await SelectVehicleAsync(connected: true);
        Panel.ControlText.ShouldBe("Nobody controls this vehicle.");
        Panel.LandCommand.CanExecute(null).ShouldBeFalse();
        Panel.FlightModes.ShouldBe(["AUTO.LOITER", "POSCTL"]);

        await Panel.TakeControlCommand.ExecuteAsync(null);

        _api.LeaseHolders[vehicle].ShouldBe("pilot-1");
        Panel.HasControl.ShouldBeTrue();
        Panel.ControlText.ShouldBe("You (pilot-1) control this vehicle.");
        Panel.LandCommand.CanExecute(null).ShouldBeTrue();
        Panel.ArmCommand.CanExecute(null).ShouldBeTrue();
        Panel.TakeControlCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task A_critical_command_asks_first_and_is_not_sent_when_the_operator_cancels()
    {
        await SelectVehicleAsync(connected: true);
        await Panel.TakeControlCommand.ExecuteAsync(null);
        _confirmation.Answer = false;

        await Panel.ArmCommand.ExecuteAsync(null);

        _confirmation.Asked.ShouldBe(["ARM UAV-01?"]);
        _api.SentCommands.ShouldBeEmpty();
        Panel.LastResult.ShouldBe("ARM cancelled.");
    }

    [Fact]
    public async Task A_confirmed_takeoff_is_sent_with_its_altitude_and_appears_in_the_history()
    {
        await SelectVehicleAsync(connected: true);
        await Panel.TakeControlCommand.ExecuteAsync(null);
        Panel.TakeoffAltitude = 25;

        await Panel.TakeoffCommand.ExecuteAsync(null);

        _api.SentCommands.ShouldHaveSingleItem().ShouldBe(new SendCommandRequest("Takeoff", Altitude: 25, Confirm: true));
        Panel.LastResult.ShouldBe("TAKEOFF: accepted by UAV-01 (1 attempt(s)).");
        Panel.LastResultIsError.ShouldBeFalse();
        Panel.History.ShouldHaveSingleItem().Command.ShouldBe("Takeoff");
    }

    [Fact]
    public async Task Land_and_rtl_are_one_click_without_a_dialog()
    {
        await SelectVehicleAsync(connected: true);
        await Panel.TakeControlCommand.ExecuteAsync(null);

        await Panel.LandCommand.ExecuteAsync(null);
        await Panel.ReturnToLaunchCommand.ExecuteAsync(null);

        _confirmation.Asked.ShouldBeEmpty();
        _api.SentCommands.Select(c => c.Command).ShouldBe(["Land", "ReturnToLaunch"]);
    }

    [Fact]
    public async Task A_timeout_from_the_vehicle_is_shown_as_an_error()
    {
        await SelectVehicleAsync(connected: true);
        await Panel.TakeControlCommand.ExecuteAsync(null);
        _api.CommandFailure = new ApiProblemException(
            HttpStatusCode.GatewayTimeout, "command.timed_out", "The vehicle did not acknowledge the command in time.", new Dictionary<string, string[]>());

        await Panel.ReturnToLaunchCommand.ExecuteAsync(null);

        Panel.LastResult.ShouldBe("RTL: The vehicle did not acknowledge the command in time.");
        Panel.LastResultIsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Another_operator_taking_control_is_pushed_and_disables_the_commands()
    {
        var vehicle = await SelectVehicleAsync(connected: true);

        _realtime.RaiseLease(new CommandLeaseResponse(vehicle, "ayse", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)));
        _api.LeaseHolders[vehicle] = "ayse";
        await Panel.TakeControlCommand.ExecuteAsync(null);

        Panel.ControlText.ShouldStartWith("Controlled by ayse until");
        Panel.LandCommand.CanExecute(null).ShouldBeFalse();
        Panel.LastResult.ShouldBe("ayse controls this vehicle.");
        Panel.LastResultIsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Commands_need_a_connected_link()
    {
        await SelectVehicleAsync(connected: false);
        await Panel.TakeControlCommand.ExecuteAsync(null);

        Panel.LandCommand.CanExecute(null).ShouldBeFalse();
        _realtime.RaiseLinkStatus(FakeApi.Link(_main.SelectedVehicle!.Id, "Connected"));
        Panel.LandCommand.CanExecute(null).ShouldBeTrue();
    }

    private async Task<Guid> SelectVehicleAsync(bool connected)
    {
        var vehicle = DesktopTestData.Vehicle("UAV-01");
        _api.Vehicles.Add(vehicle);
        _api.LinkStates[vehicle.Id] = connected ? "Connected" : "Disconnected";
        await _main.InitializeAsync(Ct);
        await Panel.LoadAsync(Ct); // selection change loads it too; awaiting here makes the test deterministic
        return vehicle.Id;
    }
}
