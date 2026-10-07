using Gcs.Application.Abstractions;
using Gcs.Contracts.Common;
using Gcs.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Gcs.Persistence.Users;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    private const int EnumColumnLength = 16;
    private const int HashLength = 256;

    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Ignore(u => u.DomainEvents);
        builder.Property(u => u.Id).HasConversion(id => id.Value, value => new UserId(value)).ValueGeneratedNever();
        builder.Property(u => u.Username)
            .HasConversion(name => name.Value, value => Username.Create(value).Value)
            .HasMaxLength(Username.MaxLength)
            .IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(HashLength).IsRequired();
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(EnumColumnLength);

        // Last line of defence behind the "username in use" check, which two simultaneous requests could both pass.
        builder.HasIndex(u => u.Username).IsUnique().HasDatabaseName("ux_users_username");
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    private const int HashLength = 64; // SHA-256 as hex

    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasConversion(id => id.Value, value => new RefreshTokenId(value)).ValueGeneratedNever();
        builder.Property(t => t.UserId).HasConversion(id => id.Value, value => new UserId(value));
        builder.Property(t => t.ReplacedBy).HasConversion(new ValueConverter<RefreshTokenId, Guid>(id => id.Value, value => new RefreshTokenId(value)));
        builder.Property(t => t.TokenHash).HasMaxLength(HashLength).IsFixedLength().IsRequired();

        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_hash");
        builder.HasIndex(t => t.UserId).HasDatabaseName("ix_refresh_tokens_user");
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserRepository(GcsDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(UserId id, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<User?> GetByUsernameAsync(Username username, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(u => u.Username == username, cancellationToken);

    public Task<bool> AnyAsync(CancellationToken cancellationToken) => db.Users.AnyAsync(cancellationToken);

    public void Add(User user) => db.Users.Add(user);

    public async Task<PagedResponse<User>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(u => u.Username).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResponse<User>(items, page, pageSize, total);
    }
}

internal sealed class RefreshTokenRepository(GcsDbContext db) : IRefreshTokenRepository
{
    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);

    public async Task RevokeAllAsync(UserId userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken);
        active.ForEach(t => t.Revoke(now));
    }
}
