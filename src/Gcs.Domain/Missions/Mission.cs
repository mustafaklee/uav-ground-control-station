using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Domain.Missions;

public enum MissionStatus
{
    Draft = 1,

    /// <summary>No longer used; kept for history (which mission a vehicle flew).</summary>
    Archived = 2,
}

/// <summary>A rule a mission must satisfy before it may be uploaded. <c>ItemIndex</c> is null for whole-mission rules.</summary>
public sealed record MissionIssue(int? ItemIndex, string Code, string Message);

/// <summary>Result of the last upload of this mission to a vehicle.</summary>
public sealed record MissionUpload(VehicleId VehicleId, bool Succeeded, DateTimeOffset At, string? Error);

/// <summary>
/// A flight plan: an ordered list of items edited as a whole. Drafts may be incomplete while an operator works on them;
/// <see cref="Validate"/> lists what still prevents flying, and upload is refused until it returns nothing.
/// </summary>
public sealed class Mission : AggregateRoot<MissionId>
{
    public const int MaxNameLength = 64;
    public const int MaxItems = 500;
    public const int InitialVersion = 1;
    private const double EarthRadiusMetres = 6_371_000;

    private readonly List<MissionItem> _items = [];

    private Mission(MissionId id, string name, DateTimeOffset now)
        : base(id)
    {
        Name = name;
        Status = MissionStatus.Draft;
        CreatedAt = now;
        UpdatedAt = now;
        Version = InitialVersion;
    }

    // Used by EF Core when loading from the database.
    private Mission()
        : base(default)
    {
        Name = null!;
    }

    public string Name { get; private set; }

    public IReadOnlyList<MissionItem> Items => _items;

    public MissionStatus Status { get; private set; }

    public MissionUpload? LastUpload { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Version { get; private set; }

    public bool IsArchived => Status == MissionStatus.Archived;

    /// <summary>Length of the path through all positioned items, in metres (great-circle distance).</summary>
    public double TotalDistanceMetres
    {
        get
        {
            var positioned = _items.Where(i => i.HasPosition).ToList();
            return positioned.Zip(positioned.Skip(1), Distance).Sum();
        }
    }

    public static Result<Mission> Create(string? name, IReadOnlyList<MissionItem> items, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return MissionErrors.NameLength;
        }

        if (items.Count > MaxItems)
        {
            return MissionErrors.TooManyItems;
        }

        var mission = new Mission(MissionId.New(), trimmed, now);
        mission._items.AddRange(items);
        mission.RaiseDomainEvent(new MissionCreated(mission.Id, mission.Name, items.Count, now));
        return mission;
    }

    public Result Update(string? name, IReadOnlyList<MissionItem> items, int expectedVersion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (expectedVersion != Version)
        {
            return MissionErrors.VersionMismatch;
        }

        if (IsArchived)
        {
            return MissionErrors.Archived;
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return MissionErrors.NameLength;
        }

        if (items.Count > MaxItems)
        {
            return MissionErrors.TooManyItems;
        }

        Name = trimmed;
        _items.Clear();
        _items.AddRange(items);
        UpdatedAt = now;
        Version++;
        RaiseDomainEvent(new MissionUpdated(Id, Version, items.Count, now));
        return Result.Success();
    }

    public Result Archive(int? expectedVersion, DateTimeOffset now)
    {
        if (expectedVersion is not null && expectedVersion != Version)
        {
            return MissionErrors.VersionMismatch;
        }

        if (IsArchived)
        {
            return Result.Success();
        }

        Status = MissionStatus.Archived;
        UpdatedAt = now;
        Version++;
        return Result.Success();
    }

    /// <summary>
    /// Whole-mission rules for a flyable plan. Item-level rules (ranges, required fields) are already guaranteed by
    /// <see cref="MissionItem.Create"/>; these are about order and structure.
    /// </summary>
    public IReadOnlyList<MissionIssue> Validate()
    {
        var issues = new List<MissionIssue>();
        if (_items.Count < 2)
        {
            issues.Add(new(null, "mission.too_short", "A mission needs at least a takeoff and a final land or return-to-launch item."));
            return issues;
        }

        if (_items[0].Command != MissionCommand.Takeoff)
        {
            issues.Add(new(0, "mission.first_not_takeoff", "The first item must be a takeoff."));
        }

        var last = _items[^1].Command;
        if (last is not (MissionCommand.Land or MissionCommand.ReturnToLaunch))
        {
            issues.Add(new(_items.Count - 1, "mission.last_not_terminal", "The last item must be land or return to launch."));
        }

        for (var i = 1; i < _items.Count; i++)
        {
            if (_items[i].Command == MissionCommand.Takeoff)
            {
                issues.Add(new(i, "mission.takeoff_not_first", "Takeoff is only allowed as the first item."));
            }

            if (i < _items.Count - 1 && _items[i].Command is MissionCommand.Land or MissionCommand.ReturnToLaunch)
            {
                issues.Add(new(i, "mission.terminal_not_last", "Nothing can follow a land or return-to-launch item."));
            }
        }

        if (!_items.Any(item => item.Command is MissionCommand.Waypoint or MissionCommand.Loiter))
        {
            issues.Add(new(null, "mission.no_waypoints", "The mission has no waypoint or loiter item."));
        }

        return issues;
    }

    /// <summary>Records an upload attempt. Operational state: does not change the plan's version.</summary>
    public void RecordUpload(VehicleId vehicleId, bool succeeded, string? error, DateTimeOffset now)
    {
        LastUpload = new MissionUpload(vehicleId, succeeded, now, succeeded ? null : error);
        if (succeeded)
        {
            RaiseDomainEvent(new MissionUploaded(Id, vehicleId, _items.Count, now));
        }
    }

    private static double Distance(MissionItem a, MissionItem b)
    {
        var lat1 = a.Latitude!.Value * Math.PI / 180;
        var lat2 = b.Latitude!.Value * Math.PI / 180;
        var dLat = lat2 - lat1;
        var dLon = (b.Longitude!.Value - a.Longitude!.Value) * Math.PI / 180;
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2), 2));
        return 2 * EarthRadiusMetres * Math.Asin(Math.Sqrt(h));
    }
}

public sealed record MissionCreated(MissionId MissionId, string Name, int ItemCount, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record MissionUpdated(MissionId MissionId, int Version, int ItemCount, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record MissionUploaded(MissionId MissionId, VehicleId VehicleId, int ItemCount, DateTimeOffset OccurredAt) : IDomainEvent;
