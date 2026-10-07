using System.Threading.Channels;
using Gcs.Domain.Missions;
using Gcs.Domain.Vehicles;
using Gcs.Mavlink.Missions;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;
using Gcs.Mavlink.Simulation;
using Gcs.Mavlink.Translation;

namespace Gcs.UnitTests.Mavlink;

/// <summary>
/// The mission protocol against the simulated vehicle, wired back to back through a channel. Each test adjusts the
/// "link" in between: lose a message, make the vehicle refuse, or make it go silent.
/// </summary>
public sealed class MissionTransferTests
{
    private const byte VehicleSystemId = 7;
    private static readonly MissionTransferOptions FastRetries = new() { ResponseTimeout = TimeSpan.FromMilliseconds(100), MaxRetries = 3 };

    private readonly SimulatedVehicle _vehicle = new(new SimulatedVehicleOptions { SystemId = VehicleSystemId });
    private readonly Channel<IMavlinkMessage> _inbox = Channel.CreateUnbounded<IMavlinkMessage>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Upload_stores_the_mission_on_the_vehicle_and_download_reads_it_back()
    {
        var items = Plan();
        var transfer = Transfer(_ => true);

        (await transfer.UploadAsync(items, Ct)).IsSuccess.ShouldBeTrue();
        _vehicle.Mission.ShouldBe(items);

        var downloaded = await transfer.DownloadAsync(Ct);
        downloaded.Value.ShouldBe(items.Select(i => i with { TargetSystem = 255, TargetComponent = MavComponent.MissionPlanner }));
    }

    [Fact]
    public async Task Lost_messages_are_recovered_by_resending()
    {
        // Drop the first MISSION_COUNT and the first copy of item 2, as a lossy radio would.
        var dropped = new HashSet<string>();
        var transfer = Transfer(message =>
        {
            var key = message switch
            {
                MissionCountMessage => "count",
                MissionItemIntMessage { Seq: 2 } => "item2",
                _ => null,
            };
            return key is null || !dropped.Add(key);
        });

        var result = await transfer.UploadAsync(Plan(), Ct);

        result.IsSuccess.ShouldBeTrue();
        _vehicle.Mission.Count.ShouldBe(Plan().Count);
    }

    [Fact]
    public async Task A_refusal_from_the_vehicle_is_reported_with_its_reason()
    {
        var transfer = new MissionTransfer(
            (message, _) =>
            {
                if (message is MissionCountMessage)
                {
                    _inbox.Writer.TryWrite(new MissionAckMessage(255, 190, MavMissionResult.NoSpace));
                }

                return ValueTask.CompletedTask;
            },
            _inbox.Reader, FastRetries, TimeProvider.System, VehicleSystemId, 1);

        var result = await transfer.UploadAsync(Plan(), Ct);

        result.Error!.Code.ShouldBe("vehicle.mission.rejected");
        result.Error.Message.ShouldContain("NoSpace");
    }

    [Fact]
    public async Task A_silent_vehicle_fails_the_transfer_after_bounded_retries()
    {
        var sent = 0;
        var transfer = new MissionTransfer(
            (_, _) =>
            {
                sent++;
                return ValueTask.CompletedTask;
            },
            _inbox.Reader, FastRetries, TimeProvider.System, VehicleSystemId, 1);

        var result = await transfer.UploadAsync(Plan(), Ct);

        result.Error.ShouldBe(MissionTransfer.NoResponse);
        sent.ShouldBe(1 + FastRetries.MaxRetries);
    }

    private MissionTransfer Transfer(Func<IMavlinkMessage, bool> linkDelivers) => new(
        (message, _) =>
        {
            if (linkDelivers(message) && _vehicle.Handle(message) is { } reply)
            {
                _inbox.Writer.TryWrite(reply);
            }

            return ValueTask.CompletedTask;
        },
        _inbox.Reader, FastRetries, TimeProvider.System, VehicleSystemId, 1);

    private static IReadOnlyList<MissionItemIntMessage> Plan() => MissionItemMapper.ToMavlink(
    [
        MissionItem.Create(MissionCommand.Takeoff, altitude: 30).Value,
        MissionItem.Create(MissionCommand.Waypoint, 39.9255, 32.8662, 50).Value,
        MissionItem.Create(MissionCommand.Waypoint, 39.9265, 32.8672, 50, speed: 8).Value,
        MissionItem.Create(MissionCommand.Land).Value,
    ], AutopilotType.Px4, VehicleSystemId, 1);
}
