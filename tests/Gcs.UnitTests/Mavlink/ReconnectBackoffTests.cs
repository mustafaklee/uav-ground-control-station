using Gcs.Mavlink.Connections;

namespace Gcs.UnitTests.Mavlink;

public sealed class ReconnectBackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(50, 30)]
    public void Delay_doubles_per_attempt_and_is_capped(int attempt, double expectedSeconds)
    {
        ReconnectBackoff.Delay(attempt, Base, Max, jitterRatio: 0, new Random(1)).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Jitter_stays_within_the_configured_ratio()
    {
        var random = new Random(42);

        var delays = Enumerable.Range(0, 1000).Select(_ => ReconnectBackoff.Delay(2, Base, Max, jitterRatio: 0.2, random).TotalSeconds).ToList();

        delays.Min().ShouldBeGreaterThanOrEqualTo(3.2);
        delays.Max().ShouldBeLessThanOrEqualTo(4.8);
        delays.Distinct().Count().ShouldBeGreaterThan(100); // actually spread out, not constant
    }
}
