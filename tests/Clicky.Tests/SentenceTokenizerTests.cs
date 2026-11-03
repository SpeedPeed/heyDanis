using Clicky.Companion;
using Xunit;

namespace Clicky.Tests;

public class SentenceTokenizerTests
{
    [Fact]
    public void Feed_SingleCompleteSentence_ReturnsSentence()
    {
        var tokenizer = new SentenceTokenizer();
        var result = tokenizer.Feed("Hello world. ");
        Assert.Single(result);
        Assert.Equal("Hello world.", result[0]);
    }

    [Fact]
    public void Feed_TwoSentences_ReturnsBoth()
    {
        var tokenizer = new SentenceTokenizer();
        var result = tokenizer.Feed("First sentence. Second sentence. ");
        Assert.Equal(2, result.Count);
        Assert.Equal("First sentence.", result[0]);
        Assert.Equal("Second sentence.", result[1]);
    }

    [Fact]
    public void Feed_IncrementalDeltas_BuffersUntilBoundary()
    {
        var tokenizer = new SentenceTokenizer();

        var r1 = tokenizer.Feed("Hello ");
        Assert.Empty(r1);

        var r2 = tokenizer.Feed("world");
        Assert.Empty(r2);

        var r3 = tokenizer.Feed(". Next");
        Assert.Single(r3);
        Assert.Equal("Hello world.", r3[0]);
    }

    [Fact]
    public void Feed_ExclamationMark_SplitsSentence()
    {
        var tokenizer = new SentenceTokenizer();
        var result = tokenizer.Feed("Wow! That's great. ");
        Assert.Equal(2, result.Count);
        Assert.Equal("Wow!", result[0]);
        Assert.Equal("That's great.", result[1]);
    }

    [Fact]
    public void Feed_QuestionMark_SplitsSentence()
    {
        var tokenizer = new SentenceTokenizer();
        var result = tokenizer.Feed("Really? Yes. ");
        Assert.Equal(2, result.Count);
        Assert.Equal("Really?", result[0]);
        Assert.Equal("Yes.", result[1]);
    }

    [Fact]
    public void Feed_DoubleNewline_SplitsSentence()
    {
        var tokenizer = new SentenceTokenizer();
        var result = tokenizer.Feed("First paragraph\n\nSecond paragraph");
        Assert.Single(result);
        Assert.Equal("First paragraph", result[0]);

        var remainder = tokenizer.Flush();
        Assert.Equal("Second paragraph", remainder);
    }

    [Fact]
    public void Feed_PointTag_StrippedByCaller_TokenizerSplitsNormally()
    {
        // The tokenizer doesn't strip POINT tags itself — CompanionManager does that.
        // But the tokenizer should still split around them correctly.
        var tokenizer = new SentenceTokenizer();
        var result = tokenizer.Feed("Check the button. [POINT:300,150:save] ");
        Assert.Single(result);
        Assert.Equal("Check the button.", result[0]);
    }

    [Fact]
    public void Feed_Abbreviation_Dr_FalsePositiveAccepted()
    {
        // Per PRD: accept false positives on abbreviations like "Dr."
        var tokenizer = new SentenceTokenizer();
        var result = tokenizer.Feed("Dr. Smith is here. ");
        // May split at "Dr." — that's acceptable per PRD notes
        Assert.True(result.Count >= 1, "Should produce at least one sentence");
    }

    [Fact]
    public void Feed_Abbreviation_Eg_FalsePositiveAccepted()
    {
        var tokenizer = new SentenceTokenizer();
        var result = tokenizer.Feed("Use it for example. ");
        Assert.True(result.Count >= 1);
    }

    [Fact]
    public void Flush_ReturnsRemainingText()
    {
        var tokenizer = new SentenceTokenizer();
        tokenizer.Feed("Partial text without terminator");
        var remainder = tokenizer.Flush();
        Assert.Equal("Partial text without terminator", remainder);
    }

    [Fact]
    public void Flush_EmptyBuffer_ReturnsNull()
    {
        var tokenizer = new SentenceTokenizer();
        Assert.Null(tokenizer.Flush());
    }

    [Fact]
    public void Flush_WhitespaceOnly_ReturnsNull()
    {
        var tokenizer = new SentenceTokenizer();
        tokenizer.Feed("   ");
        Assert.Null(tokenizer.Flush());
    }

