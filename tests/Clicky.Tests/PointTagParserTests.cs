using System.Drawing;
using Clicky.Capture;
using Clicky.Pointing;
using Xunit;

namespace Clicky.Tests;

public class PointTagParserTests
{
    // ── Parsing tests ────────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleTag_ExtractsCoordinatesAndLabel()
    {
        var result = PointTagParser.Parse(
            "click the save button up top. [POINT:1100,42:save button]");

        Assert.Equal("click the save button up top.", result.SpokenText);
        Assert.NotNull(result.Directive);
        Assert.Equal(1100, result.Directive!.X);
        Assert.Equal(42, result.Directive.Y);
        Assert.Equal("save button", result.Directive.Label);
        Assert.Null(result.Directive.ScreenNumber);
    }

    [Fact]
    public void Parse_TagWithScreenNumber_ExtractsScreenNumber()
    {
        var result = PointTagParser.Parse(
            "that's on your other monitor. [POINT:400,300:terminal:screen2]");

        Assert.Equal("that's on your other monitor.", result.SpokenText);
        Assert.NotNull(result.Directive);
        Assert.Equal(400, result.Directive!.X);
        Assert.Equal(300, result.Directive.Y);
        Assert.Equal("terminal", result.Directive.Label);
        Assert.Equal(2, result.Directive.ScreenNumber);
    }

    [Fact]
    public void Parse_PointNone_ReturnsNullDirective()
    {
        var result = PointTagParser.Parse(
            "html is the skeleton of every web page. [POINT:none]");

        Assert.Equal("html is the skeleton of every web page.", result.SpokenText);
        Assert.Null(result.Directive);
    }

    [Fact]
    public void Parse_NoTag_ReturnsTextAsIs()
    {
        var result = PointTagParser.Parse("just a normal response with no tag");

        Assert.Equal("just a normal response with no tag", result.SpokenText);
        Assert.Null(result.Directive);
    }

    [Fact]
    public void Parse_MissingLabel_DefaultsToHere()
    {
        var result = PointTagParser.Parse("look here. [POINT:500,250]");

        Assert.NotNull(result.Directive);
        Assert.Equal(500, result.Directive!.X);
        Assert.Equal(250, result.Directive.Y);
        Assert.Equal("here", result.Directive.Label);
    }

    [Fact]
    public void Parse_MalformedTag_IgnoredAndStripped()
    {
        // Malformed: missing coordinates entirely
        var result = PointTagParser.Parse("check this out [POINT:abc:label]");

        // The all-tags regex still strips it, but no directive parsed
        Assert.Equal("check this out", result.SpokenText);
        Assert.Null(result.Directive);
    }

    [Fact]
    public void Parse_SpacesAroundCoordinates_Handled()
    {
        var result = PointTagParser.Parse("here it is. [POINT:100 , 200:button]");

        Assert.NotNull(result.Directive);
        Assert.Equal(100, result.Directive!.X);
        Assert.Equal(200, result.Directive.Y);
        Assert.Equal("button", result.Directive.Label);
    }

    // ── Coordinate conversion tests ──────────────────────────────────

    [Fact]
    public void ConvertToScreenCoordinates_SingleScreen_ScalesCorrectly()
    {
        // Screenshot is 1280x720, display is 1920x1080 at origin (0,0)
        var screens = new List<CapturedScreen>
        {
            MakeScreen(isCursor: true, displayBounds: new Rectangle(0, 0, 1920, 1080),
                        ssWidth: 1280, ssHeight: 720)
        };

        var directive = new PointDirective { X = 640, Y = 360, Label = "center" };
        var result = PointTagParser.ConvertToScreenCoordinates(directive, screens);

        Assert.NotNull(result);
        // 640 * (1920/1280) = 960, 360 * (1080/720) = 540
        Assert.Equal(960.0, result!.Value.ScreenPoint.X, precision: 1);
        Assert.Equal(540.0, result.Value.ScreenPoint.Y, precision: 1);
    }

