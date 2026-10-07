using System.Threading.Channels;
using Gcs.Application.Abstractions;
using Gcs.Mavlink.Commands;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;
using Gcs.Mavlink.Simulation;

namespace Gcs.UnitTests.Mavlink;

/// <summary>
/// The command protocol against the simulated vehicle, wired back to back through a channel. Each test changes the
/// "radio" in between or the vehicle's behaviour: lose packets, make it refuse, make it never answer.
/// </summary>
public sealed class CommandExchangeTests
{
    private const byte VehicleSystemId = 7;
    private static readonly CommandExchangeOptions Fast = new()
    {
        AckTimeout = TimeSpan.FromMilliseconds(100),
        MaxRetries = 3,
        InProgressTimeout = TimeSpan.FromMilliseconds(500),
    };

    private static readonly CommandLongMessage Arm = new(VehicleSystemId, MavComponent.Autopilot1, MavCmd.ComponentArmDisarm, 0, Param1: 1);

    private readonly SimulatedVehicle _vehicle = new(new SimulatedVehicleOptions { SystemId = VehicleSystemId, StartAirborne = false });
    private readonly Channel<CommandAckMessage> _acks = Channel.CreateUnbounded<CommandAckMessage>();
    private readonly List<CommandLongMessage> _sent = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_answered_command_is_accepted_after_one_transmission()
    {
        var delivery = await Exchange().RunAsync(Arm, Ct);

        delivery.ShouldBe(new CommandDelivery(CommandDeliveryStatus.Accepted, 1, null));
        _vehicle.IsArmed.ShouldBeTrue();
        _sent.ShouldHaveSingleItem().Confirmation.ShouldBe((byte)0);
    }

    [Fact]
    public async Task A_lost_command_is_sent_again_with_the_confirmation_counter_increased()
    {
        _vehicle.DropNextCommands = 2; // the first two transmissions never arrive

        var delivery = await Exchange().RunAsync(Arm, Ct);

        delivery.Status.ShouldBe(CommandDeliveryStatus.Accepted);
        delivery.Attempts.ShouldBe(3);
        _sent.Select(c => c.Confirmation).ShouldBe([(byte)0, (byte)1, (byte)2]);
    }

    [Fact]
    public async Task A_vehicle_that_never_answers_times_out_after_the_bounded_retries()
    {
        _vehicle.IgnoreCommands = true;
        var started = TimeProvider.System.GetTimestamp();

        var delivery = await Exchange().RunAsync(Arm, Ct);

        delivery.Status.ShouldBe(CommandDeliveryStatus.TimedOut);
        delivery.Attempts.ShouldBe(1 + Fast.MaxRetries);
        delivery.Detail.ShouldBe("No COMMAND_ACK after 4 attempts (0.1 s each).");
        _sent.Count.ShouldBe(1 + Fast.MaxRetries);
        TimeProvider.System.GetElapsedTime(started).ShouldBeGreaterThanOrEqualTo(Fast.AckTimeout * (1 + Fast.MaxRetries) * 0.9);
    }

    [Fact]
    public async Task A_refusal_is_final_and_not_retried()
    {
        // TAKEOFF while disarmed: the vehicle says DENIED. Asking again would not change its answer.
        var takeoff = new CommandLongMessage(VehicleSystemId, 1, MavCmd.NavTakeoff, 0, Param7: 968);

        var delivery = await Exchange().RunAsync(takeoff, Ct);

        delivery.ShouldBe(new CommandDelivery(CommandDeliveryStatus.Rejected, 1, "The vehicle answered Denied."));
        _sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task In_progress_waits_for_the_final_answer_without_resending()
    {
        var exchange = new CommandExchange(
            async (message, _) =>
            {
                _sent.Add((CommandLongMessage)message);
                _acks.Writer.TryWrite(new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.InProgress));
                await Task.Delay(300, Ct); // longer than the ACK timeout: only IN_PROGRESS keeps us from resending
                _acks.Writer.TryWrite(new CommandAckMessage(MavCmd.ComponentArmDisarm, MavResult.Accepted));
            },
            _acks.Reader, Fast, TimeProvider.System);

        var delivery = await exchange.RunAsync(Arm, Ct);

        delivery.Status.ShouldBe(CommandDeliveryStatus.Accepted);
        _sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Answers_to_other_commands_are_not_taken_as_this_commands_answer()
    {
        var exchange = new CommandExchange(
            (message, _) =>
            {
                _sent.Add((CommandLongMessage)message);
                _acks.Writer.TryWrite(new CommandAckMessage(MavCmd.NavLand, MavResult.Accepted));
                return ValueTask.CompletedTask;
            },
            _acks.Reader, Fast with { MaxRetries = 1 }, TimeProvider.System);

        var delivery = await exchange.RunAsync(Arm, Ct);

        delivery.Status.ShouldBe(CommandDeliveryStatus.TimedOut);
    }

    private CommandExchange Exchange() => new(
        (message, _) =>
        {
            _sent.Add((CommandLongMessage)message);
            if (_vehicle.Handle(message) is CommandAckMessage ack)
            {
                _acks.Writer.TryWrite(ack);
            }

            return ValueTask.CompletedTask;
        },
        _acks.Reader, Fast, TimeProvider.System);
}
