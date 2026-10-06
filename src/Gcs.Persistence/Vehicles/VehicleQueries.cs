using Gcs.Application.Abstractions;
using Gcs.Application.Vehicles;
using Gcs.Contracts.Common;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;

namespace Gcs.Persistence.Vehicles;

/// <summary>
/// Read-only queries. <c>AsNoTracking</c> skips EF Core change tracking because nothing will be saved,
/// which is noticeably cheaper for lists.
/// </summary>
internal sealed class VehicleQueries(GcsDbContext db) : IVehicleQueries
{
    public async Task<VehicleResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var vehicleId = new VehicleId(id);
        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(v => v.Id == vehicleId, cancellationToken);
        return vehicle is null ? null : VehicleMapping.ToResponse(vehicle);
    }

    public async Task<PagedResponse<VehicleResponse>> ListAsync(
        int page,
        int pageSize,
        VehicleStatus? status,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = db.Vehicles.AsNoTracking();

        if (status is not null)
        {
            query = query.Where(v => v.Status == status);
        }

        if (search is not null)
        {
            // Callsigns are stored upper case, so an upper-cased "contains" is a case-insensitive search.
            // Wildcards typed by the user are escaped so "%" cannot match everything.
            var pattern = $"%{EscapeLike(search.ToUpperInvariant())}%";
            query = query.Where(v => EF.Functions.Like((string)(object)v.Callsign, pattern, "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var vehicles = await query
            .OrderBy(v => v.Callsign)
            .ThenBy(v => v.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<VehicleResponse>([.. vehicles.Select(VehicleMapping.ToResponse)], page, pageSize, total);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
