using Gcs.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gcs.Infrastructure.Outbox;

/// <summary>
/// Background loop that moves events from the outbox table to RabbitMQ.
/// When the broker or database is down it logs, waits and tries again; it never stops the API.
/// When a full batch was published it continues immediately, so a backlog drains quickly.
/// </summary>
internal sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IIntegrationEventPublisher publisher,
    IOptions<OutboxDispatcherOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            LogDisabled();
            return;
        }

        var interval = TimeSpan.FromMilliseconds(settings.PollingIntervalMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                // DbContext is scoped; a fresh scope per batch keeps the change tracker small and isolated.
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
                published = await store.PublishPendingAsync(settings.BatchSize, publisher.PublishAsync, stoppingToken);
                if (published > 0)
                {
                    LogPublished(published);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Database unreachable etc. Must not crash the host; the events stay in the outbox.
                LogDispatchFailed(ex);
            }

            if (published < settings.BatchSize)
            {
                await Task.Delay(interval, clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox dispatcher is disabled by configuration")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Published {Count} outbox message(s)")]
    private partial void LogPublished(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch cycle failed; retrying after the polling interval")]
    private partial void LogDispatchFailed(Exception exception);
}
