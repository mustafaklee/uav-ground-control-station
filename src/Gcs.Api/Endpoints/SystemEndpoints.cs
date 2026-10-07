using System.Reflection;
using Gcs.Contracts.Diagnostics;

namespace Gcs.Api.Endpoints;

internal static class SystemEndpoints
{
    private const string ServiceName = "gcs-api";

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(ApiVersions.V1)
            .ReportApiVersions()
            .Build();

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/system")
            .WithApiVersionSet(versionSet)
            .WithTags("System")
            .AllowAnonymous(); // name, version and environment only: what a monitoring probe needs

        group.MapGet("/info", (IHostEnvironment environment) => TypedResults.Ok(new SystemInfoResponse(
                ServiceName,
                typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                environment.EnvironmentName)))
            .WithName("GetSystemInfo");

        return endpoints;
    }
}
