namespace Gcs.Domain.Common;

/// <summary>
/// Base type for objects that have an identity which stays the same while their attributes change.
/// Two entities are equal when they are of the same type and have the same identifier.
/// </summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    protected Entity(TId id)
    {
        Id = id;
    }

    public TId Id { get; }

    public bool Equals(Entity<TId>? other) =>
        other is not null
        && (ReferenceEquals(this, other) || (other.GetType() == GetType() && EqualityComparer<TId>.Default.Equals(Id, other.Id)));

    public override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) => Equals(left, right);

    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !Equals(left, right);
}
