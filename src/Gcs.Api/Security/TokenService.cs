using System.Security.Claims;
using System.Security.Cryptography;
using Gcs.Application.Abstractions;
using Gcs.Domain.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using IPasswordHasher = Gcs.Application.Abstractions.IPasswordHasher;

namespace Gcs.Api.Security;

/// <summary>Claim names inside our access tokens. Short JWT names, not the long XML-era URIs.</summary>
public static class GcsClaims
{
    public const string UserId = JwtRegisteredClaimNames.Sub;
    public const string Name = "name";
    public const string Role = "role";
}

/// <summary>
/// Access tokens: JWT signed with HMAC-SHA256, carrying user id, name and role, valid for minutes.
/// Refresh tokens: 32 random bytes (not a JWT; the server looks them up), stored as SHA-256.
/// </summary>
internal sealed class TokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromHours(options.Value.RefreshTokenHours);

    public AccessToken CreateAccessToken(User user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        var settings = options.Value;
        var expires = now.AddMinutes(settings.AccessTokenMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(settings.CreateSigningKey(), SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(GcsClaims.UserId, user.Id.Value.ToString()),
                new Claim(GcsClaims.Name, user.Username.Value),
                new Claim(GcsClaims.Role, user.Role.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
        });
        return new AccessToken(token, expires);
    }

    public (string Token, string Hash) CreateRefreshToken()
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (token, HashRefreshToken(token));
    }

    public string HashRefreshToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token ?? string.Empty)));
}

/// <summary>
/// ASP.NET Core Identity's hasher: PBKDF2-HMAC-SHA512, 100 000 iterations, random salt, versioned format (so the
/// work factor can be raised later and old hashes upgraded at login). Battle-tested; we do not write our own crypto.
/// </summary>
internal sealed class IdentityPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public PasswordCheck Verify(string passwordHash, string password) => _hasher.VerifyHashedPassword(null!, passwordHash, password) switch
    {
        PasswordVerificationResult.Success => PasswordCheck.Succeeded,
        PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SucceededRehashNeeded,
        _ => PasswordCheck.Failed,
    };
}
