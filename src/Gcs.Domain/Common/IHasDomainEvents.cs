namespace Gcs.Domain.Common;

/// <summary>
/// Non-generic view of an aggregate's pending events, so persistence can collect them from any aggregate type
/// without knowing its identifier type.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
