using System.ComponentModel.DataAnnotations;
using Gcs.Application.Abstractions;
using Gcs.Domain.Commands;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;
using Microsoft.Extensions.Options;

namespace Gcs.Infrastructure.Commands;

public sealed class CommandLeaseOptions
{
    public const string SectionName = "Commands";

    /// <summary>
    /// How long control lasts without activity. Every command and every renewal (the desktop renews every third of this)
    /// extends it. Short enough that a crashed GCS frees the vehicle within a minute, long enough to survive a network blip.
    /// </summary>
    [Range(5, 3600)]
    public int LeaseSeconds { get; init; } = 60;
}

/// <summary>
/// Leases kept in memory. Deliberately so: links are in memory too (one API instance owns the radio links), and a lease
/// on a vehicle whose link died with the process means nothing. After a restart every operator takes control again.
/// Running several API instances needs a shared store (PostgreSQL row lock or Redis); see ADR-014.
/// </summary>
internal sealed class InMemoryCommandLeaseStore(IOptions<CommandLeaseOptions> options, TimeProvider time) : ICommandLeaseStore
{
    private readonly Dictionary<VehicleId, CommandLease> _leases = [];
    private readonly Lock _gate = new();

    public CommandLease? Find(VehicleId vehicleId)
    {
        lock (_gate)
        {
            return _leases.TryGetValue(vehicleId, out var lease) && lease.IsActiveAt(time.GetUtcNow()) ? lease : null;
        }
    }

    public Result<CommandLease> Acquire(VehicleId vehicleId, OperatorName requester)
    {
        // The read-decide-write sequence runs under one lock: two "take control" requests cannot both see a free vehicle.
        lock (_gate)
        {
            var lease = CommandLease.Acquire(
                _leases.GetValueOrDefault(vehicleId), vehicleId, requester, Now(), TimeSpan.FromSeconds(options.Value.LeaseSeconds));
            if (lease.IsSuccess)
            {
                _leases[vehicleId] = lease.Value;
            }

            return lease;
        }
    }

    public Result Release(VehicleId vehicleId, OperatorName requester)
    {
        lock (_gate)
        {
            var release = CommandLease.CanRelease(_leases.GetValueOrDefault(vehicleId), requester, Now());
            if (release.IsSuccess)
            {
                _leases.Remove(vehicleId);
            }

            return release;
        }
    }

    /// <summary>Whole microseconds, like every other stored timestamp, so API responses compare equal across reads.</summary>
    private DateTimeOffset Now()
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % (TimeSpan.TicksPerMillisecond / 1000)));
    }
}
