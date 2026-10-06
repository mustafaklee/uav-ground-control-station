namespace Gcs.Application.Abstractions;

/// <summary>A domain event that was saved in the outbox table and still has to be published.</summary>
public sealed record OutboxEntry(Guid Id, string Type, string Payload, DateTimeOffset OccurredAt);

/// <summary>
/// The outbox table. Events are written in the same transaction as the business change, so either both are saved
/// or neither is. A background dispatcher later reads pending events and hands them to the broker.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Locks up to <paramref name="batchSize"/> pending events (skipping rows another instance already holds),
    /// calls <paramref name="publish"/> for each in order and marks the published ones as processed.
    /// Stops at the first failure so events are never published out of order; the failure is recorded
    /// and the event is retried on the next run.
    /// </summary>
    /// <returns>Number of events published.</returns>
    Task<int> PublishPendingAsync(
        int batchSize,
        Func<OutboxEntry, CancellationToken, Task> publish,
        CancellationToken cancellationToken);
}

/// <summary>Sends an integration event to other parts of the system (RabbitMQ in production).</summary>
public interface IIntegrationEventPublisher
{
    Task PublishAsync(OutboxEntry entry, CancellationToken cancellationToken);
}
