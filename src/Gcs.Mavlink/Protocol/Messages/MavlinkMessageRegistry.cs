using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Gcs.Mavlink.Protocol.Messages;

/// <summary>
/// The messages this GCS understands. Ids, lengths and CRC_EXTRA values come from MAVLink common.xml and are
/// verified by golden tests against pymavlink. Adding a message: create its record, add it here, add a golden case.
/// </summary>
public static class MavlinkMessageRegistry
{
    private static readonly FrozenDictionary<uint, MavlinkMessageInfo> Messages = new MavlinkMessageInfo[]
    {
        new(HeartbeatMessage.Id, "HEARTBEAT", 50, HeartbeatMessage.Length, HeartbeatMessage.Read),
        new(SysStatusMessage.Id, "SYS_STATUS", 124, SysStatusMessage.Length, SysStatusMessage.Read),
        new(GpsRawIntMessage.Id, "GPS_RAW_INT", 24, GpsRawIntMessage.Length, GpsRawIntMessage.Read),
        new(AttitudeMessage.Id, "ATTITUDE", 39, AttitudeMessage.Length, AttitudeMessage.Read),
        new(GlobalPositionIntMessage.Id, "GLOBAL_POSITION_INT", 104, GlobalPositionIntMessage.Length, GlobalPositionIntMessage.Read),
        new(VfrHudMessage.Id, "VFR_HUD", 20, VfrHudMessage.Length, VfrHudMessage.Read),
        new(CommandLongMessage.Id, "COMMAND_LONG", 152, CommandLongMessage.Length, CommandLongMessage.Read),
        new(CommandAckMessage.Id, "COMMAND_ACK", 143, CommandAckMessage.Length, CommandAckMessage.Read),
        new(MissionCurrentMessage.Id, "MISSION_CURRENT", 28, MissionCurrentMessage.Length, MissionCurrentMessage.Read),
        new(MissionRequestListMessage.Id, "MISSION_REQUEST_LIST", 132, MissionRequestListMessage.Length, MissionRequestListMessage.Read),
        new(MissionCountMessage.Id, "MISSION_COUNT", 221, MissionCountMessage.Length, MissionCountMessage.Read),
        new(MissionItemReachedMessage.Id, "MISSION_ITEM_REACHED", 11, MissionItemReachedMessage.Length, MissionItemReachedMessage.Read),
        new(MissionAckMessage.Id, "MISSION_ACK", 153, MissionAckMessage.Length, MissionAckMessage.Read),
        new(MissionRequestIntMessage.Id, "MISSION_REQUEST_INT", 196, MissionRequestIntMessage.Length, MissionRequestIntMessage.Read),
        new(MissionItemIntMessage.Id, "MISSION_ITEM_INT", 38, MissionItemIntMessage.Length, MissionItemIntMessage.Read),
    }.ToFrozenDictionary(info => info.Id);

    public static bool TryGet(uint messageId, [NotNullWhen(true)] out MavlinkMessageInfo? info) =>
        Messages.TryGetValue(messageId, out info);

    public static MavlinkMessageInfo Get(uint messageId) =>
        Messages.TryGetValue(messageId, out var info)
            ? info
            : throw new ArgumentOutOfRangeException(nameof(messageId), messageId, "Message is not registered.");
}
