using Gcs.Contracts.Common;
using Gcs.Domain.Common;
using Gcs.Domain.Missions;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Abstractions;

public interface IMissionRepository
{
    Task<Mission?> GetByIdAsync(MissionId id, CancellationToken cancellationToken);

    void Add(Mission mission);
}

/// <summary>Read side: missions without change tracking.</summary>
public interface IMissionQueries
{
    Task<Mission?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Mission> Items, int Total)> ListAsync(int page, int pageSize, bool includeArchived, CancellationToken cancellationToken);
}

/// <summary>Sends missions to and reads missions from vehicles over their live link. Implemented by Gcs.Mavlink.</summary>
public interface IVehicleMissionTransfer
{
    Task<Result> UploadAsync(VehicleId vehicleId, IReadOnlyList<MissionItem> items, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<MissionItem>>> DownloadAsync(VehicleId vehicleId, CancellationToken cancellationToken);
}

/// <summary>Paging metadata helper for mission lists.</summary>
public static class MissionPaging
{
    public static PagedResponse<T> Page<T>(IReadOnlyList<T> items, int page, int pageSize, int total) =>
        new(items, page, pageSize, total);
}
