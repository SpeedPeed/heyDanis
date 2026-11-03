using System.Drawing;
using System.Drawing.Imaging;
using Clicky.Capture;
using Clicky.Companion;
using Clicky.Pointing;
using Xunit;

namespace Clicky.Tests;

/// <summary>
/// End-to-end fixture tests for the Windows pointing pipeline.
/// These tests build a deterministic synthetic screenshot containing labeled
/// boxes, feed a fake Claude-style [POINT:x,y:label] response through the real
/// parser and coordinate converter, and assert the final mapped point lands
/// inside the expected on-screen box.
/// </summary>
public class PointingEndToEndTests
{
    [Fact]
    public void BoxA_SingleMonitor100Percent_MapsIntoExpectedBox()
    {
        var fixture = SyntheticPointingFixture.Create(
            displayBounds: new Rectangle(0, 0, 1920, 1080),
            screenshotSize: new Size(1280, 720),
            isCursorScreen: true,
            boxSpecs:
            [
                new BoxSpec("box a", new Rectangle(160, 140, 220, 120)),
                new BoxSpec("box b", new Rectangle(520, 260, 240, 140)),
                new BoxSpec("box c", new Rectangle(900, 420, 180, 120)),
            ]);

        AssertPointLandsInsideExpectedBox(
            fixture,
            responseText: "box a is right here. [POINT:270,200:box a]",
            expectedBoxLabel: "box a",
            dpiScaleX: 1.0,
            dpiScaleY: 1.0);
    }

    [Fact]
    public void BoxA_HighDpi150Percent_MapsIntoExpectedBox()
    {
        var fixture = SyntheticPointingFixture.Create(
            displayBounds: new Rectangle(0, 0, 2560, 1440),
            screenshotSize: new Size(1280, 720),
            isCursorScreen: true,
            boxSpecs:
            [
                new BoxSpec("box a", new Rectangle(300, 180, 200, 120)),
                new BoxSpec("box b", new Rectangle(700, 320, 220, 140)),
            ]);

        AssertPointLandsInsideExpectedBox(
            fixture,
            responseText: "that is box a. [POINT:400,240:box a]",
            expectedBoxLabel: "box a",
            dpiScaleX: 1.5,
            dpiScaleY: 1.5);
    }

    [Fact]
    public void BoxA_SecondaryMonitorNegativeOrigin_MapsIntoExpectedBox()
    {
        var leftMonitorBounds = new Rectangle(-2560, 0, 2560, 1440);

        var fixture = SyntheticPointingFixture.Create(
            displayBounds: leftMonitorBounds,
            screenshotSize: new Size(1280, 720),
            isCursorScreen: true,
            boxSpecs:
            [
                new BoxSpec("box a", new Rectangle(140, 220, 220, 120)),
                new BoxSpec("box b", new Rectangle(840, 360, 260, 120)),
            ]);

        AssertPointLandsInsideExpectedBox(
            fixture,
            responseText: "i found box a. [POINT:250,280:box a]",
            expectedBoxLabel: "box a",
            dpiScaleX: 1.25,
            dpiScaleY: 1.25);
    }

    [Fact]
    public void BoxA_ExplicitScreen2_UsesSecondMonitor()
    {
        var primary = SyntheticPointingFixture.Create(
            displayBounds: new Rectangle(0, 0, 1920, 1080),
            screenshotSize: new Size(1280, 720),
            isCursorScreen: false,
            boxSpecs:
            [
                new BoxSpec("wrong screen", new Rectangle(180, 180, 240, 140)),
            ]);
        var secondary = SyntheticPointingFixture.Create(
            displayBounds: new Rectangle(1920, 0, 2560, 1440),
            screenshotSize: new Size(1280, 720),
            isCursorScreen: true,
            boxSpecs:
            [
                new BoxSpec("box a", new Rectangle(680, 260, 260, 160)),
            ]);

        var screens = new List<CapturedScreen> { primary.Screen, secondary.Screen };
        var expectedDisplayBox = secondary.ExpectedDisplayBoxes["box a"];
        var parseResult = PointTagParser.Parse("box a is on screen two. [POINT:810,340:box a:screen2]");

        Assert.NotNull(parseResult.Directive);

        var converted = PointTagParser.ConvertToScreenCoordinatesDetailed(parseResult.Directive!, screens);

        Assert.NotNull(converted);
        Assert.Equal(secondary.Screen.DisplayBounds, converted!.DisplayBounds);
        Assert.True(expectedDisplayBox.Contains(ToDrawingPoint(converted.ScreenPoint)),
            $"Expected mapped point {converted.ScreenPoint} to land inside screen 2 box {expectedDisplayBox}.");
        AssertOverlayLocalDipMatchesExpectedBox(
            converted,
            expectedDisplayBox,
            dpiScaleX: 1.5,
            dpiScaleY: 1.5);
    }

