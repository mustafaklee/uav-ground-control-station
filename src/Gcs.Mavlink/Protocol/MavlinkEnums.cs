namespace Gcs.Mavlink.Protocol;

// Subsets of the enums in the MAVLink common.xml dialect. Values must match the standard exactly.

/// <summary>MAV_TYPE: what kind of system sent the heartbeat.</summary>
public enum MavType : byte
{
    Generic = 0,
    FixedWing = 1,
    Quadrotor = 2,
    Helicopter = 4,
    Gcs = 6,
    Hexarotor = 13,
    Octorotor = 14,
    VtolTailsitterDuorotor = 19,
    VtolTailsitterQuadrotor = 20,
    VtolTiltrotor = 21,
    VtolFixedrotor = 22,
}

/// <summary>MAV_AUTOPILOT.</summary>
public enum MavAutopilot : byte
{
    Generic = 0,
    ArduPilotMega = 3,
    Invalid = 8,
    Px4 = 12,
}

/// <summary>MAV_MODE_FLAG (bit flags in HEARTBEAT.base_mode).</summary>
[Flags]
public enum MavBaseMode : byte
{
    None = 0,
    CustomModeEnabled = 1,
    TestEnabled = 2,
    AutoEnabled = 4,
    GuidedEnabled = 8,
    StabilizeEnabled = 16,
    HilEnabled = 32,
    ManualInputEnabled = 64,
    SafetyArmed = 128,
}

/// <summary>MAV_STATE.</summary>
public enum MavState : byte
{
    Uninit = 0,
    Boot = 1,
    Calibrating = 2,
    Standby = 3,
    Active = 4,
    Critical = 5,
    Emergency = 6,
    Poweroff = 7,
    FlightTermination = 8,
}

/// <summary>GPS_FIX_TYPE.</summary>
public enum GpsFixType : byte
{
    NoGps = 0,
    NoFix = 1,
    Fix2D = 2,
    Fix3D = 3,
    Dgps = 4,
    RtkFloat = 5,
    RtkFixed = 6,
    Static = 7,
    Ppp = 8,
}

/// <summary>MAV_CMD (subset). Used with COMMAND_LONG.</summary>
public enum MavCmd : ushort
{
    NavReturnToLaunch = 20,
    NavLand = 21,
    NavTakeoff = 22,
    DoSetMode = 176,
    ComponentArmDisarm = 400,
    RequestMessage = 512,
}

/// <summary>MAV_RESULT: the outcome of a command, carried by COMMAND_ACK.</summary>
public enum MavResult : byte
{
    Accepted = 0,
    TemporarilyRejected = 1,
    Denied = 2,
    Unsupported = 3,
    Failed = 4,
    InProgress = 5,
    Cancelled = 6,
}

/// <summary>Well-known component ids.</summary>
public static class MavComponent
{
    public const byte Autopilot1 = 1;

    /// <summary>MAV_COMP_ID_MISSIONPLANNER, conventionally used by ground control stations.</summary>
    public const byte MissionPlanner = 190;
}
