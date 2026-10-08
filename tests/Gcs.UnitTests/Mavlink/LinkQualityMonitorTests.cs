using Gcs.Domain.Vehicles.Connections;
using Gcs.Mavlink.Connections;
using Gcs.Mavlink.Protocol;
using Gcs.Mavlink.Protocol.Messages;
using Microsoft.Extensions.Time.Testing;

namespace Gcs.UnitTests.Mavlink;

public sealed class LinkQualityMonitorTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly MavlinkLinkStatistics _totals = new();
    private readonly LinkQualityMonitor _monitor;
    private long _received;
    private long _lost;

    public LinkQualityMonitorTests()
    {
        _monitor = new LinkQualityMonitor(_time);
    }

    [Fact]
    public void A_clean_link_is_good_with_its_message_rate()
    {
        for (var second = 0; second < 10; second++)
        {
            Receive(frames: 40, lost: 0);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var quality = _monitor.Snapshot(_totals, ConnectionState.Connected);

        quality.RecentPacketLossRatio.ShouldBe(0);
        quality.MessagesPerSecond.ShouldBe(40, tolerance: 4);
        quality.Grade.ShouldBe(LinkQualityGrade.Good);
    }

    [Fact]
    public void Recent_loss_reacts_within_seconds_while_old_seconds_leave_the_window()
    {
        // A long clean flight, then two bad seconds: 30 of every 50 frames lost.
        for (var second = 0; second < 60; second++)
        {
            Receive(frames: 50, lost: 0);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Receive(frames: 20, lost: 30);
        _time.Advance(TimeSpan.FromSeconds(1));
        Receive(frames: 20, lost: 30);

        var bad = _monitor.Snapshot(_totals, ConnectionState.Connected);
        bad.RecentPacketLossRatio.ShouldBe(60 / 500.0, tolerance: 0.001); // 8 clean seconds + 2 bad ones in the window
        bad.Grade.ShouldBe(LinkQualityGrade.Fair);

        // Ten clean seconds later the bad ones have left the window.
        for (var second = 0; second < 10; second++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            Receive(frames: 50, lost: 0);
        }

        _monitor.Snapshot(_totals, ConnectionState.Connected).RecentPacketLossRatio.ShouldBe(0);
    }

    [Fact]
    public void Heavy_loss_is_poor()
    {
        Receive(frames: 30, lost: 20);

        _monitor.Snapshot(_totals, ConnectionState.Connected).Grade.ShouldBe(LinkQualityGrade.Poor);
    }

    [Fact]
    public void Silence_makes_the_link_lost_before_the_heartbeat_watchdog_does()
    {
        Receive(frames: 40, lost: 0);
        _time.Advance(TimeSpan.FromSeconds(4));

        _monitor.Snapshot(_totals, ConnectionState.Connected).Grade.ShouldBe(LinkQualityGrade.Lost);
    }

    [Fact]
    public void Round_trip_comes_from_the_echoed_timestamp_and_is_smoothed()
    {
        Receive(frames: 10, lost: 0);

        Answer(after: TimeSpan.FromMilliseconds(100));
        _monitor.Snapshot(_totals, ConnectionState.Connected).RoundTripMilliseconds!.Value.ShouldBe(100, tolerance: 0.01);

        // One slow answer moves the average by 30 % of the difference, not all the way.
        Answer(after: TimeSpan.FromMilliseconds(500));
        _monitor.Snapshot(_totals, ConnectionState.Connected).RoundTripMilliseconds!.Value.ShouldBe(220, tolerance: 0.01);
    }

    [Fact]
    public void A_slow_round_trip_makes_an_otherwise_clean_link_poor()
    {
        Receive(frames: 10, lost: 0);
        Answer(after: TimeSpan.FromMilliseconds(1200));

        _monitor.Snapshot(_totals, ConnectionState.Connected).Grade.ShouldBe(LinkQualityGrade.Poor);
    }

    [Fact]
    public void Timesync_requests_and_implausible_echoes_are_ignored()
    {
        _time.Advance(TimeSpan.FromSeconds(20));
        _monitor.RecordTimesync(new TimesyncMessage(0, 12345));                                   // the vehicle asking us
        _monitor.RecordTimesync(new TimesyncMessage(99, _monitor.NowNanoseconds() + 1_000_000)); // from the future
        _monitor.RecordTimesync(new TimesyncMessage(99, 1));                                      // 20 s old: not ours

        _monitor.Snapshot(_totals, ConnectionState.Connected).RoundTripMilliseconds.ShouldBeNull();
    }

    [Fact]
    public void The_last_radio_report_is_kept_with_its_time()
    {
        _monitor.RecordRadio(new RadioStatusMessage(182, 176, 97, 41, 44, 12, 3));

        var radio = _monitor.Snapshot(_totals, ConnectionState.Connected).Radio!;

        radio.Rssi.ShouldBe(182);
        radio.RemoteRssi.ShouldBe(176);
        radio.Noise.ShouldBe(41);
        radio.TxBufferPercent.ShouldBe(97);
        radio.ReceiveErrors.ShouldBe(12);
        radio.ReceivedAt.ShouldBe(_time.GetUtcNow());
    }

    private void Receive(int frames, int lost)
    {
        _received += frames;
        _lost += lost;
        _monitor.RecordTotals(_received, _lost, frameArrived: frames > 0);
    }

    private void Answer(TimeSpan after)
    {
        var sentAt = _monitor.NowNanoseconds();
        _time.Advance(after);
        _monitor.RecordTimesync(new TimesyncMessage(777, sentAt));
    }
}
