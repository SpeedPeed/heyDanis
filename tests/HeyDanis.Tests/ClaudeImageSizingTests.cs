using HeyDanis.Capture;
using HeyDanis.Companion;
using Xunit;

namespace HeyDanis.Tests;

public class ClaudeImageSizingTests
{
    // Reference values from platform.claude.com/docs/en/build-with-claude/vision(-coordinates).
    [Theory]
    [InlineData(1920, 1080, 1456, 819)]
    [InlineData(1075, 1520, 924, 1307)]
    [InlineData(1000, 1000, 1000, 1000)]
    // The docs' table says 1269x952 here, but their reference implementation (which we
    // port 1:1 and which is stated to match the live API) yields 1270x952.
    [InlineData(2000, 1500, 1270, 952)]
    public void StandardTier_MatchesDocumentedSizes(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ClaudeImageSizing.ResizedSize(width, height, ClaudeImageSizing.Standard));
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(2560, 1440, 2560, 1440)]
    [InlineData(3840, 2160, 2576, 1449)]
    public void HighResolutionTier_MatchesDocumentedSizes(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ClaudeImageSizing.ResizedSize(width, height, ClaudeImageSizing.HighResolution));
    }

    [Fact]
    public void ResizedImages_NeverExceedTheTokenBudget()
    {
        var (width, height) = ClaudeImageSizing.ResizedSize(3440, 1440, ClaudeImageSizing.HighResolution);
        Assert.True(ClaudeImageSizing.CountImageTokens(width, height) <= ClaudeImageSizing.HighResolution.MaxVisualTokens);
    }

    [Theory]
    [InlineData("""{"found":true,"x":300,"y":200}""", true, 300, 200)]
    [InlineData("""{"found":false,"x":0,"y":0}""", false, 0, 0)]
    [InlineData("""{"found":true,"x":5000,"y":10}""", false, 5000, 10)]
    [InlineData("not json", false, 0, 0)]
    public void RefinementAnswer_IsParsedAndBoundsChecked(string json, bool expectedOk, int expectedX, int expectedY)
    {
        var ok = CompanionManager.TryParseRefinement(json, 1200, 800, out var x, out var y);

        Assert.Equal(expectedOk, ok);
        if (expectedOk)
        {
            Assert.Equal(expectedX, x);
            Assert.Equal(expectedY, y);
        }
    }
}
