using System.Security.Claims;
using Gcs.Api.Http;
using Gcs.Api.Security;
using Gcs.Application.Commands;
using Gcs.Contracts.Commands;

namespace Gcs.Api.Endpoints;

/// <summary>
/// Vehicle control: take/release control (command lease), send commands, read the audit log.
/// The operator is the signed-in user: the name in the lease and in the audit log comes from the access token,
/// so nobody can act under someone else's name.
/// </summary>
internal static class CommandEndpoints
{
    public static IEndpointRouteBuilder MapCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var versionSet = endpoints.NewApiVersionSet().HasApiVersion(ApiVersions.V1).ReportApiVersions().Build();
        var vehicles = endpoints.MapGroup("/api/v{version:apiVersion}/vehicles").WithApiVersionSet(versionSet).WithTags("Commands")
            .RequireAuthorization(Permissions.Read);

        vehicles.MapGet("/{id:guid}/command-lease", GetLeaseAsync).WithName("GetCommandLease");
        vehicles.MapPost("/{id:guid}/command-lease", AcquireLeaseAsync).WithName("AcquireCommandLease").RequireAuthorization(Permissions.Command);
        vehicles.MapDelete("/{id:guid}/command-lease", ReleaseLeaseAsync).WithName("ReleaseCommandLease").RequireAuthorization(Permissions.Command);
        vehicles.MapGet("/{id:guid}/flight-modes", GetFlightModesAsync).WithName("GetFlightModes");
        vehicles.MapPost("/{id:guid}/commands", SendAsync).WithName("SendVehicleCommand")
            .RequireAuthorization(Permissions.Command)
            .RequireRateLimiting(SecurityServiceCollectionExtensions.CommandRateLimit);
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
        ClaimsPrincipal user,
        AcquireCommandLeaseHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, user.Identity?.Name, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> ReleaseLeaseAsync(
        Guid id,
        ClaimsPrincipal user,
        ReleaseCommandLeaseHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, user.Identity?.Name, cancellationToken);
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
        ClaimsPrincipal user,
        SendVehicleCommandHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, user.Identity?.Name, request, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> ListAuditAsync(
        Guid id, ListCommandAuditHandler handler, CancellationToken cancellationToken, int page = 1, int pageSize = 20)
    {
        var result = await handler.HandleAsync(id, page, pageSize, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
