using Gcs.Api.Http;

namespace Gcs.UnitTests.Api;

public sealed class ETagTests
{
    [Fact]
    public void Version_is_formatted_as_a_quoted_strong_etag()
    {
        ETags.Format(3).ShouldBe("\"3\"");
    }

    [Theory]
    [InlineData("\"3\"", 3)]
    [InlineData(" \"12\" ", 12)]
    public void Strong_etag_is_parsed(string header, int expected)
    {
        ETags.TryParse(header, out var version).ShouldBeTrue();
        version.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("W/\"3\"")]
    [InlineData("*")]
    [InlineData("\"3\", \"4\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"abc\"")]
    public void Anything_else_is_rejected(string? header)
    {
        ETags.TryParse(header, out _).ShouldBeFalse();
    }
}