    [Fact]
    public void Feed_PunctuationAtEndWithoutSpace_DoesNotSplit()
    {
        // A period at the very end without trailing whitespace should not split
        // (we don't know if more text is coming)
        var tokenizer = new SentenceTokenizer();
        var result = tokenizer.Feed("Hello world.");
        Assert.Empty(result);

        // But Flush should return it
        var remainder = tokenizer.Flush();
        Assert.Equal("Hello world.", remainder);
    }

    [Fact]
    public void Feed_MultipleDeltas_PreservesOrder()
    {
        var tokenizer = new SentenceTokenizer();
        var all = new List<string>();

        all.AddRange(tokenizer.Feed("First. "));
        all.AddRange(tokenizer.Feed("Second. "));
        all.AddRange(tokenizer.Feed("Third. "));

        Assert.Equal(3, all.Count);
        Assert.Equal("First.", all[0]);
        Assert.Equal("Second.", all[1]);
        Assert.Equal("Third.", all[2]);
    }

    [Fact]
    public void Feed_NewlineInMiddle_DoesNotSplit()
    {
        // Single newline should NOT cause a split
        var tokenizer = new SentenceTokenizer();
        var result = tokenizer.Feed("Line one\nline two");
        Assert.Empty(result);

        var remainder = tokenizer.Flush();
        Assert.Equal("Line one\nline two", remainder);
    }

    // --- Eager first-chunk tests (US-031) ---

    [Fact]
    public void Feed_EagerSplit_CommaAfter60Chars()
    {
        var tokenizer = new SentenceTokenizer();
        // 65 chars before comma: "you'll want to open the color inspector in the top toolbar area"
        var text = "you'll want to open the color inspector in the top toolbar area, then click the eyedropper";
        var result = tokenizer.Feed(text);
        Assert.Single(result);
        Assert.Equal("you'll want to open the color inspector in the top toolbar area,", result[0]);

        var remainder = tokenizer.Flush();
        Assert.Equal("then click the eyedropper", remainder);
    }

    [Fact]
    public void Feed_EagerSplit_EmDash()
    {
        var tokenizer = new SentenceTokenizer();
        // em-dash after 60+ chars
        var text = "you'll want to open the color inspector in the top right area \u2014 it's right up in the toolbar.";
        var result = tokenizer.Feed(text);
        // Should split at em-dash eagerly (first chunk) then at period
        Assert.True(result.Count >= 1);
        Assert.Contains("\u2014", result[0]);
    }

    [Fact]
    public void Feed_EagerSplit_NoSplitUnder60Chars()
    {
        var tokenizer = new SentenceTokenizer();
        // Short text with comma — should NOT eager-split
        var result = tokenizer.Feed("yeah, sure thing");
        Assert.Empty(result);

        var remainder = tokenizer.Flush();
        Assert.Equal("yeah, sure thing", remainder);
    }

    [Fact]
    public void Feed_NormalBoundaryBeforeEagerThreshold()
    {
        var tokenizer = new SentenceTokenizer();
        // Normal period boundary at < 60 chars fires first, sets _firstChunkEmitted
        var result = tokenizer.Feed("Short sentence. Then a longer continuation with commas, and more text after that.");
        Assert.True(result.Count >= 1);
        Assert.Equal("Short sentence.", result[0]);
    }

    [Fact]
    public void Feed_SecondSentence_UsesNormalBoundariesOnly()
    {
        var tokenizer = new SentenceTokenizer();
        // First: emit via normal boundary
        var r1 = tokenizer.Feed("First sentence is complete. ");
        Assert.Single(r1);
        Assert.Equal("First sentence is complete.", r1[0]);

        // Second: long text with comma — should NOT eager-split (first chunk already emitted)
        var r2 = tokenizer.Feed("Now the second sentence has a comma after sixty characters of accumulated text, but it should not split here");
        Assert.Empty(r2);

        var remainder = tokenizer.Flush();
        Assert.NotNull(remainder);
        Assert.Contains("comma", remainder);
    }

    [Fact]
    public void Feed_PointTagNotCountedInEagerThreshold()
    {
        var tokenizer = new SentenceTokenizer();
        // Text is ~40 visible chars + a long POINT tag. Without stripping the tag,
        // total length > 60. With stripping, visible < 60 — should NOT eager-split.
        var text = "click [POINT:1234,5678:save button:1] the save button there, and then proceed";
        var result = tokenizer.Feed(text);
        Assert.Empty(result);
    }
}