    [Fact]
    public void GermanyMapPrompt_OverridesBadModelPointToWebMercatorGermany()
    {
        var screen = new CapturedScreen
        {
            ImageBytes = [],
            Label = "screen 1 of 3 - cursor is on this screen",
            IsCursorScreen = true,
            DisplayBounds = new Rectangle(0, 0, 1920, 1080),
            ScreenshotPixelWidth = 1920,
            ScreenshotPixelHeight = 1080,
        };
        var canadaPointFromModel = new PointDirective
        {
            X = 530,
            Y = 194,
            Label = "Germany",
            ScreenNumber = 1,
        };

        var corrected = CompanionManager.ApplyGeoMapOverrideIfAvailable(
            "point to Germany on the map",
            canadaPointFromModel,
            [screen]);

        Assert.Equal("Germany", corrected.Label);
        Assert.Equal(1015, corrected.X);
        Assert.Equal(361, corrected.Y);
        Assert.Equal(1, corrected.ScreenNumber);
    }

    [Fact]
    public void BoxA_RoundTripProducesPointDebugArtifactFriendlyImage()
    {
        var fixture = SyntheticPointingFixture.Create(
            displayBounds: new Rectangle(0, 0, 1920, 1080),
            screenshotSize: new Size(1280, 720),
            isCursorScreen: true,
            boxSpecs:
            [
                new BoxSpec("box a", new Rectangle(100, 100, 320, 180)),
            ]);

        using var image = Image.FromStream(new MemoryStream(fixture.Screen.ImageBytes));

        Assert.Equal(fixture.Screen.ScreenshotPixelWidth, image.Width);
        Assert.Equal(fixture.Screen.ScreenshotPixelHeight, image.Height);

        AssertPointLandsInsideExpectedBox(
            fixture,
            responseText: "there's box a. [POINT:260,190:box a]",
            expectedBoxLabel: "box a",
            dpiScaleX: 1.0,
            dpiScaleY: 1.0);
    }

    private static void AssertPointLandsInsideExpectedBox(
        SyntheticPointingFixture fixture,
        string responseText,
        string expectedBoxLabel,
        double dpiScaleX,
        double dpiScaleY)
    {
        var parseResult = PointTagParser.Parse(responseText);

        Assert.NotNull(parseResult.Directive);

        var converted = PointTagParser.ConvertToScreenCoordinatesDetailed(
            parseResult.Directive!,
            [fixture.Screen]);

        Assert.NotNull(converted);

        var expectedDisplayBox = fixture.ExpectedDisplayBoxes[expectedBoxLabel];
        var mappedPoint = ToDrawingPoint(converted!.ScreenPoint);

        Assert.True(
            expectedDisplayBox.Contains(mappedPoint),
            $"Expected mapped point {converted.ScreenPoint} to land inside box '{expectedBoxLabel}' with bounds {expectedDisplayBox}.");

        AssertOverlayLocalDipMatchesExpectedBox(converted, expectedDisplayBox, dpiScaleX, dpiScaleY);
    }

    private static void AssertOverlayLocalDipMatchesExpectedBox(
        PointConversionResult converted,
        Rectangle expectedDisplayBox,
        double dpiScaleX,
        double dpiScaleY)
    {
        var overlayLocalDipX = (converted.ScreenPoint.X - converted.DisplayBounds.X) / dpiScaleX;
        var overlayLocalDipY = (converted.ScreenPoint.Y - converted.DisplayBounds.Y) / dpiScaleY;

        var expectedLocalDisplayX = converted.ScreenPoint.X - converted.DisplayBounds.X;
        var expectedLocalDisplayY = converted.ScreenPoint.Y - converted.DisplayBounds.Y;

        Assert.InRange(overlayLocalDipX, 0, converted.DisplayBounds.Width / dpiScaleX);
        Assert.InRange(overlayLocalDipY, 0, converted.DisplayBounds.Height / dpiScaleY);
        Assert.Equal(expectedLocalDisplayX / dpiScaleX, overlayLocalDipX, precision: 6);
        Assert.Equal(expectedLocalDisplayY / dpiScaleY, overlayLocalDipY, precision: 6);

        var boxLocalLeftDip = (expectedDisplayBox.Left - converted.DisplayBounds.Left) / dpiScaleX;
        var boxLocalTopDip = (expectedDisplayBox.Top - converted.DisplayBounds.Top) / dpiScaleY;
        var boxLocalRightDip = (expectedDisplayBox.Right - converted.DisplayBounds.Left) / dpiScaleX;
        var boxLocalBottomDip = (expectedDisplayBox.Bottom - converted.DisplayBounds.Top) / dpiScaleY;

        Assert.InRange(overlayLocalDipX, boxLocalLeftDip, boxLocalRightDip);
        Assert.InRange(overlayLocalDipY, boxLocalTopDip, boxLocalBottomDip);
    }

