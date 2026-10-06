using RabbitMQ.Client;

namespace Gcs.Messaging;

/// <summary>
/// Owns the single long-lived AMQP connection for this process. RabbitMQ connections are expensive (TCP, TLS, handshake),
/// so one is shared and channels are created per publisher/consumer instead.
/// </summary>
public interface IRabbitMqConnectionProvider
{
    Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken);
}
