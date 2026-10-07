using Gcs.Application.Abstractions;
using Gcs.Domain.Common;

namespace Gcs.Persistence.Outbox;

/// <summary>Writes events that do not come from an aggregate save (e.g. link events) straight into the outbox.</summary>
internal sealed class EventOutbox(GcsDbContext db) : IEventOutbox
{
    public async Task EnqueueAsync(IReadOnlyCollection<IDomainEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        db.OutboxMessages.AddRange(events.Select(DomainEventSerializer.ToOutboxMessage));
        await db.SaveChangesAsync(cancellationToken);
    }
}