    [Fact]
    public void ConvertToScreenCoordinates_MultiScreen_UsesScreenNumber()
    {
        var screens = new List<CapturedScreen>
        {
            MakeScreen(isCursor: true, displayBounds: new Rectangle(0, 0, 1920, 1080),
                        ssWidth: 1280, ssHeight: 720),
            MakeScreen(isCursor: false, displayBounds: new Rectangle(1920, 0, 2560, 1440),
                        ssWidth: 1280, ssHeight: 720),
        };

        // Points at screen2 (1-based → index 1)
        var directive = new PointDirective { X = 640, Y = 360, Label = "app", ScreenNumber = 2 };
        var result = PointTagParser.ConvertToScreenCoordinates(directive, screens);

        Assert.NotNull(result);
        // 640 * (2560/1280) + 1920 = 1280 + 1920 = 3200
        // 360 * (1440/720) + 0 = 720
        Assert.Equal(3200.0, result!.Value.ScreenPoint.X, precision: 1);
        Assert.Equal(720.0, result.Value.ScreenPoint.Y, precision: 1);
        Assert.Equal(new Rectangle(1920, 0, 2560, 1440), result.Value.DisplayBounds);
    }

    [Fact]
    public void ConvertToScreenCoordinates_NoScreenNumber_UsesCursorScreen()
    {
        var screens = new List<CapturedScreen>
        {
            MakeScreen(isCursor: false, displayBounds: new Rectangle(0, 0, 1920, 1080),
                        ssWidth: 1280, ssHeight: 720),
            MakeScreen(isCursor: true, displayBounds: new Rectangle(1920, 0, 1920, 1080),
                        ssWidth: 1280, ssHeight: 720),
        };

        var directive = new PointDirective { X = 0, Y = 0, Label = "corner" };
        var result = PointTagParser.ConvertToScreenCoordinates(directive, screens);

        Assert.NotNull(result);
        // Should use cursor screen (index 1), so origin is (1920, 0)
        Assert.Equal(1920.0, result!.Value.ScreenPoint.X, precision: 1);
        Assert.Equal(0.0, result.Value.ScreenPoint.Y, precision: 1);
    }

    [Fact]
    public void ConvertToScreenCoordinates_ClampsOutOfBounds()
    {
        var screens = new List<CapturedScreen>
        {
            MakeScreen(isCursor: true, displayBounds: new Rectangle(0, 0, 1920, 1080),
                        ssWidth: 1280, ssHeight: 720)
        };

        // Coordinates exceed screenshot dimensions
        var directive = new PointDirective { X = 9999, Y = 9999, Label = "oob" };
        var result = PointTagParser.ConvertToScreenCoordinates(directive, screens);

        Assert.NotNull(result);
        // Clamped to 1279 * (1920/1280) = 1918.5, 719 * (1080/720) = 1078.5
        Assert.Equal(1918.5, result!.Value.ScreenPoint.X, precision: 0);
        Assert.Equal(1078.5, result.Value.ScreenPoint.Y, precision: 0);
    }

    [Fact]
    public void ConvertToScreenCoordinatesDetailed_ReturnsIntermediateValues()
    {
        var screens = new List<CapturedScreen>
        {
            MakeScreen(isCursor: true, displayBounds: new Rectangle(100, 200, 1920, 1080),
                        ssWidth: 1280, ssHeight: 720)
        };

        var directive = new PointDirective { X = 640, Y = 360, Label = "center" };
        var result = PointTagParser.ConvertToScreenCoordinatesDetailed(directive, screens);

        Assert.NotNull(result);
        Assert.Equal(640, result!.ClampedX);
        Assert.Equal(360, result.ClampedY);
        Assert.Equal(1.5, result.ScaleX, precision: 3);
        Assert.Equal(1.5, result.ScaleY, precision: 3);
        Assert.Equal(960.0, result.DisplayLocalPoint.X, precision: 1);
        Assert.Equal(540.0, result.DisplayLocalPoint.Y, precision: 1);
        Assert.Equal(1060.0, result.ScreenPoint.X, precision: 1);
        Assert.Equal(740.0, result.ScreenPoint.Y, precision: 1);
    }

    [Fact]
    public void ConvertToScreenCoordinates_EmptyScreens_ReturnsNull()
    {
        var directive = new PointDirective { X = 100, Y = 100, Label = "test" };
        var result = PointTagParser.ConvertToScreenCoordinates(directive, new List<CapturedScreen>());

        Assert.Null(result);
    }

