using Gcs.Contracts.Common;
using Gcs.Domain.Commands;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Abstractions;

/// <summary>
/// Holds command leases. Implementations must make acquire and release atomic per vehicle: two operators pressing
/// "take control" in the same millisecond must not both win.
/// </summary>
public interface ICommandLeaseStore
{
    /// <summary>The active lease, or null when the vehicle is free (expired leases count as free).</summary>
    CommandLease? Find(VehicleId vehicleId);

    /// <summary>Takes a free lease or renews one the requester already holds.</summary>
    Result<CommandLease> Acquire(VehicleId vehicleId, OperatorName requester);

    Result Release(VehicleId vehicleId, OperatorName requester);
}

public enum CommandDeliveryStatus
{
    Accepted,
    Rejected,
    TimedOut,
    NotConnected,

    /// <summary>The same command is still waiting for its answer from this vehicle.</summary>
    AlreadyInFlight,

    /// <summary>The command cannot be expressed for this vehicle (e.g. the needed telemetry has not arrived yet).</summary>
    Unsupported,
}

/// <summary>What happened to a command on the link. <c>Attempts</c> counts transmissions (retries included).</summary>
public sealed record CommandDelivery(CommandDeliveryStatus Status, int Attempts, string? Detail);

/// <summary>Sends commands over the live link and waits for the vehicle's answer. Implemented by Gcs.Mavlink.</summary>
public interface IVehicleCommandSender
{
    /// <summary>Never throws for link problems; every outcome, including timeouts, is a <see cref="CommandDelivery"/>.</summary>
    Task<CommandDelivery> SendAsync(VehicleId vehicleId, VehicleCommand command, CancellationToken cancellationToken);
}

/// <summary>Flight modes per autopilot. The names are the ones telemetry reports, so what you set is what you see.</summary>
public interface IFlightModeCatalog
{
    IReadOnlyList<string> GetModes(AutopilotType autopilot, VehicleType type);
}

public interface ICommandAuditLog
{
    void Add(CommandAuditEntry entry);
}

public interface ICommandAuditQueries
{
    /// <summary>Entries of one vehicle, newest first.</summary>
    Task<PagedResponse<CommandAuditEntry>> ListAsync(VehicleId vehicleId, int page, int pageSize, CancellationToken cancellationToken);
}
