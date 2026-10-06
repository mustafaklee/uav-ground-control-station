using Gcs.Contracts.Common;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Abstractions;

/// <summary>
/// Loads and stores <see cref="Vehicle"/> aggregates for commands (register, update, retire).
/// The application layer defines what it needs; Gcs.Persistence decides how (EF Core + PostgreSQL).
/// </summary>
public interface IVehicleRepository
{
    Task<Vehicle?> GetByIdAsync(VehicleId id, CancellationToken cancellationToken);

    Task<bool> IsCallsignInUseAsync(Callsign callsign, VehicleId? excluding, CancellationToken cancellationToken);

    Task<bool> IsSystemIdInUseAsync(MavlinkSystemId systemId, VehicleId? excluding, CancellationToken cancellationToken);

    void Add(Vehicle vehicle);
}

/// <summary>
/// Read side for queries. Returns response DTOs directly (no aggregate loading, no change tracking),
/// which is cheaper and keeps read models free to differ from the domain model.
/// </summary>
public interface IVehicleQueries
{
    Task<VehicleResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<PagedResponse<VehicleResponse>> ListAsync(
        int page,
        int pageSize,
        VehicleStatus? status,
        string? search,
        CancellationToken cancellationToken);
}

/// <summary>
/// Commits all changes made in one use case atomically, together with the domain events they raised (outbox).
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">The row was changed by someone else since it was loaded.</exception>
    /// <exception cref="UniqueConstraintViolationException">A unique index rejected the change.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Raised by <see cref="IUnitOfWork"/> when an optimistic concurrency check fails at save time.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException()
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised by <see cref="IUnitOfWork"/> when the database rejects a duplicate. This is the last line of defence
/// behind the application's own "is it in use?" checks, which two simultaneous requests could both pass.
/// </summary>
public sealed class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException()
    {
    }

    public UniqueConstraintViolationException(string message)
        : base(message)
    {
    }

    public UniqueConstraintViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UniqueConstraintViolationException(string constraintName, string message, Exception innerException)
        : base(message, innerException)
    {
        ConstraintName = constraintName;
    }

    public string? ConstraintName { get; }
}
