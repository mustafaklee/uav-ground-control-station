using Gcs.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gcs.Telemetry.History;

/// <summary>Deletes history older than the retention period, so the table does not grow forever.</summary>
internal sealed partial class TelemetryRetentionService(
    IServiceScopeFactory scopes,
    IOptions<TelemetryOptions> options,
    TimeProvider time,
    ILogger<TelemetryRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (settings.HistoryRetentionDays == 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.RetentionSweepIntervalMinutes), time);
        try
        {
            do
            {
                await SweepAsync(settings.HistoryRetentionDays, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task SweepAsync(int retentionDays, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var cutoff = time.GetUtcNow().AddDays(-retentionDays);
            var deleted = await scope.ServiceProvider.GetRequiredService<ITelemetryHistoryStore>().DeleteOlderThanAsync(cutoff, stoppingToken);
            if (deleted > 0)
            {
                LogDeleted(deleted, retentionDays);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSweepFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} telemetry history sample(s) older than {Days} days")]
    private partial void LogDeleted(int count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry history retention sweep failed")]
    private partial void LogSweepFailed(Exception exception);
}
