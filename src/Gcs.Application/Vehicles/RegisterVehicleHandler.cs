using FluentValidation;
using Gcs.Application.Abstractions;
using Gcs.Application.Common;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Vehicles;

/// <summary>Use case: register a new vehicle.</summary>
public sealed class RegisterVehicleHandler(
    IValidator<RegisterVehicleRequest> validator,
    IVehicleRepository vehicles,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<Result<VehicleResponse>> HandleAsync(RegisterVehicleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationErrors.From(validation);
        }

        var input = VehicleMapping.ToInput(request);

        var uniqueness = await VehicleUniqueness.CheckAsync(vehicles, input, excluding: null, cancellationToken);
        if (!uniqueness.IsSuccess)
        {
            return uniqueness.Error;
        }

        var vehicle = Vehicle.Register(input.Callsign, input.SystemId, input.Autopilot, input.Type, input.Connection, clock.GetUtcNowForStorage());
        vehicles.Add(vehicle);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException ex)
        {
            // Another request registered the same callsign/system id between our check and our save.
            return VehicleUniqueness.ToError(ex);
        }

        return VehicleMapping.ToResponse(vehicle);
    }
}
