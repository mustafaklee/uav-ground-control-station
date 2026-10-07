using Gcs.Api.Http;
using Gcs.Application.Vehicles;
using Gcs.Contracts.Common;
using Gcs.Contracts.Vehicles;
using Microsoft.AspNetCore.Mvc;

namespace Gcs.Api.Endpoints;

/// <summary>
/// REST endpoints for vehicle registration. Endpoints stay thin: translate HTTP to a use case call and the
/// result back to HTTP. All rules live in the application and domain layers.
/// </summary>
internal static class VehicleEndpoints
{
    private const string GetVehicleRouteName = "GetVehicle";

    public static IEndpointRouteBuilder MapVehicleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(ApiVersions.V1)
            .ReportApiVersions()
            .Build();

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/vehicles")
            .WithApiVersionSet(versionSet)
            .WithTags("Vehicles");

        group.MapGet("/", ListAsync).WithName("ListVehicles");
        group.MapGet("/{id:guid}", GetAsync).WithName(GetVehicleRouteName);
        group.MapPost("/", RegisterAsync).WithName("RegisterVehicle");
        group.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateVehicle");
        group.MapDelete("/{id:guid}", RetireAsync).WithName("RetireVehicle");

        group.MapPost("/{id:guid}/connection", ConnectAsync).WithName("ConnectVehicle");
        group.MapDelete("/{id:guid}/connection", DisconnectAsync).WithName("DisconnectVehicle");
        group.MapGet("/{id:guid}/connection", GetConnectionAsync).WithName("GetVehicleConnection");
        group.MapGet("/{id:guid}/telemetry", GetTelemetryAsync).WithName("GetVehicleTelemetry");
        group.MapGet("/{id:guid}/telemetry/history", GetTelemetryHistoryAsync).WithName("GetVehicleTelemetryHistory");

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        ListVehiclesHandler handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = VehicleListQuery.DefaultPageSize,
        string? status = null,
        string? search = null)
    {
        var result = await handler.HandleAsync(new VehicleListQuery(page, pageSize, status, search), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok<PagedResponse<VehicleResponse>>(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> GetAsync(Guid id, GetVehicleHandler handler, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        http.Response.Headers.ETag = ETags.Format(result.Value.Version);
        return TypedResults.Ok(result.Value);
    }

    private static async Task<IResult> RegisterAsync(
        RegisterVehicleRequest request,
        RegisterVehicleHandler handler,
        HttpContext http,
        LinkGenerator links,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error.ToProblem();
        }

        var vehicle = result.Value;
        http.Response.Headers.ETag = ETags.Format(vehicle.Version);
        var location = links.GetPathByName(http, GetVehicleRouteName, new { id = vehicle.Id, version = "1" });
        return TypedResults.Created(location, vehicle);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateVehicleRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        UpdateVehicleHandler handler,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        // Updates without If-Match would silently overwrite concurrent edits, so the header is mandatory.
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return ErrorResults.PreconditionRequired("Send the vehicle's ETag in the If-Match header to update it.");
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

    private static async Task<IResult> RetireAsync(
        Guid id,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        RetireVehicleHandler handler,
        CancellationToken cancellationToken)
    {
        // If-Match is optional here: retiring is idempotent and does not overwrite fields.
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

    /// <summary>Connecting happens in the background: 202 Accepted, then poll the status (SignalR push arrives in Phase 4).</summary>
    private static async Task<IResult> ConnectAsync(Guid id, ConnectVehicleHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Accepted($"/api/v1/vehicles/{id}/connection", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<IResult> DisconnectAsync(Guid id, DisconnectVehicleHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<IResult> GetConnectionAsync(Guid id, GetVehicleLinkHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> GetTelemetryAsync(Guid id, GetLatestTelemetryHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> GetTelemetryHistoryAsync(
        Guid id,
        TelemetryHistoryHandler handler,
        CancellationToken cancellationToken,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = TelemetryHistoryQuery.DefaultLimit)
    {
        var result = await handler.HandleAsync(id, new TelemetryHistoryQuery(from, to, limit), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
