using Gcs.Application.Abstractions;
using Gcs.Application.Common;
using Gcs.Contracts.Common;
using Gcs.Contracts.Missions;
using Gcs.Domain.Common;
using Gcs.Domain.Missions;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Missions;

/// <summary>Use case: create a mission (a draft may be incomplete; validation issues are returned with it).</summary>
public sealed class CreateMissionHandler(IMissionRepository missions, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<Result<MissionResponse>> HandleAsync(SaveMissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = MissionMapping.ToItems(request.Items);
        if (!items.IsSuccess)
        {
            return items.Error;
        }

        var mission = Mission.Create(request.Name, items.Value, clock.GetUtcNowForStorage());
        if (!mission.IsSuccess)
        {
            return mission.Error;
        }

        missions.Add(mission.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return MissionMapping.ToResponse(mission.Value);
    }
}

/// <summary>Use case: replace a mission's name and items, guarded by the version the planner edited.</summary>
public sealed class UpdateMissionHandler(IMissionRepository missions, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<Result<MissionResponse>> HandleAsync(
        Guid id, int expectedVersion, SaveMissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = MissionMapping.ToItems(request.Items);
        if (!items.IsSuccess)
        {
            return items.Error;
        }

        var mission = await missions.GetByIdAsync(new MissionId(id), cancellationToken);
        if (mission is null)
        {
            return MissionErrors.NotFound;
        }

        var update = mission.Update(request.Name, items.Value, expectedVersion, clock.GetUtcNowForStorage());
        if (!update.IsSuccess)
        {
            return update.Error;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return MissionErrors.VersionMismatch;
        }

        return MissionMapping.ToResponse(mission);
    }
}

public sealed class GetMissionHandler(IMissionQueries queries)
{
    public async Task<Result<MissionResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        var mission = await queries.GetByIdAsync(id, cancellationToken);
        return mission is null ? MissionErrors.NotFound : MissionMapping.ToResponse(mission);
    }
}

public sealed class ListMissionsHandler(IMissionQueries queries)
{
    public const int MaxPageSize = 100;

    public async Task<Result<PagedResponse<MissionSummaryResponse>>> HandleAsync(
        int page, int pageSize, bool includeArchived, CancellationToken cancellationToken)
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

        var (items, total) = await queries.ListAsync(page, pageSize, includeArchived, cancellationToken);
        return MissionPaging.Page<MissionSummaryResponse>([.. items.Select(MissionMapping.ToSummary)], page, pageSize, total);
    }
}

/// <summary>Use case: archive a mission (the DELETE endpoint). Kept for history: which plan a vehicle flew.</summary>
public sealed class ArchiveMissionHandler(IMissionRepository missions, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<Result> HandleAsync(Guid id, int? expectedVersion, CancellationToken cancellationToken)
    {
        var mission = await missions.GetByIdAsync(new MissionId(id), cancellationToken);
        if (mission is null)
        {
            return MissionErrors.NotFound;
        }

        var archive = mission.Archive(expectedVersion, clock.GetUtcNowForStorage());
        if (!archive.IsSuccess)
        {
            return archive;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// Use case: send a mission to a vehicle. Only flyable missions are sent; the outcome (success or the vehicle's reason
/// for refusing) is stored on the mission so every operator sees what is loaded where.
/// </summary>
public sealed class UploadMissionHandler(
    IMissionRepository missions,
    IVehicleRepository vehicles,
    IVehicleMissionTransfer transfer,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<Result<MissionResponse>> HandleAsync(Guid id, UploadMissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mission = await missions.GetByIdAsync(new MissionId(id), cancellationToken);
        if (mission is null)
        {
            return MissionErrors.NotFound;
        }

        if (mission.IsArchived)
        {
            return MissionErrors.Archived;
        }

        var issues = mission.Validate();
        if (issues.Count > 0)
        {
            return MissionErrors.NotFlyable with
            {
                Details = issues.GroupBy(i => i.ItemIndex is { } index ? $"items[{index}]" : "mission")
                    .ToDictionary(g => g.Key, g => g.Select(i => i.Message).ToArray()),
            };
        }

        var vehicle = await vehicles.GetByIdAsync(new VehicleId(request.VehicleId), cancellationToken);
        if (vehicle is null)
        {
            return VehicleErrors.NotFound;
        }

        var upload = await transfer.UploadAsync(vehicle.Id, mission.Items, cancellationToken);
        mission.RecordUpload(vehicle.Id, upload.IsSuccess, upload.Error?.Message, clock.GetUtcNowForStorage());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return upload.IsSuccess ? MissionMapping.ToResponse(mission) : upload.Error;
    }
}

/// <summary>Use case: read the mission currently stored on a vehicle.</summary>
public sealed class DownloadVehicleMissionHandler(IVehicleQueries vehicles, IVehicleMissionTransfer transfer)
{
    public async Task<Result<VehicleMissionResponse>> HandleAsync(Guid vehicleId, CancellationToken cancellationToken)
    {
        if (await vehicles.GetByIdAsync(vehicleId, cancellationToken) is null)
        {
            return VehicleErrors.NotFound;
        }

        var download = await transfer.DownloadAsync(new VehicleId(vehicleId), cancellationToken);
        return download.IsSuccess
            ? new VehicleMissionResponse(vehicleId, [.. download.Value.Select(MissionMapping.ToDto)])
            : download.Error;
    }
}
