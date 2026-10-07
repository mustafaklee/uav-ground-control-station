using Gcs.Application.Abstractions;
using Gcs.Application.Common;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Vehicles;

/// <summary>
/// Use case: take a vehicle out of service (the DELETE endpoint). The row is kept for audit history and any live link
/// is closed.
/// </summary>
public sealed class RetireVehicleHandler(
    IVehicleRepository vehicles,
    IUnitOfWork unitOfWork,
    IVehicleLinkManager links,
    TimeProvider clock)
{
    public async Task<Result> HandleAsync(Guid id, int? expectedVersion, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.GetByIdAsync(new VehicleId(id), cancellationToken);
        if (vehicle is null)
        {
            return VehicleErrors.NotFound;
        }

        var retire = vehicle.Retire(expectedVersion, clock.GetUtcNowForStorage());
        if (!retire.IsSuccess)
        {
            return retire;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return VehicleErrors.VersionMismatch;
        }

        // A retired vehicle must not stay connected: nobody should command or watch it as if it were in service.
        await links.DisconnectAsync(vehicle.Id, cancellationToken);
        return Result.Success();
    }
}
