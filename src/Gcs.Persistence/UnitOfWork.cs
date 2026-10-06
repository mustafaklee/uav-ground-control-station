using Gcs.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gcs.Persistence;

/// <summary>
/// Saves changes and translates database specific failures into application exceptions,
/// so the application layer never has to reference EF Core or Npgsql.
/// </summary>
internal sealed class UnitOfWork(GcsDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The entity was modified by another request.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new UniqueConstraintViolationException(pg.ConstraintName ?? string.Empty, "A unique constraint was violated.", ex);
        }
    }
}
