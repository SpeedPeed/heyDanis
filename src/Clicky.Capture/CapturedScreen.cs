using System.Drawing;

namespace Clicky.Capture;

/// <summary>
/// A single monitor's screenshot with metadata, mirroring
/// <c>CompanionScreenCapture</c> in the Mac reference implementation.
/// </summary>
public sealed class CapturedScreen
{
    /// <summary>JPEG-encoded image bytes.</summary>
    public required byte[] ImageBytes { get; init; }

    /// <summary>
    /// Human-readable label describing the screen and cursor location,
    /// e.g. "user's screen (cursor is here)" or "screen 2 of 3 — secondary screen".
    /// </summary>
    public required string Label { get; init; }

    /// <summary>Whether the OS cursor was on this screen at capture time.</summary>
    public required bool IsCursorScreen { get; init; }

    /// <summary>The display bounds in desktop (virtual-screen) pixels.</summary>
    public required Rectangle DisplayBounds { get; init; }

    /// <summary>Width of the (possibly resized) screenshot in pixels.</summary>
    public required int ScreenshotPixelWidth { get; init; }

    /// <summary>Height of the (possibly resized) screenshot in pixels.</summary>
    public required int ScreenshotPixelHeight { get; init; }
}
