using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Gcs.Messaging;

/// <summary>
/// Lazily creates the connection on first use, so the API can start (and report "not ready")
/// while the broker is still unavailable instead of crashing at startup.
/// </summary>
public sealed partial class RabbitMqConnectionProvider(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConnectionProvider> logger) : IRabbitMqConnectionProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true } existing)
            {
                return existing;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var settings = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = settings.HostName,
                Port = settings.Port,
                VirtualHost = settings.VirtualHost,
                UserName = settings.UserName,
                Password = settings.Password,
                ClientProvidedName = settings.ClientProvidedName,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(settings.ConnectionTimeoutSeconds),
                AutomaticRecoveryEnabled = true,
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            LogConnected(settings.HostName, settings.Port);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to RabbitMQ at {Host}:{Port}")]
    private partial void LogConnected(string host, int port);
}
