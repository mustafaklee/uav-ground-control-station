using Gcs.Domain.Common;

namespace Gcs.Domain.Users;

public readonly record struct RefreshTokenId(Guid Value)
{
    public static RefreshTokenId New() => new(Guid.CreateVersion7());
}

/// <summary>
/// A long-lived credential that buys new short-lived access tokens. Stored only as a SHA-256 hash: a stolen database
/// backup contains no usable tokens.
/// <para>
/// Rotation: every refresh revokes the token used and issues a new one. A token is therefore valid exactly once, and if
/// a revoked token is presented again, someone has a copy. Then every token of that user is revoked (reuse detection),
/// which logs out both the thief and the real user, who simply signs in again.
/// </para>
/// </summary>
public sealed class RefreshToken : Entity<RefreshTokenId>
{
    private RefreshToken(RefreshTokenId id, UserId userId, string tokenHash, DateTimeOffset now, DateTimeOffset expiresAt)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    // Used by EF Core when loading from the database.
    private RefreshToken()
        : base(default)
    {
        TokenHash = null!;
    }

    public UserId UserId { get; private set; }

    public string TokenHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>The token that replaced this one in a rotation; null when revoked by logout or reuse detection.</summary>
    public RefreshTokenId? ReplacedBy { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsUsableAt(DateTimeOffset now) => !IsRevoked && now < ExpiresAt;

    public static RefreshToken Issue(UserId userId, string tokenHash, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        return new RefreshToken(RefreshTokenId.New(), userId, tokenHash, now, now + lifetime);
    }

    public void Revoke(DateTimeOffset now, RefreshTokenId? replacedBy = null)
    {
        if (IsRevoked)
        {
            return;
        }

        RevokedAt = now;
        ReplacedBy = replacedBy;
    }
}

public static class UserErrors
{
    public static readonly Error UsernameFormat = Error.Validation(
        "user.username.format",
        $"Username must be {Username.MinLength}–{Username.MaxLength} characters: letters, digits or . _ @ -.");

    public static readonly Error PasswordLength = Error.Validation(
        "user.password.length",
        $"Password must be {User.MinPasswordLength}–{User.MaxPasswordLength} characters. A long passphrase beats complex rules.");

    public static readonly Error RoleUnknown = Error.Validation(
        "user.role.unknown", "Role must be one of: Observer, Operator, Maintenance, Administrator.");

    public static readonly Error NotFound = Error.NotFound("user.not_found", "The user does not exist.");

    public static readonly Error UsernameInUse = Error.Conflict("user.username.in_use", "Another user already has this username.");

    /// <summary>One message for "no such user", "wrong password" and "deactivated": the answer must not reveal which.</summary>
    public static readonly Error InvalidCredentials = Error.Unauthorized(
        "auth.invalid_credentials", "Username or password is wrong.");

    public static readonly Error LockedOut = Error.Unauthorized(
        "auth.locked_out", "Too many wrong passwords. The account is locked for a few minutes.");

    public static readonly Error InvalidRefreshToken = Error.Unauthorized(
        "auth.invalid_refresh_token", "The session has expired or was ended. Sign in again.");

    public static readonly Error WrongCurrentPassword = Error.Validation(
        "user.password.current_wrong", "The current password is wrong.");

    public static readonly Error CannotChangeOwnAccess = Error.Conflict(
        "user.self_change", "You cannot change your own role or deactivate yourself; ask another administrator.");
}
