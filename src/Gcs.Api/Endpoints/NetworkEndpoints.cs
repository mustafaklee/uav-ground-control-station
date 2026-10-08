using Gcs.Api.Security;
using Gcs.Application.Network;
using Gcs.Contracts.Network;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Gcs.Api.Endpoints;

/// <summary>The network around the GCS (Phase 12, ADR-019).</summary>
internal static class NetworkEndpoints
{
    public static IEndpointRouteBuilder MapNetworkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(ApiVersions.V1)
            .ReportApiVersions()
            .Build();

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/network")
            .WithApiVersionSet(versionSet)
            .WithTags("Network")
            .RequireAuthorization(Permissions.Read);

        group.MapGet("/topology", GetTopologyAsync).WithName("GetNetworkTopology");
        return endpoints;
    }

    private static async Task<Ok<NetworkTopologyResponse>> GetTopologyAsync(GetNetworkTopologyHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(cancellationToken));
}
