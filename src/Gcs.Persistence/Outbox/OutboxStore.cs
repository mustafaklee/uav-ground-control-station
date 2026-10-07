using Gcs.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gcs.Persistence.Outbox;

internal sealed partial class OutboxStore(GcsDbContext db, TimeProvider clock, ILogger<OutboxStore> logger) : IOutboxStore
{
    public async Task<int> PublishPendingAsync(
        int batchSize,
        Func<OutboxEntry, CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);

        // With EnableRetryOnFailure, a manual transaction must run inside the execution strategy
        // so the whole unit (lock, publish, mark) is retried together on a transient database error.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // FOR UPDATE SKIP LOCKED: several API instances can run the dispatcher at the same time;
            // each one takes different rows instead of publishing the same event twice or waiting on each other.
            var pending = await db.OutboxMessages
                .FromSql($"""
                    SELECT * FROM gcs.outbox_messages
                    WHERE processed_at IS NULL
                    ORDER BY occurred_at
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);

            var published = 0;
            foreach (var message in pending)
            {
                try
                {
                    await publish(new OutboxEntry(message.Id, message.Type, message.Payload, message.OccurredAt, message.TraceParent), ct);
                    message.ProcessedAt = clock.GetUtcNow();
                    published++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    message.Attempts++;
                    message.LastError = Truncate(ex.Message, OutboxMessage.MaxErrorLength);
                    LogPublishFailed(ex, message.Id, message.Type, message.Attempts);
                    break; // keep order: later events wait until this one is published
                }
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return published;
        }, cancellationToken);
    }

    public async Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken)
    {
        var pending = db.OutboxMessages.AsNoTracking().Where(m => m.ProcessedAt == null);
        var count = await pending.CountAsync(cancellationToken);
        var oldest = count == 0 ? null : await pending.MinAsync(m => (DateTimeOffset?)m.OccurredAt, cancellationToken);
        return new OutboxBacklog(count, oldest);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing outbox message {MessageId} ({Type}) failed, attempt {Attempt}")]
    private partial void LogPublishFailed(Exception exception, Guid messageId, string type, int attempt);
}
