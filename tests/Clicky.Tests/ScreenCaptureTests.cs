using System.Drawing;
using System.IO;
using Clicky.Capture;
using Xunit;

namespace Clicky.Tests;

public class ScreenCaptureTests
{
    [Fact]
    public void BuildLabel_SingleScreen_ReturnsUserScreen()
    {
        var label = ScreenCapture.BuildLabel(totalScreens: 1, displayIndex: 0, isCursorScreen: true);
        Assert.Equal("user's screen (cursor is here)", label);
    }

    [Fact]
    public void BuildLabel_MultiScreen_CursorScreen_ReturnsPrimaryFocus()
    {
        var label = ScreenCapture.BuildLabel(totalScreens: 3, displayIndex: 0, isCursorScreen: true);
        Assert.Equal("screen 1 of 3 — cursor is on this screen (primary focus)", label);
    }

    [Fact]
    public void BuildLabel_MultiScreen_SecondaryScreen_ReturnsSecondary()
    {
        var label = ScreenCapture.BuildLabel(totalScreens: 2, displayIndex: 1, isCursorScreen: false);
        Assert.Equal("screen 2 of 2 — secondary screen", label);
    }

    [Fact]
    public void EnumerateMonitors_ReturnsAtLeastOneMonitor()
    {
        var monitors = ScreenCapture.EnumerateMonitors();
        Assert.NotEmpty(monitors);
    }

    [Fact]
    public async Task CaptureAllScreensAsJpegAsync_ReturnsAtLeastOneScreen()
    {
        var screens = await ScreenCapture.CaptureAllScreensAsJpegAsync();

        Assert.NotEmpty(screens);
        Assert.All(screens, s =>
        {
            Assert.NotEmpty(s.ImageBytes);
            Assert.NotEmpty(s.Label);
            Assert.True(s.ScreenshotPixelWidth > 0);
            Assert.True(s.ScreenshotPixelHeight > 0);
            Assert.True(s.ScreenshotPixelWidth <= 2048 || s.ScreenshotPixelHeight <= 2048);
            Assert.True(s.DisplayBounds.Width > 0);
        });
    }

    [Fact]
    public async Task CaptureAllScreensAsJpegAsync_CursorScreenIsFirst()
    {
        var screens = await ScreenCapture.CaptureAllScreensAsJpegAsync();

        Assert.True(screens[0].IsCursorScreen, "First captured screen should be the cursor screen");
    }

    [Fact]
    public async Task CaptureAllScreensAsJpegAsync_JpegMagicBytes()
    {
        var screens = await ScreenCapture.CaptureAllScreensAsJpegAsync();

        // JPEG files start with FF D8 FF
        foreach (var screen in screens)
        {
            Assert.True(screen.ImageBytes.Length >= 3);
            Assert.Equal(0xFF, screen.ImageBytes[0]);
            Assert.Equal(0xD8, screen.ImageBytes[1]);
            Assert.Equal(0xFF, screen.ImageBytes[2]);
        }
    }

    [Fact]
    public async Task CaptureAllScreensAsJpegAsync_MetadataMatchesActualJpegDimensions()
    {
        var screens = await ScreenCapture.CaptureAllScreensAsJpegAsync();

        foreach (var screen in screens)
        {
            using var ms = new MemoryStream(screen.ImageBytes);
            using var bitmap = new Bitmap(ms);

            Assert.Equal(bitmap.Width, screen.ScreenshotPixelWidth);
            Assert.Equal(bitmap.Height, screen.ScreenshotPixelHeight);
        }
    }
}
