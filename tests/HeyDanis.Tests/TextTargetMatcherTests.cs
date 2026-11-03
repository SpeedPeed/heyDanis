using System.Drawing;
using HeyDanis.Capture;
using HeyDanis.Pointing;
using Xunit;

namespace HeyDanis.Tests;

public class TextTargetMatcherTests
{
    // Two lines of OCR words, laid out 100 px apart horizontally, 40 px apart vertically.
    private static readonly IReadOnlyList<RecognizedWord> Words = new[]
    {
        new RecognizedWord("Ursache:", new Rectangle(10, 10, 80, 20), 0, 0),
        new RecognizedWord("Ddein", new Rectangle(100, 10, 60, 20), 0, 1),
        new RecognizedWord("Screenshot", new Rectangle(170, 10, 110, 20), 0, 2),
        new RecognizedWord("im", new Rectangle(290, 10, 20, 20), 0, 3),
        new RecognizedWord("Browwser", new Rectangle(10, 50, 90, 20), 1, 0),
        new RecognizedWord("oder", new Rectangle(110, 50, 45, 20), 1, 1),
        new RecognizedWord("Speichern", new Rectangle(10, 90, 90, 20), 2, 0),
        new RecognizedWord("unter", new Rectangle(105, 90, 50, 20), 2, 1),
        new RecognizedWord("Speichern", new Rectangle(800, 600, 90, 20), 3, 0),
    };

    [Fact]
    public void MisspelledWord_IsFoundExactly_EvenIfClaudePointedElsewhere()
    {
        // Claude's rough point was on "oder" — the circle must still land on "Browwser".
        var box = TextTargetMatcher.FindTextTarget("Browwser", 130, 60, Words);

        Assert.Equal(new Rectangle(10, 50, 90, 20), box);
    }

    [Fact]
    public void QuotesAndCaseDontMatter()
    {
        Assert.Equal(new Rectangle(100, 10, 60, 20), TextTargetMatcher.FindTextTarget("„ddein“", 0, 0, Words));
    }

    [Fact]
    public void MultiWordLabels_MatchPhrases()
    {
        var box = TextTargetMatcher.FindTextTarget("Speichern unter", 50, 95, Words);

        Assert.Equal(new Rectangle(10, 90, 145, 20), box);
    }

    [Fact]
    public void DuplicateText_PicksTheOneNearestToClaudesPoint()
    {
        Assert.Equal(new Rectangle(800, 600, 90, 20), TextTargetMatcher.FindTextTarget("Speichern", 780, 590, Words));
        Assert.Equal(new Rectangle(10, 90, 90, 20), TextTargetMatcher.FindTextTarget("Speichern", 40, 120, Words));
    }

    [Fact]
    public void SmallOcrMisread_IsTolerated()
    {
        // OCR read "Screenshot" — Claude wrote "Screenshoot".
        Assert.Equal(new Rectangle(170, 10, 110, 20), TextTargetMatcher.FindTextTarget("Screenshoot", 0, 0, Words));
    }

    [Fact]
    public void ShortWords_RequireExactMatch_AndUnknownTextReturnsNull()
    {
        Assert.Null(TextTargetMatcher.FindTextTarget("in", 290, 10, Words));
        Assert.Null(TextTargetMatcher.FindTextTarget("Zahnrad-Symbol", 0, 0, Words));
    }
}
