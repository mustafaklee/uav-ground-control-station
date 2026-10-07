using Gcs.Application.Abstractions;
using Gcs.Mavlink.Commands;
using Gcs.Mavlink.Connections;
using Gcs.Mavlink.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gcs.Mavlink;

public static class DependencyInjection
{
    public static IServiceCollection AddMavlink(this IServiceCollection services)
    {
        services.AddOptions<MavlinkConnectionOptions>()
            .BindConfiguration(MavlinkConnectionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IMavlinkTransportFactory, MavlinkTransportFactory>();
        services.AddSingleton<VehicleLinkManager>();
        services.AddSingleton<IVehicleLinkManager>(sp => sp.GetRequiredService<VehicleLinkManager>());
        services.AddSingleton<IVehicleMissionTransfer>(sp => sp.GetRequiredService<VehicleLinkManager>());
        services.AddSingleton<IVehicleCommandSender>(sp => sp.GetRequiredService<VehicleLinkManager>());
        services.AddSingleton<IFlightModeCatalog, FlightModeCatalog>();
        return services;
    }
}