    [Fact]
    public void ConvertToScreenCoordinates_InvalidScreenNumber_FallsToCursorScreen()
    {
        var screens = new List<CapturedScreen>
        {
            MakeScreen(isCursor: true, displayBounds: new Rectangle(0, 0, 1920, 1080),
                        ssWidth: 1280, ssHeight: 720),
        };

        // screen5 doesn't exist
        var directive = new PointDirective { X = 640, Y = 360, Label = "test", ScreenNumber = 5 };
        var result = PointTagParser.ConvertToScreenCoordinates(directive, screens);

        Assert.NotNull(result);
        // Falls back to cursor screen
        Assert.Equal(960.0, result!.Value.ScreenPoint.X, precision: 1);
        Assert.Equal(540.0, result.Value.ScreenPoint.Y, precision: 1);
    }

    [Fact]
    public void BuildPointDirectiveForScreenPoint_SingleScreen_MapsPhysicalPointToScreenshotPoint()
    {
        var screens = new List<CapturedScreen>
        {
            MakeScreen(isCursor: true, displayBounds: new Rectangle(0, 0, 1920, 1080),
                        ssWidth: 960, ssHeight: 540),
        };

        var directive = Clicky.Companion.CompanionManager.BuildPointDirectiveForScreenPoint(
            new System.Windows.Point(480, 270),
            "box A",
            screens);

        Assert.NotNull(directive);
        Assert.Equal(240, directive!.X);
        Assert.Equal(135, directive.Y);
        Assert.Equal("box A", directive.Label);
        Assert.Equal(1, directive.ScreenNumber);
    }

    [Fact]
    public void BuildPointDirectiveForScreenPoint_NegativeOriginScreen_UsesExplicitScreenNumber()
    {
        var screens = new List<CapturedScreen>
        {
            MakeScreen(isCursor: false, displayBounds: new Rectangle(0, 0, 1920, 1080),
                        ssWidth: 1920, ssHeight: 1080),
            MakeScreen(isCursor: true, displayBounds: new Rectangle(-1280, 0, 1280, 720),
                        ssWidth: 640, ssHeight: 360),
        };

        var directive = Clicky.Companion.CompanionManager.BuildPointDirectiveForScreenPoint(
            new System.Windows.Point(-640, 360),
            "box B",
            screens);

        Assert.NotNull(directive);
        Assert.Equal(320, directive!.X);
        Assert.Equal(180, directive.Y);
        Assert.Equal(2, directive.ScreenNumber);
    }

    [Fact]
    public void BuildPointDirectiveForScreenPoint_OutsideCapturedScreens_ReturnsNull()
    {
        var screens = new List<CapturedScreen>
        {
            MakeScreen(isCursor: true, displayBounds: new Rectangle(0, 0, 1920, 1080),
                        ssWidth: 1920, ssHeight: 1080),
        };

        var directive = Clicky.Companion.CompanionManager.BuildPointDirectiveForScreenPoint(
            new System.Windows.Point(3000, 2000),
            "outside",
            screens);

        Assert.Null(directive);
    }

    // ── StripPointTags backward-compat tests (delegating to PointTagParser) ──

    [Fact]
    public void StripPointTags_ViaCompanionManager_StillWorks()
    {
        var stripped = Clicky.Companion.CompanionManager.StripPointTags(
            "check the menu. [POINT:285,11:source control]");
        Assert.Equal("check the menu.", stripped);
    }

    [Fact]
    public void StripPointTags_PointNone_StillWorks()
    {
        var stripped = Clicky.Companion.CompanionManager.StripPointTags(
            "just explaining things. [POINT:none]");
        Assert.Equal("just explaining things.", stripped);
    }

    // ── Helper ───────────────────────────────────────────────────────

    private static CapturedScreen MakeScreen(
        bool isCursor, Rectangle displayBounds, int ssWidth, int ssHeight)
    {
        return new CapturedScreen
        {
            ImageBytes = new byte[] { 0xFF, 0xD8, 0xFF }, // JPEG magic
            Label = isCursor ? "user's screen (cursor is here)" : "screen 2 of 2 — secondary screen",
            IsCursorScreen = isCursor,
            DisplayBounds = displayBounds,
            ScreenshotPixelWidth = ssWidth,
            ScreenshotPixelHeight = ssHeight,
        };
    }
}
