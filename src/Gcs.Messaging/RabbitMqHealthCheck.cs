using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gcs.Messaging;

internal sealed class RabbitMqHealthCheck(IRabbitMqConnectionProvider connectionProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await connectionProvider.GetConnectionAsync(cancellationToken);
            return connection.IsOpen
                ? HealthCheckResult.Healthy("RabbitMQ connection is open.")
                : new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ connection is closed.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ is unreachable.", ex);
        }
    }
}
