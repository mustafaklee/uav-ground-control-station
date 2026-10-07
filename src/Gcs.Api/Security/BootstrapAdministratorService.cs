using System.Security.Claims;
using Gcs.Application.Security;

namespace Gcs.Api.Security;

public sealed class BootstrapAdministratorOptions
{
    public const string SectionName = "Security:BootstrapAdministrator";

    public string Username { get; set; } = "admin";

    /// <summary>A secret like the signing key: user-secrets or <c>Security__BootstrapAdministrator__Password</c>.</summary>
    public string? Password { get; set; }
}

/// <summary>
/// Creates the first administrator when the user table is empty. Runs in the background and retries, so a database that
/// is not up yet delays the bootstrap instead of crashing the API. Once any user exists it does nothing, so the
/// configured password can never reset an existing account.
/// </summary>
internal sealed partial class BootstrapAdministratorService(
    IServiceScopeFactory scopes, IConfiguration configuration, ILogger<BootstrapAdministratorService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configuration.GetSection(BootstrapAdministratorOptions.SectionName).Get<BootstrapAdministratorOptions>() ?? new();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<BootstrapAdministratorHandler>();
                var result = await handler.HandleAsync(options.Username, options.Password, stoppingToken);
                if (!result.IsSuccess)
                {
                    LogNotConfigured(logger, result.Error.Message);
                }
                else if (result.Value)
                {
                    LogCreated(logger, options.Username);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRetry(logger, ex.GetBaseException().Message);
                await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No users exist and no valid bootstrap administrator is configured ({Reason}). Set Security:BootstrapAdministrator:Password to create one.")]
    private static partial void LogNotConfigured(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created bootstrap administrator {Username}. Sign in and create personal accounts.")]
    private static partial void LogCreated(ILogger logger, string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap administrator check failed, retrying: {Reason}")]
    private static partial void LogRetry(ILogger logger, string reason);
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's id (the token's <c>sub</c>). Endpoints behind authorization always have it.</summary>
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user?.FindFirstValue(GcsClaims.UserId), out var id) ? id : Guid.Empty;
}

/// <summary>
/// Defensive response headers. The API serves JSON only, so the content security policy forbids everything;
/// it costs nothing and turns a future XSS bug in an error page into a non-event.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.CompletedTask;
        });
        return next(context);
    }
}
