using Npgsql;
using OpenTelemetry.Trace;

namespace Gcs.Infrastructure;

public static class AdapterInstrumentation
{
    /// <summary>
    /// Tracing for the adapters this layer owns (one span per SQL command). Lives here, not in the API, so the API keeps
    /// reaching databases and brokers only through Infrastructure (architecture rule).
    /// </summary>
    public static TracerProviderBuilder AddAdapterInstrumentation(this TracerProviderBuilder tracing) => tracing.AddNpgsql();
}
