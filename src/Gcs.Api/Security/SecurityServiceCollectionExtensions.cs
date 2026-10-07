using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Gcs.Api.Http;
using Gcs.Application.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Gcs.Api.Security;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Requests per minute per user (or per IP before sign-in) across the whole API.</summary>
    [Range(1, 1_000_000)]
    public int GlobalPerMinute { get; set; } = 600;

    /// <summary>Login and refresh attempts per minute per IP: slows password guessing to a crawl.</summary>
    [Range(1, 10_000)]
    public int AuthPerMinute { get; set; } = 10;

    /// <summary>Vehicle commands per minute per user. A human cannot meaningfully send more; a runaway script can.</summary>
    [Range(1, 10_000)]
    public int CommandsPerMinute { get; set; } = 60;
}

public static class SecurityServiceCollectionExtensions
{
    public const string AuthRateLimit = "auth";
    public const string CommandRateLimit = "commands";

    public static IServiceCollection AddGcsSecurity(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<JwtOptionsSetup>();
        services.AddSingleton<IPostConfigureOptions<JwtOptions>>(sp => sp.GetRequiredService<JwtOptionsSetup>());
        services.AddSingleton<IValidateOptions<JwtOptions>>(sp => sp.GetRequiredService<JwtOptionsSetup>());
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
        services.AddHostedService<BootstrapAdministratorService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                bearer.MapInboundClaims = false; // keep "role" and "name" as they are in the token
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = jwt.Value.CreateSigningKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256], // no "alg: none", no algorithm confusion
                    NameClaimType = GcsClaims.Name,
                    RoleClaimType = GcsClaims.Role,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                bearer.Events = new JwtBearerEvents
                {
                    // WebSockets cannot send headers from a browser, so SignalR passes the token in the query string.
                    // Accepted only on hub paths, where it is needed.
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()) // secure by default
            .AddPermissionPolicies();

        services.AddOptions<RateLimitOptions>().BindConfiguration(RateLimitOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitOptions>>((limiter, settings) => ConfigureRateLimits(limiter, settings.Value));

        return services;
    }

    private static AuthorizationBuilder AddPermissionPolicies(this AuthorizationBuilder builder)
    {
        foreach (var (permission, roles) in Permissions.RolesByPermission)
        {
            builder.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireRole(roles));
        }

        return builder;
    }

    private static void ConfigureRateLimits(RateLimiterOptions limiter, RateLimitOptions settings)
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.OnRejected = async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            await TypedResults.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                detail: "Slow down and try again shortly.",
                extensions: new Dictionary<string, object?> { [ErrorResults.CodeExtension] = "http.rate_limited" })
                .ExecuteAsync(context.HttpContext);
        };

        // Everyone: per signed-in user, or per IP address before sign-in. Health probes and SignalR streams are exempt.
        limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            http.Request.Path.StartsWithSegments("/health") || http.Request.Path.StartsWithSegments("/hubs")
                ? RateLimitPartition.GetNoLimiter("exempt")
                : RateLimitPartition.GetFixedWindowLimiter(PartitionKey(http), _ => PerMinute(settings.GlobalPerMinute)));

        limiter.AddPolicy(AuthRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
            $"ip:{http.Connection.RemoteIpAddress}", _ => PerMinute(settings.AuthPerMinute)));
        limiter.AddPolicy(CommandRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(http), _ => PerMinute(settings.CommandsPerMinute)));
    }

    private static string PartitionKey(HttpContext http) =>
        http.User.FindFirstValue(GcsClaims.UserId) is { } userId ? $"user:{userId}" : $"ip:{http.Connection.RemoteIpAddress}";

    private static FixedWindowRateLimiterOptions PerMinute(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0, // reject at once; queued requests would only time out later
    };
}
