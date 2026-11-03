using System.Drawing;
using Clicky.Capture;
using Clicky.Pointing;
using Xunit;

namespace Clicky.Tests;

public sealed class StructuredPointingTurnParserTests
{
    [Fact]
    public void Parse_ValidPointJson_ReturnsPointIntent()
    {
        var result = StructuredPointingTurnParser.Parse("""
            {"spokenText":"that field is here.","pointIntent":{"kind":"point","x":402,"y":390,"screen":1,"label":"api key","confidence":"high"}}
            """);

        Assert.Equal("that field is here.", result.SpokenText);
        Assert.Equal(PointIntentKind.Point, result.PointIntent.Kind);
        Assert.Equal(402, result.PointIntent.X);
        Assert.Equal(390, result.PointIntent.Y);
        Assert.Equal(1, result.PointIntent.ScreenNumber);
        Assert.Equal("api key", result.PointIntent.Label);
        Assert.Single(result.PointIntents);
    }

    [Fact]
    public void Parse_ValidPointIntentsJson_ReturnsOrderedPointIntents()
    {
        var result = StructuredPointingTurnParser.Parse("""
            {"spokenText":"first here, then there.","pointIntents":[{"kind":"point","x":120,"y":80,"screen":1,"label":"first setting","confidence":"high"},{"kind":"point","x":320,"y":210,"screen":1,"label":"second setting","confidence":"high"}]}
            """);

        Assert.Equal("first here, then there.", result.SpokenText);
        Assert.Equal(2, result.PointIntents.Count);
        Assert.Equal("first setting", result.PointIntents[0].Label);
        Assert.Equal("second setting", result.PointIntents[1].Label);
        Assert.Equal("first setting", result.PointIntent.Label);
    }

    [Fact]
    public void Parse_GamerDrawingJson_ReturnsCircleAndArrowIntents()
    {
        var result = StructuredPointingTurnParser.Parse("""
            {"spokenText":"circle that minion, then push upward.","pointIntents":[{"kind":"circle","x":220,"y":140,"radius":32,"screen":1,"label":"minion","confidence":"high"},{"kind":"arrow","x1":230,"y1":180,"x2":310,"y2":90,"screen":1,"label":"push","confidence":"high"}]}
            """);

        Assert.Equal(PointIntentKind.Circle, result.PointIntents[0].Kind);
        Assert.Equal(32, result.PointIntents[0].Radius);
        Assert.Equal(PointIntentKind.Arrow, result.PointIntents[1].Kind);
        Assert.Equal(230, result.PointIntents[1].X1);
        Assert.Equal(90, result.PointIntents[1].Y2);
    }

    [Fact]
    public void ToDrawingDirectives_ConvertsCircleAndArrow()
    {
        var result = StructuredPointingTurnParser.Parse("""
            {"spokenText":"draw these.","pointIntents":[{"kind":"circle","x":220,"y":140,"radius":32,"screen":1,"label":"minion","confidence":"high"},{"kind":"arrow","x1":230,"y1":180,"x2":310,"y2":90,"screen":1,"label":"push","confidence":"high"}]}
            """);

        var directives = StructuredPointingTurnParser.ToDrawingDirectives(result.PointIntents, [Screen(400, 240)]);

        Assert.Equal(2, directives.Count);
        Assert.Equal(DrawingDirectiveKind.Circle, directives[0].Kind);
        Assert.Equal(32, directives[0].Radius);
        Assert.Equal(DrawingDirectiveKind.Arrow, directives[1].Kind);
        Assert.Equal(310, directives[1].X2);
    }

    [Fact]
    public void ToDirectives_DropsInvalidSequenceTargets()
    {
        var result = StructuredPointingTurnParser.Parse("""
            {"spokenText":"these are the settings.","pointIntents":[{"kind":"point","x":120,"y":80,"screen":1,"label":"valid","confidence":"high"},{"kind":"point","x":999,"y":80,"screen":1,"label":"outside","confidence":"high"},{"kind":"point","x":180,"y":100,"screen":1,"label":"low confidence","confidence":"low"}]}
            """);

        var directives = StructuredPointingTurnParser.ToDirectives(result.PointIntents, [Screen(400, 240)]);

        var directive = Assert.Single(directives);
        Assert.Equal("valid", directive.Label);
    }

    [Fact]
    public void Parse_LowConfidencePoint_ReturnsNone()
    {
        var result = StructuredPointingTurnParser.Parse("""
            {"spokenText":"i'm not fully sure.","pointIntent":{"kind":"point","x":402,"y":390,"label":"egypt","confidence":"low"}}
            """);

        Assert.Equal(PointIntentKind.None, result.PointIntent.Kind);
        Assert.Equal("unsafe_low_confidence", result.PointIntent.NoPointReason);
    }

    [Fact]
    public void ToDirective_RejectsPointOutsideScreenshotBounds()
    {
        var result = StructuredPointingTurnParser.Parse("""
            {"spokenText":"outside.","pointIntent":{"kind":"point","x":901,"y":100,"screen":1,"label":"target","confidence":"high"}}
            """);

        var directive = StructuredPointingTurnParser.ToDirective(result.PointIntent, new[] { Screen(900, 560) });

        Assert.Null(directive);
    }

    [Fact]
    public void Parse_InvalidJson_FallsBackToSpeakableTextAndNoPoint()
    {
        var result = StructuredPointingTurnParser.Parse("this is not json. [POINT:10,20:old tag]");

        Assert.Equal("this is not json.", result.SpokenText);
        Assert.Equal(PointIntentKind.None, result.PointIntent.Kind);
        Assert.Equal("invalid_schema", result.PointIntent.NoPointReason);
    }

    private static CapturedScreen Screen(int width, int height) => new()
    {
        ImageBytes = Array.Empty<byte>(),
        Label = "screen",
        IsCursorScreen = true,
        DisplayBounds = new Rectangle(0, 0, width, height),
        ScreenshotPixelWidth = width,
        ScreenshotPixelHeight = height,
    };
}
