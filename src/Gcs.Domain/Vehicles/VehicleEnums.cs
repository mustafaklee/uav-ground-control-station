namespace Gcs.Domain.Vehicles;

/// <summary>Flight stack running on the vehicle. Flight modes and mission details differ between them.</summary>
public enum AutopilotType
{
    Px4 = 1,
    ArduPilot = 2,
    Simulator = 3,
}

public enum VehicleType
{
    Multirotor = 1,
    FixedWing = 2,
    Vtol = 3,
}

public enum VehicleStatus
{
    Active = 1,

    /// <summary>Taken out of service. Kept (not deleted) so audit records that reference it stay meaningful.</summary>
    Retired = 2,
}

/// <summary>How the GCS reaches the vehicle.</summary>
public enum TransportType
{
    Udp = 1,
    Tcp = 2,
    Serial = 3,
    Simulator = 4,
}
