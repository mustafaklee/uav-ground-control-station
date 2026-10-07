using Gcs.Application.Abstractions;
using Gcs.Domain.Vehicles;
using Gcs.Mavlink.Translation;

namespace Gcs.Mavlink.Commands;

internal sealed class FlightModeCatalog : IFlightModeCatalog
{
    public IReadOnlyList<string> GetModes(AutopilotType autopilot, VehicleType type) =>
        FlightModeDecoder.SelectableModes(CommandMapper.ToMavAutopilot(autopilot), CommandMapper.ToMavType(type));
}
