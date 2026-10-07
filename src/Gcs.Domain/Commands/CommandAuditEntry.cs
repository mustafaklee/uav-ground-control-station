using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Domain.Commands;

public readonly record struct CommandAuditId(Guid Value)
{
    public static CommandAuditId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum CommandOutcome
{
    /// <summary>Recorded, being sent. A row left in this state means the API stopped before the answer arrived.</summary>
    Pending = 1,

    /// <summary>The vehicle acknowledged and accepted the command.</summary>
    Accepted = 2,

    /// <summary>The vehicle answered and refused (e.g. DISARM in flight).</summary>
    Rejected = 3,

    /// <summary>No answer after every retry. The vehicle may or may not have executed it.</summary>
    TimedOut = 4,

    /// <summary>The GCS did not send it: no lease, not connected, or the same command still in flight.</summary>
    Refused = 5,
}

/// <summary>
/// One line of the command audit log: who sent what to which vehicle, when, and what came of it.
/// Append-only by design: an entry is created, completed once, and never edited or deleted afterwards.
/// </summary>
public sealed class CommandAuditEntry : Entity<CommandAuditId>
{
    public const int MaxDetailLength = 256;
    public const string ApiSource = "GCS-API";

    private CommandAuditEntry(
        CommandAuditId id, VehicleId vehicleId, string callsign, OperatorName operatorName, VehicleCommand command, DateTimeOffset now)
        : base(id)
    {
        VehicleId = vehicleId;
        Callsign = callsign;
        Operator = operatorName.Value;
        Command = command.Type;
        Parameters = command.DescribeParameters();
        Source = ApiSource;
        Outcome = CommandOutcome.Pending;
        RequestedAt = now;
    }

    // Used by EF Core when loading from the database.
    private CommandAuditEntry()
        : base(default)
    {
        Callsign = null!;
        Operator = null!;
        Source = null!;
    }

    public VehicleId VehicleId { get; private set; }

    /// <summary>Copied at the time of the command: the log must still read correctly if the vehicle is renamed later.</summary>
    public string Callsign { get; private set; }

    public string Operator { get; private set; }

    public VehicleCommandType Command { get; private set; }

    public string? Parameters { get; private set; }

    /// <summary>Which system the command came through (today always the API; later e.g. a mission script).</summary>
    public string Source { get; private set; }

    public CommandOutcome Outcome { get; private set; }

    public string? Detail { get; private set; }

    /// <summary>How many times the command was transmitted (1 = answered on the first try).</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Recorded before anything is sent ("write-ahead"): if the API crashes mid-command, the attempt is still on file.</summary>
    public static CommandAuditEntry Start(VehicleId vehicleId, Callsign callsign, OperatorName operatorName, VehicleCommand command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(operatorName);
        ArgumentNullException.ThrowIfNull(command);
        return new CommandAuditEntry(CommandAuditId.New(), vehicleId, callsign.Value, operatorName, command, now);
    }

    /// <summary>A request the GCS turned down itself. Recorded too: refused attempts are exactly what an investigation looks for.</summary>
    public static CommandAuditEntry Refused(
        VehicleId vehicleId, Callsign callsign, OperatorName operatorName, VehicleCommand command, string detail, DateTimeOffset now)
    {
        var entry = Start(vehicleId, callsign, operatorName, command, now);
        entry.Complete(CommandOutcome.Refused, detail, 0, now);
        return entry;
    }

    public void Complete(CommandOutcome outcome, string? detail, int attempts, DateTimeOffset now)
    {
        if (Outcome != CommandOutcome.Pending)
        {
            throw new InvalidOperationException("An audit entry is completed once and never changed afterwards.");
        }

        if (outcome == CommandOutcome.Pending)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Completing needs a final outcome.");
        }

        Outcome = outcome;
        Detail = detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail;
        Attempts = attempts;
        CompletedAt = now;
    }
}
