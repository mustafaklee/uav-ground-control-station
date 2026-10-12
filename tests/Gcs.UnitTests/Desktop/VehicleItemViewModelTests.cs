using Gcs.Contracts.Vehicles;
using Gcs.Desktop.ViewModels;

namespace Gcs.UnitTests.Desktop;

public sealed class VehicleItemViewModelTests
{
    [Fact]
    public void A_connected_link_shows_grade_round_trip_loss_rate_and_radio()
    {
        var row = new VehicleItemViewModel(DesktopTestData.Vehicle("UAV-01"));
        var quality = new LinkQualityDto(
            1000, 4, 0.004, 0, RecentPacketLossRatio: 0.0123, MessagesPerSecond: 41.2, RoundTripMilliseconds: 12.4,
            LastFrameAt: DateTimeOffset.UnixEpoch, Grade: "Good",
            Radio: new RadioStatusDto(182, 176, 40, 42, 0, 0, 100, DateTimeOffset.UnixEpoch));

        row.Apply(new VehicleLinkResponse(row.Id, "Connected", null, 0, null, quality));

        row.LinkGrade.ShouldBe("Good");
        row.LinkSummary.ShouldBe("Good · 12 ms · loss 1.2 % · 41 msg/s · RSSI 182/176");
    }

    [Fact]
    public void Without_a_round_trip_or_radio_those_parts_are_left_out()
    {
        VehicleItemViewModel.Summarize(new LinkQualityDto(10, 0, 0, 0, MessagesPerSecond: 9, Grade: "Fair"))
            .ShouldBe("Fair · loss 0 % · 9 msg/s");
    }

    [Fact]
    public void A_link_that_is_not_connected_shows_no_quality_line()
    {
        var row = new VehicleItemViewModel(DesktopTestData.Vehicle("UAV-01"));
        row.Apply(new VehicleLinkResponse(row.Id, "Connected", null, 0, null, new LinkQualityDto(1, 0, 0, 0, Grade: "Good")));

        row.Apply(new VehicleLinkResponse(row.Id, "Reconnecting", null, 1, null, new LinkQualityDto(1, 0, 0, 0)));

        row.LinkSummary.ShouldBeEmpty();
        row.LinkGrade.ShouldBe("Lost");
    }

    [Fact]
    public void A_connected_link_without_a_counted_frame_waits_for_data_instead_of_reporting_lost()
    {
        var row = new VehicleItemViewModel(DesktopTestData.Vehicle("UAV-01"));

        row.Apply(new VehicleLinkResponse(row.Id, "Connected", null, 0, null, new LinkQualityDto(0, 0, 0, 0)));

        row.LinkSummary.ShouldBe(VehicleItemViewModel.WaitingForData);
    }
}
