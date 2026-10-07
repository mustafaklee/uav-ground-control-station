using Gcs.Application.Vehicles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gcs.Infrastructure.LinkEvents;

/// <summary>Restores the links the operator had open, once, when the host starts (after migrations ran).</summary>
internal sealed partial class VehicleLinkRestorer(IServiceScopeFactory scopes, ILogger<VehicleLinkRestorer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var restored = await scope.ServiceProvider.GetRequiredService<RestoreVehicleLinksHandler>().HandleAsync(stoppingToken);
            LogRestored(restored);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            // Database unavailable at startup: links can still be opened manually; do not crash the host.
            LogRestoreFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restored {Count} vehicle link(s) requested before the restart")]
    private partial void LogRestored(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Restoring vehicle links at startup failed")]
    private partial void LogRestoreFailed(Exception exception);
}
