using System.Drawing;
using Clicky.Capture;
using Clicky.Companion;
using Clicky.Pointing;
using Xunit;

namespace Clicky.Tests;

public sealed class TurnTimingRecordTests
{
    [Fact]
    public void Mark_FormatsStableTurnLineWithCorrelationProviderModelAndTime()
    {
        var now = 42L;
        var record = new TurnTimingRecord("anthropic", "claude-sonnet-4-6", () => now);

        record.Mark("llm-request-start", "history=1 screens=1");

        var line = Assert.Single(record.Events);
        Assert.Contains("[TURN] id=", line);
        Assert.Contains("t=42ms", line);
        Assert.Contains("provider=anthropic", line);
        Assert.Contains("model=claude-sonnet-4-6", line);
        Assert.Contains("event=llm-request-start", line);
        Assert.Contains("history=1 screens=1", line);
    }

    [Fact]
    public void FormatScreenEvidence_IncludesImagePayloadAndFilteringState()
    {
        var screens = new[]
        {
            new CapturedScreen
            {
                ImageBytes = new byte[123],
                Label = "user's screen",
                IsCursorScreen = true,
                DisplayBounds = new Rectangle(10, 20, 300, 200),
                ScreenshotPixelWidth = 150,
                ScreenshotPixelHeight = 100,
            },
        };

        var result = TurnTimingRecord.FormatScreenEvidence(screens, gridAnnotated: false, cursorScreenFiltering: true);

        Assert.Contains("screens=1", result);
        Assert.Contains("totalImageBytes=123", result);
        Assert.Contains("gridAnnotated=False", result);
        Assert.Contains("cursorScreenFiltering=True", result);
        Assert.Contains("#1:150x100,bytes=123,cursor=True,bounds=(10,20,300,200)", result);
    }

    [Fact]
    public void FormatPointDirective_SupportsNoneAndPointTags()
    {
        Assert.Equal("point=none", TurnTimingRecord.FormatPointDirective(null));

        var directive = new PointDirective
        {
            X = 12,
            Y = 34,
            Label = "save",
            ScreenNumber = 2,
        };

        Assert.Equal("point=(12,34) label=\"save\" screen=2", TurnTimingRecord.FormatPointDirective(directive));
    }
}
