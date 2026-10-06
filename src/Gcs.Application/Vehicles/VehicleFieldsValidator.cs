using FluentValidation;
using Gcs.Contracts.Vehicles;
using Gcs.Domain.Common;
using Gcs.Domain.Vehicles;

namespace Gcs.Application.Vehicles;

/// <summary>
/// Validates register/update requests and reports every problem at once, per field.
/// The rules themselves live in the domain value objects; this validator only calls them and collects the errors,
/// so a rule (e.g. allowed callsign characters) is written exactly once.
/// </summary>
public sealed class VehicleFieldsValidator<TRequest> : AbstractValidator<TRequest>
    where TRequest : IVehicleFields
{
    public VehicleFieldsValidator()
    {
        RuleFor(x => x.Callsign).Custom((value, context) => AddIfFailed(context, Callsign.Create(value)));

        RuleFor(x => x.MavlinkSystemId).Custom((value, context) => AddIfFailed(context, MavlinkSystemId.Create(value)));

        RuleFor(x => x.Autopilot)
            .Must(value => VehicleMapping.TryParseEnum<AutopilotType>(value, out _))
            .WithErrorCode("vehicle.autopilot")
            .WithMessage($"Autopilot must be one of: {string.Join(", ", Enum.GetNames<AutopilotType>())}.");

        RuleFor(x => x.Type)
            .Must(value => VehicleMapping.TryParseEnum<VehicleType>(value, out _))
            .WithErrorCode("vehicle.type")
            .WithMessage($"Type must be one of: {string.Join(", ", Enum.GetNames<VehicleType>())}.");

        RuleFor(x => x.Connection)
            .NotNull()
            .WithErrorCode("vehicle.connection.required")
            .WithMessage("Connection settings are required.")
            .Custom((value, context) =>
            {
                if (value is not null)
                {
                    AddIfFailed(context, VehicleMapping.ToConnectionSettings(value));
                }
            });
    }

    private static void AddIfFailed<T>(ValidationContext<TRequest> context, Result<T> result)
    {
        if (!result.IsSuccess)
        {
            context.AddFailure(new FluentValidation.Results.ValidationFailure(context.PropertyPath, result.Error.Message)
            {
                ErrorCode = result.Error.Code,
            });
        }
    }
}
