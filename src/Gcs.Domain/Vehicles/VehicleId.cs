namespace Gcs.Domain.Vehicles;

/// <summary>
/// Strongly typed vehicle identifier. Wrapping the Guid means a mission id can never be passed where a vehicle id
/// is expected: the compiler rejects it.
/// </summary>
public readonly record struct VehicleId(Guid Value)
{
    /// <summary>Version 7 Guids are time ordered, which keeps database index inserts sequential.</summary>
    public static VehicleId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
