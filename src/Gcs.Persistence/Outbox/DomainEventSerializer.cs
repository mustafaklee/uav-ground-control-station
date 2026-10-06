using System.Text.Json;
using System.Text.Json.Serialization;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Persistence.Outbox;

/// <summary>
/// Serializes domain events into the JSON stored in the outbox and later sent to RabbitMQ.
/// Value objects are written as plain values (<c>"callsign": "UAV-01"</c>, not <c>{"value": "UAV-01"}</c>)
/// so consumers do not need to know our domain types.
/// </summary>
internal static class DomainEventSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static OutboxMessage ToOutboxMessage(IDomainEvent domainEvent) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = domainEvent.GetType().Name,
        Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Options),
        OccurredAt = domainEvent.OccurredAt,
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new ValueConverter<VehicleId, Guid>(id => id.Value));
        options.Converters.Add(new ValueConverter<Callsign, string>(callsign => callsign.Value));
        options.Converters.Add(new ValueConverter<MavlinkSystemId, int>(id => id.Value));
        return options;
    }

    /// <summary>Write-only converter: events are serialized for consumers and never deserialized back here.</summary>
    private sealed class ValueConverter<TValue, TPrimitive>(Func<TValue, TPrimitive> toPrimitive) : JsonConverter<TValue>
    {
        public override TValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Outbox events are write-only.");

        public override void Write(Utf8JsonWriter writer, TValue value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, toPrimitive(value), options);
    }
}
