using FluentValidation;
using Gcs.Application.Abstractions;
using Gcs.Application.Common;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Vehicles;

/// <summary>Use case: change a vehicle's registration, guarded by the version the operator edited.</summary>
public sealed class UpdateVehicleHandler(
    IValidator<UpdateVehicleRequest> validator,
    IVehicleRepository vehicles,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<Result<VehicleResponse>> HandleAsync(
        Guid id,
        int expectedVersion,
        UpdateVehicleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationErrors.From(validation);
        }

        var vehicle = await vehicles.GetByIdAsync(new VehicleId(id), cancellationToken);
        if (vehicle is null)
        {
            return VehicleErrors.NotFound;
        }

        var input = VehicleMapping.ToInput(request);

        // Only active vehicles reserve callsigns and system ids, and the vehicle itself does not count as a clash.
        if (!vehicle.IsRetired)
        {
            var uniqueness = await VehicleUniqueness.CheckAsync(vehicles, input, vehicle.Id, cancellationToken);
            if (!uniqueness.IsSuccess)
            {
                return uniqueness.Error;
            }
        }

        var update = vehicle.Update(
            input.Callsign, input.SystemId, input.Autopilot, input.Type, input.Connection, expectedVersion, clock.GetUtcNowForStorage());
        if (!update.IsSuccess)
        {
            return update.Error;
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Someone saved a newer version between our read and our write.
            return VehicleErrors.VersionMismatch;
        }
        catch (UniqueConstraintViolationException ex)
        {
            return VehicleUniqueness.ToError(ex);
        }

        return VehicleMapping.ToResponse(vehicle);
    }
}
