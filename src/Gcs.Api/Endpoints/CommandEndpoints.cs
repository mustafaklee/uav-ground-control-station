using Gcs.Api.Http;
using Gcs.Application.Commands;
using Gcs.Contracts.Commands;
using Microsoft.AspNetCore.Mvc;

namespace Gcs.Api.Endpoints;

/// <summary>
/// Vehicle control: take/release control (command lease), send commands, read the audit log.
/// Every request names its operator in <see cref="CommandHeaders.Operator"/> until authentication exists (Phase 8).
/// </summary>
internal static class CommandEndpoints
{
    public static IEndpointRouteBuilder MapCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var versionSet = endpoints.NewApiVersionSet().HasApiVersion(ApiVersions.V1).ReportApiVersions().Build();
        var vehicles = endpoints.MapGroup("/api/v{version:apiVersion}/vehicles").WithApiVersionSet(versionSet).WithTags("Commands");

        vehicles.MapGet("/{id:guid}/command-lease", GetLeaseAsync).WithName("GetCommandLease");
        vehicles.MapPost("/{id:guid}/command-lease", AcquireLeaseAsync).WithName("AcquireCommandLease");
        vehicles.MapDelete("/{id:guid}/command-lease", ReleaseLeaseAsync).WithName("ReleaseCommandLease");
        vehicles.MapGet("/{id:guid}/flight-modes", GetFlightModesAsync).WithName("GetFlightModes");
        vehicles.MapPost("/{id:guid}/commands", SendAsync).WithName("SendVehicleCommand");
        vehicles.MapGet("/{id:guid}/commands", ListAuditAsync).WithName("ListVehicleCommands");

        return endpoints;
    }

    private static async Task<IResult> GetLeaseAsync(Guid id, GetCommandLeaseHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> AcquireLeaseAsync(
        Guid id,
        [FromHeader(Name = CommandHeaders.Operator)] string? operatorName,
        AcquireCommandLeaseHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, operatorName, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> ReleaseLeaseAsync(
        Guid id,
        [FromHeader(Name = CommandHeaders.Operator)] string? operatorName,
        ReleaseCommandLeaseHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, operatorName, cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<IResult> GetFlightModesAsync(Guid id, GetFlightModesHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    /// <summary>
    /// 200 with the audit entry when the vehicle accepted; 409 when the vehicle refused or the GCS did not send it
    /// (no lease, not connected, same command in flight); 504 when the vehicle never answered.
    /// </summary>
    private static async Task<IResult> SendAsync(
        Guid id,
        SendCommandRequest request,
        [FromHeader(Name = CommandHeaders.Operator)] string? operatorName,
        SendVehicleCommandHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, operatorName, request, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> ListAuditAsync(
        Guid id, ListCommandAuditHandler handler, CancellationToken cancellationToken, int page = 1, int pageSize = 20)
    {
        var result = await handler.HandleAsync(id, page, pageSize, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
