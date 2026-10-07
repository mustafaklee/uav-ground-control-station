using Gcs.Api.Http;
using Gcs.Application.Missions;
using Gcs.Contracts.Missions;
using Microsoft.AspNetCore.Mvc;

namespace Gcs.Api.Endpoints;

/// <summary>Mission planning: CRUD with optimistic concurrency, upload to a vehicle, read back from a vehicle.</summary>
internal static class MissionEndpoints
{
    private const string GetMissionRouteName = "GetMission";

    public static IEndpointRouteBuilder MapMissionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var versionSet = endpoints.NewApiVersionSet().HasApiVersion(ApiVersions.V1).ReportApiVersions().Build();

        var missions = endpoints.MapGroup("/api/v{version:apiVersion}/missions").WithApiVersionSet(versionSet).WithTags("Missions");
        missions.MapGet("/", ListAsync).WithName("ListMissions");
        missions.MapGet("/{id:guid}", GetAsync).WithName(GetMissionRouteName);
        missions.MapPost("/", CreateAsync).WithName("CreateMission");
        missions.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateMission");
        missions.MapDelete("/{id:guid}", ArchiveAsync).WithName("ArchiveMission");
        missions.MapPost("/{id:guid}/upload", UploadAsync).WithName("UploadMission");

        var vehicles = endpoints.MapGroup("/api/v{version:apiVersion}/vehicles").WithApiVersionSet(versionSet).WithTags("Missions");
        vehicles.MapGet("/{id:guid}/mission", DownloadAsync).WithName("DownloadVehicleMission");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ListMissionsHandler handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 20,
        bool includeArchived = false)
    {
        var result = await handler.HandleAsync(page, pageSize, includeArchived, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> GetAsync(Guid id, GetMissionHandler handler, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        http.Response.Headers.ETag = ETags.Format(result.Value.Version);
        return TypedResults.Ok(result.Value);
    }

    private static async Task<IResult> CreateAsync(
        SaveMissionRequest request, CreateMissionHandler handler, HttpContext http, LinkGenerator links, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        http.Response.Headers.ETag = ETags.Format(result.Value.Version);
        var location = links.GetPathByName(http, GetMissionRouteName, new { id = result.Value.Id, version = "1" });
        return TypedResults.Created(location, result.Value);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        SaveMissionRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        UpdateMissionHandler handler,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return ErrorResults.PreconditionRequired("Send the mission's ETag in the If-Match header to update it.");
        }

        if (!ETags.TryParse(ifMatch, out var expectedVersion))
        {
            return ErrorResults.InvalidIfMatch();
        }

        var result = await handler.HandleAsync(id, expectedVersion, request, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        http.Response.Headers.ETag = ETags.Format(result.Value.Version);
        return TypedResults.Ok(result.Value);
    }

    private static async Task<IResult> ArchiveAsync(
        Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, ArchiveMissionHandler handler, CancellationToken cancellationToken)
    {
        int? expectedVersion = null;
        if (!string.IsNullOrWhiteSpace(ifMatch))
        {
            if (!ETags.TryParse(ifMatch, out var parsed))
            {
                return ErrorResults.InvalidIfMatch();
            }

            expectedVersion = parsed;
        }

        var result = await handler.HandleAsync(id, expectedVersion, cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<IResult> UploadAsync(
        Guid id, UploadMissionRequest request, UploadMissionHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, request, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> DownloadAsync(Guid id, DownloadVehicleMissionHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