    private static System.Drawing.Point ToDrawingPoint(PointCoordinates point)
    {
        return new System.Drawing.Point(
            (int)Math.Round(point.X),
            (int)Math.Round(point.Y));
    }

    private sealed record BoxSpec(string Label, Rectangle ScreenshotBounds);

    private sealed class SyntheticPointingFixture
    {
        public required CapturedScreen Screen { get; init; }
        public required Dictionary<string, Rectangle> ExpectedDisplayBoxes { get; init; }

        public static SyntheticPointingFixture Create(
            Rectangle displayBounds,
            Size screenshotSize,
            bool isCursorScreen,
            IReadOnlyList<BoxSpec> boxSpecs)
        {
            var bytes = CreateScreenshotBytes(screenshotSize, boxSpecs);
            var expectedDisplayBoxes = boxSpecs.ToDictionary(
                box => box.Label,
                box => ScaleRectangle(box.ScreenshotBounds, screenshotSize, displayBounds));

            return new SyntheticPointingFixture
            {
                Screen = new CapturedScreen
                {
                    ImageBytes = bytes,
                    Label = isCursorScreen ? "user's screen (cursor is here)" : "screen 2 of 2 - secondary screen",
                    IsCursorScreen = isCursorScreen,
                    DisplayBounds = displayBounds,
                    ScreenshotPixelWidth = screenshotSize.Width,
                    ScreenshotPixelHeight = screenshotSize.Height,
                },
                ExpectedDisplayBoxes = expectedDisplayBoxes,
            };
        }

        private static byte[] CreateScreenshotBytes(Size screenshotSize, IReadOnlyList<BoxSpec> boxSpecs)
        {
            using var bitmap = new Bitmap(screenshotSize.Width, screenshotSize.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(24, 28, 38));
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var titleFont = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
            using var boxFont = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var subtitleBrush = new SolidBrush(Color.FromArgb(220, 220, 225));

            graphics.DrawString("synthetic clicky fixture", titleFont, textBrush, 40, 28);
            graphics.DrawString("used by PointingEndToEndTests", boxFont, subtitleBrush, 40, 58);

            var palette = new[]
            {
                Color.FromArgb(0x42, 0x85, 0xF4),
                Color.FromArgb(0x34, 0xA8, 0x53),
                Color.FromArgb(0xFB, 0xBC, 0x05),
                Color.FromArgb(0xEA, 0x43, 0x35),
            };

            for (int i = 0; i < boxSpecs.Count; i++)
            {
                var box = boxSpecs[i];
                var fillColor = palette[i % palette.Length];
                using var fill = new SolidBrush(fillColor);
                using var stroke = new Pen(Color.White, 3f);
                graphics.FillRectangle(fill, box.ScreenshotBounds);
                graphics.DrawRectangle(stroke, box.ScreenshotBounds);
                graphics.DrawString(box.Label.ToUpperInvariant(), boxFont, textBrush,
                    box.ScreenshotBounds.X + 14,
                    box.ScreenshotBounds.Y + 14);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Jpeg);
            return stream.ToArray();
        }

        private static Rectangle ScaleRectangle(Rectangle screenshotRect, Size screenshotSize, Rectangle displayBounds)
        {
            double scaleX = (double)displayBounds.Width / screenshotSize.Width;
            double scaleY = (double)displayBounds.Height / screenshotSize.Height;

            var left = displayBounds.Left + (int)Math.Floor(screenshotRect.Left * scaleX);
            var top = displayBounds.Top + (int)Math.Floor(screenshotRect.Top * scaleY);
            var right = displayBounds.Left + (int)Math.Ceiling(screenshotRect.Right * scaleX);
            var bottom = displayBounds.Top + (int)Math.Ceiling(screenshotRect.Bottom * scaleY);

            return Rectangle.FromLTRB(left, top, right, bottom);
        }
    }
}
