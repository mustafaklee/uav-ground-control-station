using System.Text.RegularExpressions;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Domain.Commands;

/// <summary>
/// Who is acting, e.g. <c>operator01</c>: the signed-in user's name, taken from the access token (never from the client's
/// own claim). The rest of the system only sees this value.
/// </summary>
public sealed partial record OperatorName
{
    public const int MaxLength = 64;

    private OperatorName(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<OperatorName> Create(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > MaxLength || !AllowedPattern().IsMatch(trimmed)
            ? CommandErrors.OperatorRequired
            : new OperatorName(trimmed);
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^[A-Za-z0-9._@\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPattern();
}

/// <summary>
/// The right to command one vehicle, held by one operator for a limited time. Two operators sending conflicting commands
/// (one says LAND, the other TAKEOFF) is a real accident cause, so only the holder may command the vehicle.
/// The lease expires on its own: an operator whose GCS crashed or lost its network does not lock the vehicle forever.
/// </summary>
public sealed record CommandLease(VehicleId VehicleId, OperatorName Holder, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt)
{
    public bool IsActiveAt(DateTimeOffset now) => now < ExpiresAt;

    public bool IsHeldBy(OperatorName operatorName, DateTimeOffset now) => IsActiveAt(now) && Holder == operatorName;

    /// <summary>
    /// The lease rule in one place: a free or expired vehicle can be taken, the holder can renew (the acquisition time is
    /// kept, so "in control since" stays true), anyone else is refused while the lease is active.
    /// </summary>
    public static Result<CommandLease> Acquire(
        CommandLease? current, VehicleId vehicleId, OperatorName requester, DateTimeOffset now, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(requester);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A lease must last some time.");
        }

        if (current is null || !current.IsActiveAt(now))
        {
            return new CommandLease(vehicleId, requester, now, now + duration);
        }

        return current.Holder == requester
            ? current with { ExpiresAt = now + duration }
            : CommandErrors.LeaseHeldByOther(current.Holder, current.ExpiresAt);
    }

    /// <summary>Only the holder can give a lease back. Releasing a free or expired lease succeeds (nothing to do).</summary>
    public static Result CanRelease(CommandLease? current, OperatorName requester, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(requester);
        return current is null || !current.IsActiveAt(now) || current.Holder == requester
            ? Result.Success()
            : CommandErrors.LeaseHeldByOther(current.Holder, current.ExpiresAt);
    }
}
