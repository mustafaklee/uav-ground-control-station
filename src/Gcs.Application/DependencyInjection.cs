using Microsoft.Extensions.DependencyInjection;

namespace Gcs.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers application-layer services (use cases, validators). Populated from Phase 2 onwards.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
