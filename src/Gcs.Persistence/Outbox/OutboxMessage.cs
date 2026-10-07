namespace Gcs.Persistence.Outbox;

/// <summary>One row of the outbox table: a serialized domain event waiting to be published to the broker.</summary>
internal sealed class OutboxMessage
{
    public const int MaxTypeLength = 128;
    public const int MaxErrorLength = 2000;

    public Guid Id { get; init; }

    /// <summary>Event name, e.g. <c>VehicleRegistered</c>. Also used to build the RabbitMQ routing key.</summary>
    public required string Type { get; init; }

    /// <summary>The event as JSON (stored as PostgreSQL jsonb).</summary>
    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// W3C <c>traceparent</c> of the operation that raised the event, e.g. the HTTP request that registered a vehicle.
    /// The publish later continues that trace, although it runs seconds later on a background thread.
    /// </summary>
    public string? TraceParent { get; init; }

    public const int MaxTraceParentLength = 64;
}
