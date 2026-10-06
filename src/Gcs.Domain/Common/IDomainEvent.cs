namespace Gcs.Domain.Common;

/// <summary>
/// Something meaningful that happened inside the domain (e.g. a vehicle connected).
/// Domain events are raised by aggregates and dispatched by the application layer after a successful save.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
