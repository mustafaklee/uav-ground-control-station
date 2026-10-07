using System.Security.Claims;
using Gcs.Api.Http;
using Gcs.Api.Security;
using Gcs.Application.Security;
using Gcs.Contracts.Auth;

namespace Gcs.Api.Endpoints;

/// <summary>Sign in, keep the session alive, sign out, change your password; and user administration.</summary>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var versionSet = endpoints.NewApiVersionSet().HasApiVersion(ApiVersions.V1).ReportApiVersions().Build();

        var auth = endpoints.MapGroup("/api/v{version:apiVersion}/auth").WithApiVersionSet(versionSet).WithTags("Auth");

        // Anonymous by nature (this is how you get a token), so they get the strict per-IP limit instead.
        auth.MapPost("/login", LoginAsync).WithName("Login").AllowAnonymous().RequireRateLimiting(SecurityServiceCollectionExtensions.AuthRateLimit);
        auth.MapPost("/refresh", RefreshAsync).WithName("RefreshSession").AllowAnonymous().RequireRateLimiting(SecurityServiceCollectionExtensions.AuthRateLimit);
        auth.MapPost("/logout", LogoutAsync).WithName("Logout").AllowAnonymous().RequireRateLimiting(SecurityServiceCollectionExtensions.AuthRateLimit);
        auth.MapGet("/me", MeAsync).WithName("GetCurrentUser");
        auth.MapPost("/password", ChangePasswordAsync).WithName("ChangePassword");

        var users = endpoints.MapGroup("/api/v{version:apiVersion}/users").WithApiVersionSet(versionSet).WithTags("Users")
            .RequireAuthorization(Permissions.ManageUsers);
        users.MapGet("/", ListUsersAsync).WithName("ListUsers");
        users.MapGet("/{id:guid}", GetUserAsync).WithName("GetUser");
        users.MapPost("/", CreateUserAsync).WithName("CreateUser");
        users.MapPut("/{id:guid}", UpdateUserAsync).WithName("UpdateUser");

        return endpoints;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, LoginHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request, RefreshSessionHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> LogoutAsync(RefreshRequest request, LogoutHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal user, GetUserHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId(), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request, ClaimsPrincipal user, ChangePasswordHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId(), request, cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<IResult> ListUsersAsync(ListUsersHandler handler, CancellationToken cancellationToken, int page = 1, int pageSize = 50)
    {
        var result = await handler.HandleAsync(page, pageSize, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> GetUserAsync(Guid id, GetUserHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(id, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> CreateUserAsync(CreateUserRequest request, CreateUserHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request, cancellationToken);
        return result.IsSuccess ? TypedResults.Created($"/api/v1/users/{result.Value.Id}", result.Value) : result.Error.ToProblem();
    }

    private static async Task<IResult> UpdateUserAsync(
        Guid id, UpdateUserRequest request, ClaimsPrincipal user, UpdateUserHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId(), id, request, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
