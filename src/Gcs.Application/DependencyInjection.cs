using FluentValidation;
using Gcs.Application.Vehicles;
using Gcs.Contracts.Vehicles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gcs.Application;

public static class DependencyInjection
{
    /// <summary>Registers application-layer services: use case handlers and validators.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TimeProvider instead of DateTimeOffset.UtcNow, so tests can control "now".
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IValidator<RegisterVehicleRequest>, VehicleFieldsValidator<RegisterVehicleRequest>>();
        services.AddSingleton<IValidator<UpdateVehicleRequest>, VehicleFieldsValidator<UpdateVehicleRequest>>();

        services.AddScoped<RegisterVehicleHandler>();
        services.AddScoped<UpdateVehicleHandler>();
        services.AddScoped<RetireVehicleHandler>();
        services.AddScoped<GetVehicleHandler>();
        services.AddScoped<ListVehiclesHandler>();

        return services;
    }
}
