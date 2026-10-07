using Gcs.Contracts.Common;
using Gcs.Domain.Users;

namespace Gcs.Application.Abstractions;

public enum PasswordCheck
{
    Failed,
    Succeeded,

    /// <summary>Correct, but hashed with older settings: store a fresh hash.</summary>
    SucceededRehashNeeded,
}

/// <summary>Slow, salted password hashing (PBKDF2 via ASP.NET Core Identity). Implemented outside the application layer.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    PasswordCheck Verify(string passwordHash, string password);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Issues signed access tokens (JWT) and random refresh tokens. Implemented by the API host.</summary>
public interface ITokenService
{
    TimeSpan RefreshTokenLifetime { get; }

    AccessToken CreateAccessToken(User user, DateTimeOffset now);

    /// <summary>A new random refresh token: the value for the client and the hash for the database.</summary>
    (string Token, string Hash) CreateRefreshToken();

    string HashRefreshToken(string token);
}

public interface IUserRepository
{
    Task<User?> GetByIdAsync(UserId id, CancellationToken cancellationToken);

    Task<User?> GetByUsernameAsync(Username username, CancellationToken cancellationToken);

    Task<bool> AnyAsync(CancellationToken cancellationToken);

    void Add(User user);

    Task<PagedResponse<User>> ListAsync(int page, int pageSize, CancellationToken cancellationToken);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken);

    void Add(RefreshToken token);

    /// <summary>Revokes every still-usable token of the user (logout everywhere, reuse detection, deactivation).</summary>
    Task RevokeAllAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken);
}
