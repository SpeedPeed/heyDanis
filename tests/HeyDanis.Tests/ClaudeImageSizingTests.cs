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
    [InlineData("""{"found":true,"x1":100,"y1":50,"x2":180,"y2":80}""", true)]
    [InlineData("""{"found":true,"x1":180,"y1":80,"x2":100,"y2":50}""", true)]
    [InlineData("""{"found":false,"x1":0,"y1":0,"x2":0,"y2":0}""", false)]
    [InlineData("""{"found":true,"x1":100,"y1":50,"x2":5000,"y2":80}""", false)]
    [InlineData("not json", false)]
    public void RefinementBox_IsParsedNormalizedAndBoundsChecked(string json, bool expectedOk)
    {
        var ok = CompanionManager.TryParseRefinementBox(json, 1200, 800, out var box);

        Assert.Equal(expectedOk, ok);
        if (expectedOk)
        {
            Assert.Equal(new System.Drawing.Rectangle(100, 50, 80, 30), box);
        }
    }

    [Fact]
    public void CircleRadius_EnclosesWideWords()
    {
        // A 120 px wide word gets a radius of at least 60 px plus some air.
        Assert.True(CompanionManager.CircleRadiusEnclosing(new System.Drawing.Rectangle(0, 0, 120, 20)) >= 68);
        // Tiny targets still get a visible circle.
        Assert.Equal(14, CompanionManager.CircleRadiusEnclosing(new System.Drawing.Rectangle(0, 0, 4, 4)));
    }
}
