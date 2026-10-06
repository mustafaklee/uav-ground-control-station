using Gcs.Application.Abstractions;
using Gcs.Contracts.Common;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Vehicles;

/// <summary>Use case: read one vehicle.</summary>
public sealed class GetVehicleHandler(IVehicleQueries queries)
{
    public async Task<Result<VehicleResponse>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        var vehicle = await queries.GetByIdAsync(id, cancellationToken);
        return vehicle is null ? VehicleErrors.NotFound : vehicle;
    }
}

/// <summary>Use case: list vehicles, paged, optionally filtered by status and a callsign search term.</summary>
public sealed class ListVehiclesHandler(IVehicleQueries queries)
{
    public const int MaxSearchLength = 32;

    public async Task<Result<PagedResponse<VehicleResponse>>> HandleAsync(VehicleListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var errors = new Dictionary<string, string[]>();
        if (query.Page < 1)
        {
            errors["page"] = ["Page must be 1 or greater."];
        }

        if (query.PageSize is < 1 or > VehicleListQuery.MaxPageSize)
        {
            errors["pageSize"] = [$"Page size must be between 1 and {VehicleListQuery.MaxPageSize}."];
        }

        VehicleStatus? status = null;
        if (query.Status is not null)
        {
            if (VehicleMapping.TryParseEnum<VehicleStatus>(query.Status, out var parsed))
            {
                status = parsed;
            }
            else
            {
                errors["status"] = [$"Status must be one of: {string.Join(", ", Enum.GetNames<VehicleStatus>())}."];
            }
        }

        var search = query.Search?.Trim();
        if (search?.Length > MaxSearchLength)
        {
            errors["search"] = [$"Search must be at most {MaxSearchLength} characters."];
        }

        if (errors.Count > 0)
        {
            return new Error(Common.ValidationErrors.Code, "One or more query parameters are invalid.", ErrorType.Validation)
            {
                Details = errors,
            };
        }

        return await queries.ListAsync(query.Page, query.PageSize, status, string.IsNullOrEmpty(search) ? null : search, cancellationToken);
    }
}
