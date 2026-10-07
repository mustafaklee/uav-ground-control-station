using Gcs.Application.Abstractions;
using Gcs.Application.Common;
using Gcs.Application.Vehicles;
using Gcs.Contracts.Commands;
using Gcs.Contracts.Common;
using Gcs.Domain.Commands;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Commands;

/// <summary>Use case: take control of a vehicle, or extend control you already have.</summary>
public sealed class AcquireCommandLeaseHandler(IVehicleRepository vehicles, ICommandLeaseStore leases, ILiveUpdatePublisher live)
{
    public async Task<Result<CommandLeaseResponse>> HandleAsync(Guid vehicleId, string? operatorName, CancellationToken cancellationToken)
    {
        var requester = OperatorName.Create(operatorName);
        if (!requester.IsSuccess)
        {
            return requester.Error;
        }

        var vehicle = await vehicles.GetByIdAsync(new VehicleId(vehicleId), cancellationToken);
        if (vehicle is null)
        {
            return VehicleErrors.NotFound;
        }

        if (vehicle.IsRetired)
        {
            return VehicleErrors.Retired;
        }

        var previousHolder = leases.Find(vehicle.Id)?.Holder;
        var lease = leases.Acquire(vehicle.Id, requester.Value);
        if (!lease.IsSuccess)
        {
            return lease.Error;
        }

        var response = CommandMapping.ToResponse(vehicle.Id, lease.Value);
        if (previousHolder != requester.Value)
        {
            // Renewals are not pushed: they happen every few seconds and change nothing anyone needs to see.
            await live.PublishCommandLeaseAsync(response, cancellationToken);
        }

        return response;
    }
}

/// <summary>Use case: give control back. Idempotent for the holder; refused for anyone else.</summary>
public sealed class ReleaseCommandLeaseHandler(IVehicleQueries vehicles, ICommandLeaseStore leases, ILiveUpdatePublisher live)
{
    public async Task<Result> HandleAsync(Guid vehicleId, string? operatorName, CancellationToken cancellationToken)
    {
        var requester = OperatorName.Create(operatorName);
        if (!requester.IsSuccess)
        {
            return requester.Error;
        }

        if (await vehicles.GetByIdAsync(vehicleId, cancellationToken) is null)
        {
            return VehicleErrors.NotFound;
        }

        var id = new VehicleId(vehicleId);
        var wasHeld = leases.Find(id) is not null;
        var release = leases.Release(id, requester.Value);
        if (release.IsSuccess && wasHeld)
        {
            await live.PublishCommandLeaseAsync(CommandMapping.ToResponse(id, null), cancellationToken);
        }

        return release;
    }
}

public sealed class GetCommandLeaseHandler(IVehicleQueries vehicles, ICommandLeaseStore leases)
{
    public async Task<Result<CommandLeaseResponse>> HandleAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        if (await vehicles.GetByIdAsync(vehicleId, cancellationToken) is null)
        {
            return VehicleErrors.NotFound;
        }

        var id = new VehicleId(vehicleId);
        return CommandMapping.ToResponse(id, leases.Find(id));
    }
}

/// <summary>Use case: which flight modes SetMode accepts for this vehicle.</summary>
public sealed class GetFlightModesHandler(IVehicleRepository vehicles, IFlightModeCatalog modes)
{
    public async Task<Result<FlightModesResponse>> HandleAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.GetByIdAsync(new VehicleId(vehicleId), cancellationToken);
        return vehicle is null
            ? VehicleErrors.NotFound
            : new FlightModesResponse(vehicleId, modes.GetModes(vehicle.Autopilot, vehicle.Type));
    }
}

