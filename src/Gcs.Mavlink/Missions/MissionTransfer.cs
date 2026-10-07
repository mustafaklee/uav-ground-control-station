using System.Threading.Channels;
using Gcs.Domain.Common;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;

namespace Gcs.Mavlink.Missions;

public sealed record MissionTransferOptions
{
    /// <summary>How long to wait for each answer before resending the last message.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Resends per step before giving up. Radio links lose packets; a few retries are normal, not an error.</summary>
    public int MaxRetries { get; init; } = 5;
}

/// <summary>
/// The MAVLink mission protocol as a small state machine (https://mavlink.io/en/services/mission.html).
/// <code>
/// Upload:   GCS ──MISSION_COUNT(n)──►        Download: GCS ──MISSION_REQUEST_LIST──►
///           GCS ◄──MISSION_REQUEST_INT(0)──            GCS ◄──MISSION_COUNT(n)──
///           GCS ──MISSION_ITEM_INT(0)──►               GCS ──MISSION_REQUEST_INT(0)──►
///           ... (vehicle asks for each item)           GCS ◄──MISSION_ITEM_INT(0)── ... ×n
///           GCS ◄──MISSION_ACK(ACCEPTED)──             GCS ──MISSION_ACK(ACCEPTED)──►
/// </code>
/// The receiving side drives the transfer by requesting items, so lost packets are recovered by re-requesting.
/// Our side resends its last message when an answer does not arrive in time, a bounded number of times.
/// </summary>
public sealed class MissionTransfer(
    Func<IMavlinkMessage, CancellationToken, ValueTask> send,
    ChannelReader<IMavlinkMessage> inbox,
    MissionTransferOptions options,
    TimeProvider time,
    byte targetSystem,
    byte targetComponent)
{
    public static readonly Error NoResponse = Error.Conflict(
        "vehicle.mission.no_response", "The vehicle stopped answering during the mission transfer.");

    public async Task<Result> UploadAsync(IReadOnlyList<MissionItemIntMessage> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        IMavlinkMessage lastSent = new MissionCountMessage(targetSystem, targetComponent, (ushort)items.Count);
        await send(lastSent, cancellationToken);

        var retries = 0;
        while (true)
        {
            var reply = await ReceiveAsync(cancellationToken);
            switch (reply)
            {
                case null when retries++ < options.MaxRetries:
                    await send(lastSent, cancellationToken);
                    break;
                case null:
                    return NoResponse;
                case MissionRequestIntMessage request when request.Seq < items.Count:
                    lastSent = items[request.Seq];
                    retries = 0;
                    await send(lastSent, cancellationToken);
                    break;
                case MissionRequestIntMessage request:
                    return Rejected($"The vehicle requested item {request.Seq} of a {items.Count}-item mission.");
                case MissionAckMessage { Type: MavMissionResult.Accepted }:
                    return Result.Success();
                case MissionAckMessage ack:
                    return Rejected($"The vehicle rejected the mission: {ack.Type}.");
                default:
                    break; // unrelated mission message (e.g. MISSION_CURRENT): keep waiting
            }
        }
    }

    public async Task<Result<IReadOnlyList<MissionItemIntMessage>>> DownloadAsync(CancellationToken cancellationToken)
    {
        var count = await RequestAsync<MissionCountMessage>(
            new MissionRequestListMessage(targetSystem, targetComponent), _ => true, cancellationToken);
        if (count is null)
        {
            return NoResponse;
        }

        var items = new List<MissionItemIntMessage>(count.Count);
        for (ushort seq = 0; seq < count.Count; seq++)
        {
            var current = seq;
            var item = await RequestAsync<MissionItemIntMessage>(
                new MissionRequestIntMessage(targetSystem, targetComponent, current), m => m.Seq == current, cancellationToken);
            if (item is null)
            {
                return NoResponse;
            }

            items.Add(item);
        }

        await send(new MissionAckMessage(targetSystem, targetComponent, MavMissionResult.Accepted), cancellationToken);
        return items;
    }

    private async Task<TReply?> RequestAsync<TReply>(IMavlinkMessage request, Func<TReply, bool> matches, CancellationToken cancellationToken)
        where TReply : class, IMavlinkMessage
    {
        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            await send(request, cancellationToken);
            var deadline = time.GetUtcNow() + options.ResponseTimeout;
            while (time.GetUtcNow() < deadline)
            {
                var reply = await ReceiveAsync(cancellationToken);
                if (reply is TReply typed && matches(typed))
                {
                    return typed;
                }

                if (reply is null)
                {
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>Next mission message, or null when none arrives within the response timeout.</summary>
    private async Task<IMavlinkMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.ResponseTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await inbox.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static Error Rejected(string message) => Error.Conflict("vehicle.mission.rejected", message);
}
