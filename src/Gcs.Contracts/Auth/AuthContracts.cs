namespace Gcs.Contracts.Auth;

/// <summary>Role names as the API and tokens spell them.</summary>
public static class Roles
{
    public const string Observer = "Observer";
    public const string Operator = "Operator";
    public const string Maintenance = "Maintenance";
    public const string Administrator = "Administrator";
}

/// <summary>Body of <c>POST /api/v1/auth/login</c>.</summary>
public sealed record LoginRequest(string? Username, string? Password);

/// <summary>Body of <c>POST /api/v1/auth/refresh</c> and <c>POST /api/v1/auth/logout</c>.</summary>
public sealed record RefreshRequest(string? RefreshToken);

/// <summary>
/// A signed-in session. Send <c>AccessToken</c> as <c>Authorization: Bearer ...</c>; before it expires, exchange
/// <c>RefreshToken</c> for a new pair. The refresh token is single use: always keep the newest one.
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserResponse User);

public sealed record UserResponse(
    Guid Id,
    string Username,
    string Role,
    bool IsActive,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt);

/// <summary>Body of <c>POST /api/v1/users</c> (administrators only).</summary>
public sealed record CreateUserRequest(string? Username, string? Password, string? Role);

/// <summary>Body of <c>PUT /api/v1/users/{id}</c>: change role and/or activation (administrators only).</summary>
public sealed record UpdateUserRequest(string? Role, bool? IsActive);

/// <summary>Body of <c>POST /api/v1/auth/password</c>: change your own password.</summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
