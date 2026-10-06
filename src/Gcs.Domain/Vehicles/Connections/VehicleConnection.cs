using Gcs.Domain.Common;

namespace Gcs.Domain.Vehicles.Connections;

/// <summary>
/// Live link status of one vehicle. Kept in memory by the MAVLink layer (Phase 3), never persisted:
/// it changes several times per second and is meaningless after a restart.
/// </summary>
/// <remarks>Not thread safe. The owning connection manager serializes access per vehicle.</remarks>
public sealed class VehicleConnection
{
    public VehicleConnection(VehicleId vehicleId, int maxReconnectAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReconnectAttempts);

        VehicleId = vehicleId;
        MaxReconnectAttempts = maxReconnectAttempts;
    }

    public VehicleId VehicleId { get; }

    /// <summary>Upper bound on retries, so a dead link ends in Faulted instead of an endless reconnect loop.</summary>
    public int MaxReconnectAttempts { get; }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    public int ReconnectAttempts { get; private set; }

    public string? FaultReason { get; private set; }

    public Result BeginConnect()
    {
        var result = MoveTo(ConnectionState.Connecting);
        if (result.IsSuccess)
        {
            FaultReason = null;
        }

        return result;
    }

    /// <summary>A heartbeat arrived. Completes a connect or reconnect, or simply refreshes a healthy link.</summary>
    public Result HeartbeatReceived(DateTimeOffset at)
    {
        if (State == ConnectionState.Connected)
        {
            LastHeartbeatAt = at;
            return Result.Success();
        }

        var result = MoveTo(ConnectionState.Connected);
        if (result.IsSuccess)
        {
            LastHeartbeatAt = at;
            ReconnectAttempts = 0;
        }

        return result;
    }

    public Result HeartbeatLost() => MoveTo(ConnectionState.Reconnecting);

    /// <summary>One reconnect try failed. After <see cref="MaxReconnectAttempts"/> failures the link is Faulted.</summary>
    public Result ReconnectAttemptFailed()
    {
        if (State != ConnectionState.Reconnecting)
        {
            return InvalidTransition(State, ConnectionState.Reconnecting);
        }

        ReconnectAttempts++;
        return ReconnectAttempts >= MaxReconnectAttempts
            ? Fault($"No heartbeat after {ReconnectAttempts} reconnect attempts.")
            : Result.Success();
    }

    public Result Fault(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var result = MoveTo(ConnectionState.Faulted);
        if (result.IsSuccess)
        {
            FaultReason = reason;
        }

        return result;
    }

    public Result Disconnect()
    {
        if (State == ConnectionState.Disconnected)
        {
            return Result.Success();
        }

        var result = MoveTo(ConnectionState.Disconnected);
        if (result.IsSuccess)
        {
            ReconnectAttempts = 0;
        }

        return result;
    }

    private Result MoveTo(ConnectionState target)
    {
        if (!ConnectionStateMachine.CanTransition(State, target))
        {
            return InvalidTransition(State, target);
        }

        State = target;
        return Result.Success();
    }

    private static Error InvalidTransition(ConnectionState from, ConnectionState to) =>
        Error.Conflict("vehicle.connection.invalid_transition", $"Cannot move a connection from {from} to {to}.");
}
