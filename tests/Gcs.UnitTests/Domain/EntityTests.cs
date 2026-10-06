using Gcs.Domain.Common;

namespace Gcs.UnitTests.Domain;

public sealed class EntityTests
{
    [Fact]
    public void Entities_of_same_type_with_same_id_are_equal()
    {
        var id = Guid.NewGuid();

        var first = new TestEntity(id);
        var second = new TestEntity(id);

        first.ShouldBe(second);
        (first == second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Fact]
    public void Entities_with_different_ids_are_not_equal()
    {
        var first = new TestEntity(Guid.NewGuid());
        var second = new TestEntity(Guid.NewGuid());

        first.ShouldNotBe(second);
        (first != second).ShouldBeTrue();
    }

    [Fact]
    public void Entities_of_different_types_with_same_id_are_not_equal()
    {
        var id = Guid.NewGuid();

        Entity<Guid> first = new TestEntity(id);
        Entity<Guid> second = new OtherTestEntity(id);

        first.Equals(second).ShouldBeFalse();
    }

    [Fact]
    public void Entity_is_not_equal_to_null()
    {
        var entity = new TestEntity(Guid.NewGuid());

        entity.Equals(null).ShouldBeFalse();
        (entity == null).ShouldBeFalse();
    }

    private sealed class TestEntity(Guid id) : Entity<Guid>(id);

    private sealed class OtherTestEntity(Guid id) : Entity<Guid>(id);
}
