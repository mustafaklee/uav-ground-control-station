using Gcs.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gcs.Telemetry.History;

/// <summary>
/// Drains the history buffer and writes samples in batches: when <c>HistoryBatchSize</c> samples are waiting or every
/// <c>HistoryFlushInterval</c>, whichever comes first. One INSERT batch of 500 rows is far cheaper than 500 round trips.
/// If the database fails, the batch is retried after the flush interval; meanwhile the bounded buffer protects memory.
/// </summary>
internal sealed partial class TelemetryHistoryWriter(
    TelemetryHistoryBuffer buffer,
    IServiceScopeFactory scopes,
    IOptions<TelemetryOptions> options,
    TimeProvider time,
    ILogger<TelemetryHistoryWriter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var flushInterval = TimeSpan.FromMilliseconds(settings.HistoryFlushIntervalMilliseconds);
        var batch = new List<TelemetrySample>(settings.HistoryBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FillBatchAsync(batch, settings.HistoryBatchSize, flushInterval, stoppingToken);
                if (batch.Count == 0)
                {
                    continue;
                }

                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ITelemetryHistoryStore>().AppendAsync(batch, stoppingToken);
                LogWritten(batch.Count);
                batch.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogWriteFailed(ex, batch.Count, buffer.DroppedSamples);
                await Task.Delay(flushInterval, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task FillBatchAsync(List<TelemetrySample> batch, int batchSize, TimeSpan flushInterval, CancellationToken stoppingToken)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        window.CancelAfter(flushInterval);
        try
        {
            while (batch.Count < batchSize && await buffer.Reader.WaitToReadAsync(window.Token))
            {
                while (batch.Count < batchSize && buffer.Reader.TryRead(out var sample))
                {
                    batch.Add(sample);
                }
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // flush interval elapsed: write what we have
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wrote {Count} telemetry history sample(s)")]
    private partial void LogWritten(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing {Count} telemetry history sample(s) failed; retrying. Samples dropped so far: {Dropped}")]
    private partial void LogWriteFailed(Exception exception, int count, long dropped);
}
