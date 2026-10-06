using System.ComponentModel.DataAnnotations;

namespace Gcs.Infrastructure.Outbox;

public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "Outbox";

    public bool Enabled { get; init; } = true;

    /// <summary>How long to wait before checking again when the outbox was empty or publishing failed.</summary>
    [Range(100, 60_000)]
    public int PollingIntervalMilliseconds { get; init; } = 1000;

    [Range(1, 1000)]
    public int BatchSize { get; init; } = 50;
}