/// <summary>
/// Use case: send one command to a vehicle and report what the vehicle answered.
/// <code>
/// validate ─► vehicle exists, active ─► caller holds the lease? ── no ──► audit "Refused", 409
///                                             │ yes (lease renewed)
///                                             ▼
///             audit "Pending" (saved) ─► link: COMMAND_LONG, wait COMMAND_ACK, retry ─► audit outcome (saved) ─► answer
/// </code>
/// The audit row is saved before the command leaves: if the database is down, the command is not sent at all
/// (no command without a record). Once the command is on its way, the request's cancellation is ignored: a closed
/// browser tab must not leave the audit entry without an outcome.
/// </summary>
public sealed class SendVehicleCommandHandler(
    IVehicleRepository vehicles,
    ICommandLeaseStore leases,
    IFlightModeCatalog modes,
    IVehicleCommandSender sender,
    ICommandAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<Result<CommandAuditResponse>> HandleAsync(
        Guid vehicleId, string? operatorName, SendCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requester = OperatorName.Create(operatorName);
        if (!requester.IsSuccess)
        {
            return requester.Error;
        }

        var command = Parse(request);
        if (!command.IsSuccess)
        {
            return command.Error;
        }

        if (command.Value.RequiresConfirmation && !request.Confirm)
        {
            return CommandErrors.ConfirmationRequired;
        }

        var vehicle = await vehicles.GetByIdAsync(new VehicleId(vehicleId), cancellationToken);
        if (vehicle is null)
        {
            return VehicleErrors.NotFound;
        }

        if (vehicle.IsRetired)
        {
            return VehicleErrors.Retired;
        }

        if (command.Value.Type == VehicleCommandType.SetMode)
        {
            var available = modes.GetModes(vehicle.Autopilot, vehicle.Type);
            if (!available.Contains(command.Value.Mode!, StringComparer.Ordinal))
            {
                return CommandErrors.UnknownMode(available);
            }
        }

        var lease = leases.Find(vehicle.Id);
        var now = clock.GetUtcNowForStorage();
        if (lease is null || !lease.IsHeldBy(requester.Value, now))
        {
            var refusal = lease is null ? CommandErrors.LeaseRequired : CommandErrors.LeaseHeldByOther(lease.Holder, lease.ExpiresAt);
            audit.Add(CommandAuditEntry.Refused(vehicle.Id, vehicle.Callsign, requester.Value, command.Value, refusal.Message, now));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return refusal;
        }

        // Commanding is "being in control": every command extends the lease.
        leases.Acquire(vehicle.Id, requester.Value);

        var entry = CommandAuditEntry.Start(vehicle.Id, vehicle.Callsign, requester.Value, command.Value, now);
        audit.Add(entry);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var delivery = await sender.SendAsync(vehicle.Id, command.Value, CancellationToken.None);
        var (outcome, error) = Interpret(delivery);
        entry.Complete(outcome, delivery.Detail ?? error?.Message, delivery.Attempts, clock.GetUtcNowForStorage());
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        if (error is null)
        {
            return CommandMapping.ToResponse(entry);
        }

        // Keep the stable code, but say what the vehicle (or link) actually reported.
        return delivery.Detail is null ? error : error with { Message = $"{error.Message} {delivery.Detail}" };
    }

    private static Result<VehicleCommand> Parse(SendCommandRequest request) =>
        VehicleMapping.TryParseEnum<VehicleCommandType>(request.Command, out var type)
            ? VehicleCommand.Create(type, request.Altitude, request.Mode)
            : CommandErrors.UnknownCommand;

    private static (CommandOutcome Outcome, Error? Error) Interpret(CommandDelivery delivery) => delivery.Status switch
    {
        CommandDeliveryStatus.Accepted => (CommandOutcome.Accepted, null),
        CommandDeliveryStatus.Rejected => (CommandOutcome.Rejected, CommandErrors.Rejected),
        CommandDeliveryStatus.TimedOut => (CommandOutcome.TimedOut, CommandErrors.TimedOut),
        CommandDeliveryStatus.NotConnected => (CommandOutcome.Refused, CommandErrors.NotConnected),
        CommandDeliveryStatus.AlreadyInFlight => (CommandOutcome.Refused, CommandErrors.AlreadyInFlight),
        CommandDeliveryStatus.Unsupported => (CommandOutcome.Refused, CommandErrors.Unsupported),
        _ => throw new ArgumentOutOfRangeException(nameof(delivery), delivery.Status, "Unhandled delivery status."),
    };
}

/// <summary>Use case: read a vehicle's command audit log, newest first.</summary>
public sealed class ListCommandAuditHandler(IVehicleQueries vehicles, ICommandAuditQueries audit)
{
    public const int MaxPageSize = 100;

    public async Task<Result<PagedResponse<CommandAuditResponse>>> HandleAsync(
        Guid vehicleId, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1 || pageSize is < 1 or > MaxPageSize)
        {
            return new Error(ValidationErrors.Code, "One or more query parameters are invalid.", ErrorType.Validation)
            {
                Details = new Dictionary<string, string[]>
                {
                    ["page"] = ["Page must be 1 or greater."],
                    ["pageSize"] = [$"Page size must be between 1 and {MaxPageSize}."],
                },
            };
        }

        if (await vehicles.GetByIdAsync(vehicleId, cancellationToken) is null)
        {
            return VehicleErrors.NotFound;
        }

        var entries = await audit.ListAsync(new VehicleId(vehicleId), page, pageSize, cancellationToken);
        return new PagedResponse<CommandAuditResponse>(
            [.. entries.Items.Select(CommandMapping.ToResponse)], entries.Page, entries.PageSize, entries.TotalCount);
    }
}

public static class CommandMapping
{
    public static CommandLeaseResponse ToResponse(VehicleId vehicleId, CommandLease? lease) =>
        new(vehicleId.Value, lease?.Holder.Value, lease?.AcquiredAt, lease?.ExpiresAt);

    public static CommandAuditResponse ToResponse(CommandAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new CommandAuditResponse(
            entry.Id.Value,
            entry.VehicleId.Value,
            entry.Callsign,
            entry.Operator,
            entry.Command.ToString(),
            entry.Parameters,
            entry.Outcome.ToString(),
            entry.Detail,
            entry.Attempts,
            entry.Source,
            entry.RequestedAt,
            entry.CompletedAt);
    }
}
