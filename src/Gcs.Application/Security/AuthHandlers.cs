using Gcs.Application.Abstractions;
using Gcs.Application.Common;
using Gcs.Application.Vehicles;
using Gcs.Contracts.Auth;
using Gcs.Contracts.Common;
using Gcs.Domain.Common;
using Gcs.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Gcs.Application.Security;

/// <summary>
/// Use case: sign in with username and password, get an access token (minutes) and a refresh token (hours).
/// Every failure answers the same "username or password is wrong", whether the user exists or not, and an unknown
/// user still costs one password-hash computation, so neither the message nor the response time reveals valid names.
/// </summary>
public sealed partial class LoginHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher,
    ITokenService tokens,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<LoginHandler> logger)
{
    private static string? _dummyHash;

    public async Task<Result<TokenResponse>> HandleAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.GetUtcNowForStorage();
        var username = Username.Create(request.Username);
        var user = username.IsSuccess ? await users.GetByUsernameAsync(username.Value, cancellationToken) : null;
        var password = request.Password ?? string.Empty;

        if (user is null)
        {
            hasher.Verify(_dummyHash ??= hasher.Hash("not-a-real-password-just-for-timing"), password);
            LogLoginFailed(logger, request.Username ?? string.Empty, "unknown user");
            return UserErrors.InvalidCredentials;
        }

        var allowed = user.CanSignIn(now);
        if (!allowed.IsSuccess)
        {
            LogLoginFailed(logger, user.Username.Value, allowed.Error.Code);
            return allowed.Error;
        }

        var check = hasher.Verify(user.PasswordHash, password);
        if (check == PasswordCheck.Failed)
        {
            user.RecordFailedLogin(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            LogLoginFailed(logger, user.Username.Value, user.IsLockedOutAt(now) ? "wrong password, account locked" : "wrong password");
            return UserErrors.InvalidCredentials;
        }

        if (check == PasswordCheck.SucceededRehashNeeded)
        {
            user.UpgradePasswordHash(hasher.Hash(password));
        }

        user.RecordSuccessfulLogin(now);
        var session = SessionIssuer.Issue(user, refreshTokens, tokens, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        LogLoginSucceeded(logger, user.Username.Value, user.Role);
        return session;
    }

    // Never log the password, not even a wrong one: it is often the right password for another system.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Login failed for {Username}: {Reason}")]
    private static partial void LogLoginFailed(ILogger logger, string username, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Login succeeded for {Username} ({Role})")]
    private static partial void LogLoginSucceeded(ILogger logger, string username, UserRole role);
}

/// <summary>
/// Use case: exchange a refresh token for a new access token and a new refresh token (rotation). Presenting a token
/// that was already used means it was copied: every session of that user is ended (reuse detection).
/// </summary>
public sealed partial class RefreshSessionHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    ITokenService tokens,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<RefreshSessionHandler> logger)
{
    public async Task<Result<TokenResponse>> HandleAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return UserErrors.InvalidRefreshToken;
        }

        var now = clock.GetUtcNowForStorage();
        var stored = await refreshTokens.GetByHashAsync(tokens.HashRefreshToken(request.RefreshToken), cancellationToken);
        if (stored is null)
        {
            return UserErrors.InvalidRefreshToken;
        }

        if (stored.IsRevoked)
        {
            await refreshTokens.RevokeAllAsync(stored.UserId, now, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            LogReuseDetected(logger, stored.UserId.Value);
            return UserErrors.InvalidRefreshToken;
        }

        var user = await users.GetByIdAsync(stored.UserId, cancellationToken);
        if (!stored.IsUsableAt(now) || user is null || !user.IsActive)
        {
            return UserErrors.InvalidRefreshToken;
        }

        var session = SessionIssuer.Issue(user, refreshTokens, tokens, now, rotated: stored);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return session;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh token reuse for user {UserId}: all sessions of the user were revoked")]
    private static partial void LogReuseDetected(ILogger logger, Guid userId);
}

/// <summary>Use case: end a session. Idempotent: an unknown or already revoked token is not an error.</summary>
public sealed class LogoutHandler(IRefreshTokenRepository refreshTokens, ITokenService tokens, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<Result> HandleAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.RefreshToken)
            && await refreshTokens.GetByHashAsync(tokens.HashRefreshToken(request.RefreshToken), cancellationToken) is { } stored)
        {
            stored.Revoke(clock.GetUtcNowForStorage());
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}

