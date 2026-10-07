using Gcs.Domain.Common;

namespace Gcs.Domain.Vehicles;

/// <summary>
/// A registered vehicle: its identity and how to reach it. Live state (position, battery, link status)
/// is not part of this aggregate; it changes many times per second and is handled by the telemetry pipeline.
/// </summary>
public sealed class Vehicle : AggregateRoot<VehicleId>
{
    public const int InitialVersion = 1;

    private Vehicle(
        VehicleId id,
        Callsign callsign,
        MavlinkSystemId systemId,
        AutopilotType autopilot,
        VehicleType type,
        ConnectionSettings connection,
        DateTimeOffset now)
        : base(id)
    {
        Callsign = callsign;
        SystemId = systemId;
        Autopilot = autopilot;
        Type = type;
        Connection = connection;
        Status = VehicleStatus.Active;
        CreatedAt = now;
        UpdatedAt = now;
        Version = InitialVersion;
    }

    // Used by EF Core when loading from the database.
    private Vehicle()
        : base(default)
    {
        Callsign = null!;
        Connection = null!;
    }

    public Callsign Callsign { get; private set; }

    public MavlinkSystemId SystemId { get; private set; }

    public AutopilotType Autopilot { get; private set; }

    public VehicleType Type { get; private set; }

    public ConnectionSettings Connection { get; private set; }

    public VehicleStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Incremented on every change. Clients send back the version they edited; if it no longer matches,
    /// someone else changed the vehicle in the meantime and the edit is rejected instead of silently overwriting.
    /// </summary>
    public int Version { get; private set; }

    public bool IsRetired => Status == VehicleStatus.Retired;

    /// <summary>
    /// The operator wants a live link to this vehicle. Stored so links are restored after the GCS restarts:
    /// live link state itself lives in memory and is lost on restart, the operator's intent must not be.
    /// Operational state, not registration data, so changing it does not bump <see cref="Version"/>.
    /// </summary>
    public bool LinkRequested { get; private set; }

    public static Vehicle Register(
        Callsign callsign,
        MavlinkSystemId systemId,
        AutopilotType autopilot,
        VehicleType type,
        ConnectionSettings connection,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(connection);

        var vehicle = new Vehicle(VehicleId.New(), callsign, systemId, autopilot, type, connection, now);
        vehicle.RaiseDomainEvent(new VehicleRegistered(vehicle.Id, callsign, systemId, autopilot, now));
        return vehicle;
    }

    public Result Update(
        Callsign callsign,
        MavlinkSystemId systemId,
        AutopilotType autopilot,
        VehicleType type,
        ConnectionSettings connection,
        int expectedVersion,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(callsign);
        ArgumentNullException.ThrowIfNull(connection);

        if (expectedVersion != Version)
        {
            return VehicleErrors.VersionMismatch;
        }

        if (IsRetired)
        {
            return VehicleErrors.Retired;
        }

        var unchanged = callsign == Callsign
            && systemId == SystemId
            && autopilot == Autopilot
            && type == Type
            && connection == Connection;
        if (unchanged)
        {
            return Result.Success();
        }

        Callsign = callsign;
        SystemId = systemId;
        Autopilot = autopilot;
        Type = type;
        Connection = connection;
        Touch(now);

        RaiseDomainEvent(new VehicleUpdated(Id, Callsign, Version, now));
        return Result.Success();
    }

    /// <summary>Records that the operator wants this vehicle connected. Retired vehicles cannot be connected.</summary>
    public Result RequestLink()
    {
        if (IsRetired)
        {
            return VehicleErrors.Retired;
        }

        LinkRequested = true;
        return Result.Success();
    }

    /// <summary>Records that the operator ended the link on purpose; it will not be restored after a restart.</summary>
    public void ReleaseLink() => LinkRequested = false;

    /// <summary>
    /// Takes the vehicle out of service. Retiring an already retired vehicle succeeds without doing anything,
    /// so a repeated DELETE request (for example after a network timeout) is harmless.
    /// </summary>
    public Result Retire(int? expectedVersion, DateTimeOffset now)
    {
        if (expectedVersion is not null && expectedVersion != Version)
        {
            return VehicleErrors.VersionMismatch;
        }

        if (IsRetired)
        {
            return Result.Success();
        }

        Status = VehicleStatus.Retired;
        LinkRequested = false;
        Touch(now);

        RaiseDomainEvent(new VehicleRetired(Id, Callsign, now));
        return Result.Success();
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}
