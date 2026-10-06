using Gcs.Domain.Common;

namespace Gcs.UnitTests.Domain;

public sealed class AggregateRootTests
{
    [Fact]
    public void Raised_domain_events_are_recorded_in_order()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());

        aggregate.DoSomething("first");
        aggregate.DoSomething("second");

        aggregate.DomainEvents.Cast<SomethingHappened>().Select(e => e.Name).ShouldBe(["first", "second"]);
    }

    [Fact]
    public void Clearing_domain_events_empties_the_list()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());
        aggregate.DoSomething("event");

        aggregate.ClearDomainEvents();

        aggregate.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Raising_a_null_domain_event_throws()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());

        Should.Throw<ArgumentNullException>(aggregate.RaiseNull);
    }

    private sealed record SomethingHappened(string Name, DateTimeOffset OccurredAt) : IDomainEvent;

    private sealed class TestAggregate(Guid id) : AggregateRoot<Guid>(id)
    {
        public void DoSomething(string name) => RaiseDomainEvent(new SomethingHappened(name, DateTimeOffset.UnixEpoch));

        public void RaiseNull() => RaiseDomainEvent(null!);
    }
}