/// <summary>Use case: change your own password. Every other session is ended, in case the old password leaked.</summary>
public sealed class ChangePasswordHandler(
    IUserRepository users, IRefreshTokenRepository refreshTokens, IPasswordHasher hasher, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<Result> HandleAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await users.GetByIdAsync(new UserId(userId), cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound;
        }

        if (hasher.Verify(user.PasswordHash, request.CurrentPassword ?? string.Empty) == PasswordCheck.Failed)
        {
            return UserErrors.WrongCurrentPassword;
        }

        var valid = User.ValidatePassword(request.NewPassword);
        if (!valid.IsSuccess)
        {
            return valid;
        }

        var now = clock.GetUtcNowForStorage();
        user.ChangePassword(hasher.Hash(request.NewPassword!), now);
        await refreshTokens.RevokeAllAsync(user.Id, now, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed class GetUserHandler(IUserRepository users)
{
    public async Task<Result<UserResponse>> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(new UserId(userId), cancellationToken);
        return user is null ? UserErrors.NotFound : UserMapping.ToResponse(user);
    }
}

public sealed class ListUsersHandler(IUserRepository users)
{
    public const int MaxPageSize = 100;

    public async Task<Result<PagedResponse<UserResponse>>> HandleAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1 || pageSize is < 1 or > MaxPageSize)
        {
            return new Error(ValidationErrors.Code, "One or more query parameters are invalid.", ErrorType.Validation)
            {
                Details = new Dictionary<string, string[]>
                {
                    ["page"] = ["Page must be 1 or greater."],
                    ["pageSize"] = [$"Page size must be between 1 and {MaxPageSize}."],
                },
            };
        }

        var result = await users.ListAsync(page, pageSize, cancellationToken);
        return new PagedResponse<UserResponse>([.. result.Items.Select(UserMapping.ToResponse)], result.Page, result.PageSize, result.TotalCount);
    }
}

/// <summary>Use case (administrators): create a user with a role and an initial password.</summary>
public sealed class CreateUserHandler(IUserRepository users, IPasswordHasher hasher, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<Result<UserResponse>> HandleAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var username = Username.Create(request.Username);
        if (!username.IsSuccess)
        {
            return username.Error;
        }

        if (!UserMapping.TryParseRole(request.Role, out var role))
        {
            return UserErrors.RoleUnknown;
        }

        var password = User.ValidatePassword(request.Password);
        if (!password.IsSuccess)
        {
            return password.Error;
        }

        if (await users.GetByUsernameAsync(username.Value, cancellationToken) is not null)
        {
            return UserErrors.UsernameInUse;
        }

        var user = User.Create(username.Value, hasher.Hash(request.Password!), role, clock.GetUtcNowForStorage());
        users.Add(user);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return UserErrors.UsernameInUse; // two admins creating the same name at once
        }

        return UserMapping.ToResponse(user);
    }
}

/// <summary>
/// Use case (administrators): change a user's role or (de)activate them. Their refresh tokens are revoked, so the change
/// applies at the latest when the current access token expires (15 minutes).
/// </summary>
public sealed class UpdateUserHandler(IUserRepository users, IRefreshTokenRepository refreshTokens, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<Result<UserResponse>> HandleAsync(Guid actingUserId, Guid userId, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        UserRole? role = null;
        if (request.Role is not null)
        {
            if (!UserMapping.TryParseRole(request.Role, out var parsed))
            {
                return UserErrors.RoleUnknown;
            }

            role = parsed;
        }

        var user = await users.GetByIdAsync(new UserId(userId), cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound;
        }

        // The last administrator demoting themselves would lock everyone out of user management.
        if (actingUserId == userId && ((role is { } r && r != user.Role) || request.IsActive == false))
        {
            return UserErrors.CannotChangeOwnAccess;
        }

        var now = clock.GetUtcNowForStorage();
        var accessChanged = false;
        if (role is { } newRole && newRole != user.Role)
        {
            user.ChangeRole(newRole, now);
            accessChanged = true;
        }

        if (request.IsActive is { } active && active != user.IsActive)
        {
            if (active)
            {
                user.Activate(now);
            }
            else
            {
                user.Deactivate(now);
            }

            accessChanged = true;
        }

        if (accessChanged)
        {
            await refreshTokens.RevokeAllAsync(user.Id, now, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return UserMapping.ToResponse(user);
    }
}

/// <summary>
/// Use case: on first start, create the first administrator from configuration. Without it nobody could sign in to
/// create users. It runs only while the user table is empty, so a configured password cannot reset an existing account.
/// </summary>
public sealed class BootstrapAdministratorHandler(IUserRepository users, IPasswordHasher hasher, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<Result<bool>> HandleAsync(string? username, string? password, CancellationToken cancellationToken)
    {
        if (await users.AnyAsync(cancellationToken))
        {
            return false;
        }

        var name = Username.Create(username);
        if (!name.IsSuccess)
        {
            return name.Error;
        }

        var valid = User.ValidatePassword(password);
        if (!valid.IsSuccess)
        {
            return valid.Error;
        }

        users.Add(User.Create(name.Value, hasher.Hash(password!), UserRole.Administrator, clock.GetUtcNowForStorage()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}

internal static class SessionIssuer
{
    public static TokenResponse Issue(User user, IRefreshTokenRepository store, ITokenService tokens, DateTimeOffset now, RefreshToken? rotated = null)
    {
        var access = tokens.CreateAccessToken(user, now);
        var (refreshValue, refreshHash) = tokens.CreateRefreshToken();
        var refresh = RefreshToken.Issue(user.Id, refreshHash, now, tokens.RefreshTokenLifetime);
        store.Add(refresh);
        rotated?.Revoke(now, refresh.Id);
        return new TokenResponse(access.Token, access.ExpiresAt, refreshValue, refresh.ExpiresAt, UserMapping.ToResponse(user));
    }
}

public static class UserMapping
{
    public static UserResponse ToResponse(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserResponse(user.Id.Value, user.Username.Value, user.Role.ToString(), user.IsActive, user.LastLoginAt, user.CreatedAt);
    }

    public static bool TryParseRole(string? value, out UserRole role) => VehicleMapping.TryParseEnum(value, out role);
}
