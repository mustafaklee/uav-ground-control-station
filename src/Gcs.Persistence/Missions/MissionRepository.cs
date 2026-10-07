using Gcs.Application.Abstractions;
using Gcs.Domain.Missions;
using Microsoft.EntityFrameworkCore;

namespace Gcs.Persistence.Missions;

internal sealed class MissionRepository(GcsDbContext db) : IMissionRepository
{
    public Task<Mission?> GetByIdAsync(MissionId id, CancellationToken cancellationToken) =>
        db.Missions.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public void Add(Mission mission) => db.Missions.Add(mission);
}

internal sealed class MissionQueries(GcsDbContext db) : IMissionQueries
{
    public Task<Mission?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var missionId = new MissionId(id);
        return db.Missions.AsNoTracking().SingleOrDefaultAsync(m => m.Id == missionId, cancellationToken);
    }

    public async Task<(IReadOnlyList<Mission> Items, int Total)> ListAsync(
        int page, int pageSize, bool includeArchived, CancellationToken cancellationToken)
    {
        var query = db.Missions.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(m => m.Status == MissionStatus.Draft);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(m => m.UpdatedAt)
            .ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }
}
