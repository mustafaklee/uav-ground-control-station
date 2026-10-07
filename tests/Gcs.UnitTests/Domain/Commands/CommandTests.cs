using Gcs.Domain.Commands;
using Gcs.Domain.Vehicles;

namespace Gcs.UnitTests.Domain.Commands;

public sealed class CommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly VehicleId Vehicle = VehicleId.New();
    private static readonly OperatorName Ali = OperatorName.Create("ali").Value;
    private static readonly OperatorName Ayse = OperatorName.Create("ayse").Value;

    [Theory]
    [InlineData(VehicleCommandType.Arm, true)]
    [InlineData(VehicleCommandType.Disarm, true)]
    [InlineData(VehicleCommandType.Takeoff, true)]
    [InlineData(VehicleCommandType.SetMode, true)]
    [InlineData(VehicleCommandType.Land, false)]
    [InlineData(VehicleCommandType.ReturnToLaunch, false)]
    public void Commands_that_start_or_stop_flight_need_confirmation_but_safety_commands_do_not(VehicleCommandType type, bool critical)
    {
        var command = VehicleCommand.Create(
            type, type == VehicleCommandType.Takeoff ? 30 : null, type == VehicleCommandType.SetMode ? "posctl" : null).Value;

        command.RequiresConfirmation.ShouldBe(critical);
    }

    [Fact]
    public void Parameters_are_validated_and_only_accepted_by_the_command_they_belong_to()
    {
        VehicleCommand.Takeoff(1).Error!.Code.ShouldBe("command.takeoff.altitude");
        VehicleCommand.Takeoff(null).Error!.Code.ShouldBe("command.takeoff.altitude");
        VehicleCommand.Takeoff(double.NaN).Error!.Code.ShouldBe("command.takeoff.altitude");
        VehicleCommand.SetMode("  ").Error!.Code.ShouldBe("command.mode.required");
        VehicleCommand.Create(VehicleCommandType.Land, 30, null).Error!.Code.ShouldBe("command.parameter_unexpected");

        VehicleCommand.SetMode(" auto.loiter ").Value.Mode.ShouldBe("AUTO.LOITER");
        VehicleCommand.Takeoff(30).Value.DescribeParameters().ShouldBe("altitude=30");
    }

    [Fact]
    public void A_free_vehicle_can_be_taken_and_the_holder_renews_without_losing_the_start_time()
    {
        var taken = CommandLease.Acquire(null, Vehicle, Ali, Now, Minute).Value;
        var renewed = CommandLease.Acquire(taken, Vehicle, Ali, Now.AddSeconds(20), Minute).Value;

        taken.ShouldBe(new CommandLease(Vehicle, Ali, Now, Now + Minute));
        renewed.AcquiredAt.ShouldBe(Now);
        renewed.ExpiresAt.ShouldBe(Now.AddSeconds(20) + Minute);
    }

    [Fact]
    public void Another_operator_is_refused_while_the_lease_is_active_and_may_take_it_once_it_expires()
    {
        var aliLease = CommandLease.Acquire(null, Vehicle, Ali, Now, Minute).Value;

        var whileActive = CommandLease.Acquire(aliLease, Vehicle, Ayse, Now.AddSeconds(59), Minute);
        var afterExpiry = CommandLease.Acquire(aliLease, Vehicle, Ayse, Now.AddSeconds(60), Minute);

        whileActive.Error!.Code.ShouldBe("command.lease_held");
        whileActive.Error.Message.ShouldStartWith("ali controls this vehicle until 12:01:00 UTC");
        afterExpiry.Value.Holder.ShouldBe(Ayse);
    }

    [Fact]
    public void Only_the_holder_can_release_and_releasing_a_free_vehicle_succeeds()
    {
        var aliLease = CommandLease.Acquire(null, Vehicle, Ali, Now, Minute).Value;

        CommandLease.CanRelease(aliLease, Ayse, Now).Error!.Code.ShouldBe("command.lease_held");
        CommandLease.CanRelease(aliLease, Ali, Now).IsSuccess.ShouldBeTrue();
        CommandLease.CanRelease(null, Ayse, Now).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public void Operator_names_must_be_simple_identifiers(string? name) =>
        OperatorName.Create(name).Error!.Code.ShouldBe("command.operator_required");

    [Fact]
    public void An_audit_entry_is_completed_exactly_once()
    {
        var entry = CommandAuditEntry.Start(Vehicle, Callsign.Create("UAV-01").Value, Ali, VehicleCommand.Arm(), Now);
        entry.Outcome.ShouldBe(CommandOutcome.Pending);

        entry.Complete(CommandOutcome.Accepted, null, 2, Now.AddSeconds(1));

        entry.Attempts.ShouldBe(2);
        entry.CompletedAt.ShouldBe(Now.AddSeconds(1));
        Should.Throw<InvalidOperationException>(() => entry.Complete(CommandOutcome.Rejected, "again", 1, Now));
    }
}
