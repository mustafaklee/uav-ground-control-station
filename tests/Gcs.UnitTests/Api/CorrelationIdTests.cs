using Gcs.Api.Middleware;

namespace Gcs.UnitTests.Api;

public sealed class CorrelationIdTests
{
    [Theory]
    [InlineData("abc-123")]
    [InlineData("4f1c2a7e6b0d4e1f9a532c8d7e1b0a9f")]
    [InlineData("op_01.request-7")]
    public void Valid_incoming_id_is_kept(string incoming)
    {
        CorrelationIdMiddleware.ResolveCorrelationId(incoming).ShouldBe(incoming);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("line\nbreak")]
    [InlineData("<script>")]
    public void Missing_or_unsafe_id_is_replaced_with_a_new_one(string? incoming)
    {
        var resolved = CorrelationIdMiddleware.ResolveCorrelationId(incoming);

        resolved.ShouldNotBe(incoming);
        Guid.TryParseExact(resolved, "N", out _).ShouldBeTrue();
    }

    [Fact]
    public void Too_long_id_is_replaced()
    {
        var incoming = new string('a', 65);

        CorrelationIdMiddleware.ResolveCorrelationId(incoming).ShouldNotBe(incoming);
    }
}
