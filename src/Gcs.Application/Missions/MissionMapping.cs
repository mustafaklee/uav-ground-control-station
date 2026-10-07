using System.Globalization;
using Gcs.Application.Common;
using Gcs.Application.Vehicles;
using Gcs.Contracts.Missions;
using Gcs.Domain.Common;
using Gcs.Domain.Missions;

namespace Gcs.Application.Missions;

public static class MissionMapping
{
    public static MissionResponse ToResponse(Mission mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        var issues = mission.Validate();
        return new MissionResponse(
            mission.Id.Value,
            mission.Name,
            mission.Status.ToString(),
            [.. mission.Items.Select(ToDto)],
            [.. issues.Select(i => new MissionIssueDto(i.ItemIndex, i.Code, i.Message))],
            issues.Count == 0,
            Math.Round(mission.TotalDistanceMetres, 1),
            ToDto(mission.LastUpload),
            mission.Version,
            mission.CreatedAt,
            mission.UpdatedAt);
    }

    public static MissionSummaryResponse ToSummary(Mission mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        return new MissionSummaryResponse(
            mission.Id.Value,
            mission.Name,
            mission.Status.ToString(),
            mission.Items.Count,
            mission.Validate().Count == 0,
            Math.Round(mission.TotalDistanceMetres, 1),
            ToDto(mission.LastUpload),
            mission.Version,
            mission.UpdatedAt);
    }

    public static MissionItemDto ToDto(MissionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new MissionItemDto(item.Command.ToString(), item.Latitude, item.Longitude, item.Altitude, item.HoldSeconds, item.Speed);
    }

    /// <summary>
    /// Parses request items, collecting every problem with its position (<c>items[3]</c>) so the planner UI can mark
    /// exactly which rows to fix.
    /// </summary>
    public static Result<IReadOnlyList<MissionItem>> ToItems(IReadOnlyList<MissionItemDto>? dtos)
    {
        if (dtos is null)
        {
            return Result.Success<IReadOnlyList<MissionItem>>([]);
        }

        var items = new List<MissionItem>(dtos.Count);
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < dtos.Count; i++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"items[{i}]");
            var dto = dtos[i];
            if (dto is null || !VehicleMapping.TryParseEnum<MissionCommand>(dto.Command, out var command))
            {
                errors[key] = [$"Command must be one of: {string.Join(", ", Enum.GetNames<MissionCommand>())}."];
                continue;
            }

            var item = MissionItem.Create(command, dto.Latitude, dto.Longitude, dto.Altitude, dto.HoldSeconds, dto.Speed);
            if (item.IsSuccess)
            {
                items.Add(item.Value);
            }
            else
            {
                errors[key] = [item.Error.Message];
            }
        }

        if (errors.Count > 0)
        {
            return new Error(ValidationErrors.Code, "One or more mission items are invalid.", ErrorType.Validation) { Details = errors };
        }

        return Result.Success<IReadOnlyList<MissionItem>>(items);
    }

    private static MissionUploadDto? ToDto(MissionUpload? upload) =>
        upload is null ? null : new MissionUploadDto(upload.VehicleId.Value, upload.Succeeded, upload.At, upload.Error);
}
