namespace Gcs.Application.Vehicles;

/// <summary>
/// Names of the database unique indexes that protect vehicle invariants. Shared with Gcs.Persistence so a
/// violation reported by PostgreSQL can be translated back into the matching domain error.
/// </summary>
public static class VehicleConstraints
{
    public const string ActiveCallsignUnique = "ux_vehicles_callsign_active";
    public const string ActiveSystemIdUnique = "ux_vehicles_system_id_active";
}
