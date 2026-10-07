using System.Text.RegularExpressions;
using Gcs.Domain.Common;

namespace Gcs.Domain.Users;

public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// What a user may do. One role per user keeps the authorization matrix small enough to test completely
/// (see docs/security.md). Least privilege: an observer can never command, an operator can never manage users.
/// </summary>
public enum UserRole
{
    /// <summary>Read-only: fleet, telemetry, missions, audit log. For supervisors and visitors.</summary>
    Observer = 1,

    /// <summary>Flies vehicles: connects links, plans and uploads missions, takes control and sends commands.</summary>
    Operator = 2,

    /// <summary>Looks after the fleet: registers, edits and retires vehicles, connects links for checks. Cannot command.</summary>
    Maintenance = 3,

    /// <summary>Manages users and system configuration; has every permission.</summary>
    Administrator = 4,
}

/// <summary>
/// Login name, e.g. <c>operator01</c>. Stored lower case so "Ali" and "ali" are one account. The allowed characters are
/// the ones the command audit log accepts as an operator name, so every user can be recorded there unchanged.
/// </summary>
public sealed partial record Username
{
    public const int MinLength = 3;
    public const int MaxLength = 64;

    private Username(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<Username> Create(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length is < MinLength or > MaxLength || !AllowedPattern().IsMatch(normalized)
            ? UserErrors.UsernameFormat
            : new Username(normalized);
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z0-9._@\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPattern();
}

/// <summary>
/// A person who signs in to the GCS. Holds only a password <i>hash</i>; the password itself never reaches the database
/// or the logs. Repeated wrong passwords lock the account for a while, which turns online guessing into a crawl.
/// </summary>
public sealed class User : AggregateRoot<UserId>
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 128;
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private User(UserId id, Username username, string passwordHash, UserRole role, DateTimeOffset now)
        : base(id)
    {
        Username = username;
        PasswordHash = passwordHash;
        Role = role;
        IsActive = true;
        CreatedAt = now;
        UpdatedAt = now;
    }

    // Used by EF Core when loading from the database.
    private User()
        : base(default)
    {
        Username = null!;
        PasswordHash = null!;
    }

    public Username Username { get; private set; }

    public string PasswordHash { get; private set; }

    public UserRole Role { get; private set; }

    /// <summary>Deactivated users cannot sign in or refresh. Users are never deleted: the audit log references them by name.</summary>
    public bool IsActive { get; private set; }

    public int FailedLogins { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result ValidatePassword(string? password) =>
        password is { Length: >= MinPasswordLength and <= MaxPasswordLength } && !string.IsNullOrWhiteSpace(password)
            ? Result.Success()
            : UserErrors.PasswordLength;

    /// <summary>Creates an active user. The hash comes from the password hasher; the domain never stores a plain password.</summary>
    public static User Create(Username username, string passwordHash, UserRole role, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return new User(UserId.New(), username, passwordHash, role, now);
    }

    public bool IsLockedOutAt(DateTimeOffset now) => LockedUntil is { } until && now < until;

    /// <summary>Whether this user may sign in right now (password aside).</summary>
    public Result CanSignIn(DateTimeOffset now) =>
        !IsActive ? UserErrors.InvalidCredentials
        : IsLockedOutAt(now) ? UserErrors.LockedOut
        : Result.Success();

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLogins = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }

    /// <summary>The fifth wrong password in a row locks the account for <see cref="LockoutDuration"/>.</summary>
    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLogins++;
        if (FailedLogins >= MaxFailedLogins)
        {
            LockedUntil = now + LockoutDuration;
            FailedLogins = 0;
        }
    }

    /// <summary>A newer hash format or work factor: re-hash transparently at the next successful login.</summary>
    public void UpgradePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public void ChangePassword(string passwordHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        UpdatedAt = now;
    }

    public void ChangeRole(UserRole role, DateTimeOffset now)
    {
        Role = role;
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        IsActive = true;
        LockedUntil = null;
        FailedLogins = 0;
        UpdatedAt = now;
    }
}
