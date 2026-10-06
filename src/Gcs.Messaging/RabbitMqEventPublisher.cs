using System.Text;
using Gcs.Application.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Gcs.Messaging;

/// <summary>
/// Publishes outbox entries to the events exchange with publisher confirms: <c>BasicPublishAsync</c> only returns
/// once the broker has taken responsibility for the message, so an event is marked processed only after it is
/// really stored by RabbitMQ.
/// </summary>
internal sealed class RabbitMqEventPublisher(IRabbitMqConnectionProvider connectionProvider, IOptions<RabbitMqOptions> options)
    : IIntegrationEventPublisher, IAsyncDisposable
{
    private const string JsonContentType = "application/json";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // A channel is not safe for concurrent publishes; the gate serializes them.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetChannelAsync(cancellationToken);
            var properties = new BasicProperties
            {
                MessageId = entry.Id.ToString(),
                Type = entry.Type,
                ContentType = JsonContentType,
                DeliveryMode = DeliveryModes.Persistent,
                Timestamp = new AmqpTimestamp(entry.OccurredAt.ToUnixTimeSeconds()),
            };

            await channel.BasicPublishAsync(
                exchange: options.Value.EventsExchange,
                routingKey: EventRoutingKey.From(entry.Type),
                mandatory: false,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(entry.Payload),
                cancellationToken: cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        _gate.Dispose();
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        var connection = await connectionProvider.GetConnectionAsync(cancellationToken);
        _channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        // Declaring is idempotent: it creates the exchange the first time and is a no-op afterwards.
        await _channel.ExchangeDeclareAsync(
            options.Value.EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);

        return _channel;
    }
}
