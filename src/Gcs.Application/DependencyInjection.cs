using FluentValidation;
using Gcs.Application.Commands;
using Gcs.Application.Missions;
using Gcs.Application.Security;
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
        services.AddScoped<ConnectVehicleHandler>();
        services.AddScoped<DisconnectVehicleHandler>();
        services.AddScoped<RestoreVehicleLinksHandler>();
        services.AddScoped<GetVehicleLinkHandler>();
        services.AddScoped<GetLatestTelemetryHandler>();
        services.AddScoped<TelemetryHistoryHandler>();

        services.AddScoped<CreateMissionHandler>();
        services.AddScoped<UpdateMissionHandler>();
        services.AddScoped<GetMissionHandler>();
        services.AddScoped<ListMissionsHandler>();
        services.AddScoped<ArchiveMissionHandler>();
        services.AddScoped<UploadMissionHandler>();
        services.AddScoped<DownloadVehicleMissionHandler>();

        services.AddScoped<AcquireCommandLeaseHandler>();
        services.AddScoped<ReleaseCommandLeaseHandler>();
        services.AddScoped<GetCommandLeaseHandler>();
        services.AddScoped<GetFlightModesHandler>();
        services.AddScoped<SendVehicleCommandHandler>();
        services.AddScoped<ListCommandAuditHandler>();

        services.AddScoped<LoginHandler>();
        services.AddScoped<RefreshSessionHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<ChangePasswordHandler>();
        services.AddScoped<GetUserHandler>();
        services.AddScoped<ListUsersHandler>();
        services.AddScoped<CreateUserHandler>();
        services.AddScoped<UpdateUserHandler>();
        services.AddScoped<BootstrapAdministratorHandler>();

        return services;
    }
}
