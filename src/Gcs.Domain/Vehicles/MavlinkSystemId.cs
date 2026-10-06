using Gcs.Domain.Common;

namespace Gcs.Domain.Vehicles;

/// <summary>
/// The MAVLink system id (SYSID_THISMAV on the autopilot). Every message a vehicle sends carries it, and every
/// command we send is addressed to it. Two active vehicles with the same id would make commands ambiguous.
/// </summary>
public readonly record struct MavlinkSystemId
{
    /// <summary>0 is the MAVLink broadcast address and cannot identify a single vehicle.</summary>
    public const int Min = 1;
    public const int Max = 255;

    private MavlinkSystemId(byte value)
    {
        Value = value;
    }

    public byte Value { get; }

    public static Result<MavlinkSystemId> Create(int value) =>
        value is < Min or > Max
            ? VehicleErrors.SystemIdRange
            : new MavlinkSystemId((byte)value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
