using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Gcs.Api.Security;

/// <summary>
/// Token settings. The signing key is a secret: it comes from user-secrets (development), an environment variable
/// (<c>Jwt__SigningKey</c>, Docker) or a secret store, never from a file in the repository.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int MinKeyBytes = 32; // HS256 needs at least 256 bits

    [Required]
    public string Issuer { get; set; } = "gcs-api";

    [Required]
    public string Audience { get; set; } = "gcs";

    /// <summary>At least 32 bytes, e.g. <c>openssl rand -base64 48</c>.</summary>
    public string? SigningKey { get; set; }

    /// <summary>Short on purpose: a stolen access token is useful only this long, and role changes apply within it.</summary>
    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>About one shift. After that the operator signs in again.</summary>
    [Range(1, 24 * 30)]
    public int RefreshTokenHours { get; set; } = 12;

    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey ?? string.Empty));
}

/// <summary>
/// Refuses to start with a missing or short signing key. In Development only, a random key is generated instead, with a
/// warning: convenient for <c>dotnet run</c>, and every restart then signs everyone out, which nobody mistakes for production.
/// </summary>
internal sealed partial class JwtOptionsSetup(IHostEnvironment environment, ILogger<JwtOptionsSetup> logger)
    : IPostConfigureOptions<JwtOptions>, IValidateOptions<JwtOptions>
{
    public void PostConfigure(string? name, JwtOptions options)
    {
        if (string.IsNullOrEmpty(options.SigningKey) && environment.IsDevelopment())
        {
            options.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            LogEphemeralKey(logger);
        }
    }

    public ValidateOptionsResult Validate(string? name, JwtOptions options) =>
        Encoding.UTF8.GetByteCount(options.SigningKey ?? string.Empty) >= JwtOptions.MinKeyBytes
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Jwt:SigningKey must be at least {JwtOptions.MinKeyBytes} bytes. Set it with user-secrets or the Jwt__SigningKey environment variable.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "No Jwt:SigningKey configured: using a random key for this run (Development only). Tokens stop working after a restart.")]
    private static partial void LogEphemeralKey(ILogger logger);
}
