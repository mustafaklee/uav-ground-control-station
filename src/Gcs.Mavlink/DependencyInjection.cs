using Gcs.Application.Abstractions;
using Gcs.Mavlink.Commands;
using Gcs.Mavlink.Connections;
using Gcs.Mavlink.Network;
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
        services.AddSingleton<UdpEndpointHub>();
        services.AddSingleton<IMavlinkTransportFactory, MavlinkTransportFactory>();
        services.AddSingleton<VehicleLinkManager>();
        services.AddSingleton<IVehicleLinkManager>(sp => sp.GetRequiredService<VehicleLinkManager>());
        services.AddSingleton<IVehicleMissionTransfer>(sp => sp.GetRequiredService<VehicleLinkManager>());
        services.AddSingleton<IVehicleCommandSender>(sp => sp.GetRequiredService<VehicleLinkManager>());
        services.AddSingleton<IFlightModeCatalog, FlightModeCatalog>();

        // Network topology and radios (ADR-019). More IRadioNetworkProvider implementations (a mesh radio) can be added.
        services.AddSingleton<MavlinkNetworkTopology>();
        services.AddSingleton<INetworkTopologySource>(sp => sp.GetRequiredService<MavlinkNetworkTopology>());
        services.AddSingleton<IRadioNetworkProvider>(sp => sp.GetRequiredService<MavlinkNetworkTopology>());
        return services;
    }
}
