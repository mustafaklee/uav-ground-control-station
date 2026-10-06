using Gcs.Application.Abstractions;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Vehicles;

/// <summary>
/// "No two active vehicles share a callsign or a MAVLink system id." Checked here first to give a clear error,
/// and enforced again by unique indexes in the database for the race where two requests pass the check together.
/// </summary>
internal static class VehicleUniqueness
{
    public static async Task<Result> CheckAsync(
        IVehicleRepository vehicles,
        VehicleInput input,
        VehicleId? excluding,
        CancellationToken cancellationToken)
    {
        if (await vehicles.IsCallsignInUseAsync(input.Callsign, excluding, cancellationToken))
        {
            return VehicleErrors.CallsignInUse;
        }

        if (await vehicles.IsSystemIdInUseAsync(input.SystemId, excluding, cancellationToken))
        {
            return VehicleErrors.SystemIdInUse;
        }

        return Result.Success();
    }

    public static Error ToError(UniqueConstraintViolationException exception) =>
        exception.ConstraintName switch
        {
            VehicleConstraints.ActiveSystemIdUnique => VehicleErrors.SystemIdInUse,
            VehicleConstraints.ActiveCallsignUnique => VehicleErrors.CallsignInUse,
            _ => throw new InvalidOperationException(
                $"Unexpected unique constraint violation '{exception.ConstraintName}'.", exception),
        };
}
