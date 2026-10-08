using Gcs.Domain.Vehicles.Connections;

namespace Gcs.UnitTests.Domain.Vehicles;

public sealed class LinkQualityRulesTests
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(200);

    [Theory]
    [InlineData(0.0, 20.0, LinkQualityGrade.Good)]
    [InlineData(0.029, 299.0, LinkQualityGrade.Good)]
    [InlineData(0.03, 20.0, LinkQualityGrade.Fair)]
    [InlineData(0.0, 300.0, LinkQualityGrade.Fair)]
    [InlineData(0.149, 999.0, LinkQualityGrade.Fair)]
    [InlineData(0.15, 20.0, LinkQualityGrade.Poor)]
    [InlineData(0.0, 1000.0, LinkQualityGrade.Poor)]
    public void A_connected_link_is_graded_by_recent_loss_and_round_trip(double loss, double roundTrip, LinkQualityGrade expected) =>
        LinkQualityRules.Grade(ConnectionState.Connected, Fresh, loss, roundTrip).ShouldBe(expected);

    [Fact]
    public void Without_a_round_trip_measurement_loss_alone_decides() =>
        LinkQualityRules.Grade(ConnectionState.Connected, Fresh, 0.01, null).ShouldBe(LinkQualityGrade.Good);

    [Theory]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Reconnecting)]
    [InlineData(ConnectionState.Faulted)]
    [InlineData(ConnectionState.Disconnected)]
    public void A_link_that_is_not_connected_is_lost(ConnectionState state) =>
        LinkQualityRules.Grade(state, Fresh, 0, 10).ShouldBe(LinkQualityGrade.Lost);

    [Fact]
    public void Silence_longer_than_the_limit_is_lost_even_before_the_watchdog_reacts()
    {
        LinkQualityRules.Grade(ConnectionState.Connected, TimeSpan.FromSeconds(3.1), 0, 10).ShouldBe(LinkQualityGrade.Lost);
        LinkQualityRules.Grade(ConnectionState.Connected, null, 0, 10).ShouldBe(LinkQualityGrade.Lost);
    }
}
