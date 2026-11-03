using System.Drawing;
using System.Text.RegularExpressions;
using Clicky.Capture;

namespace Clicky.Pointing;

/// <summary>
/// Parses [POINT:x,y:label:screenN] tags from Claude's streamed response
/// and converts screenshot-space coordinates to desktop screen coordinates.
/// Mirrors <c>parsePointingCoordinates</c> in Mac's CompanionManager.swift.
/// </summary>
public static partial class PointTagParser
{
    // Matches [POINT:none] and [POINT:x,y:label] and [POINT:x,y:label:screenN]
    // Group 1: X, Group 2: Y, Group 3: label (optional), Group 4: screen number (optional)
    [GeneratedRegex(@"\[POINT:(?:none|(\d+)\s*,\s*(\d+)(?::([^\]:\s][^\]:]*?))?(?::screen(\d+))?)\]\s*$")]
    private static partial Regex PointTagRegex();

    // For stripping all point tags (including mid-text ones, though they should be at the end)
    [GeneratedRegex(@"\[POINT:[^\]]*\]")]
    private static partial Regex AllPointTagsRegex();

    /// <summary>
    /// Parses a Claude response for a [POINT:...] tag at the end of the text.
    /// Returns the spoken text (with tag removed) and an optional <see cref="PointDirective"/>.
    /// </summary>
    public static PointingParseResult Parse(string responseText)
    {
        var match = PointTagRegex().Match(responseText);

        // Strip all point tags from the response for TTS
        var spokenText = AllPointTagsRegex().Replace(responseText, "").Trim();

        if (!match.Success || !match.Groups[1].Success)
        {
            // [POINT:none], malformed tag, or no tag at all
            return new PointingParseResult { SpokenText = spokenText };
        }

        var x = int.Parse(match.Groups[1].Value);
        var y = int.Parse(match.Groups[2].Value);
        var label = match.Groups[3].Success ? match.Groups[3].Value.Trim() : "here";
        int? screenNumber = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : null;

        return new PointingParseResult
        {
            SpokenText = spokenText,
            Directive = new PointDirective
            {
                X = x,
                Y = y,
                Label = label,
                ScreenNumber = screenNumber,
            }
        };
    }

    /// <summary>
    /// Converts a <see cref="PointDirective"/> from screenshot pixel space to
    /// desktop screen coordinates (DIPs) using the captured screen metadata.
    /// Mirrors the coordinate conversion in Mac's CompanionManager.swift (lines 625-682).
    /// </summary>
    /// <param name="directive">The parsed point directive with screenshot-space coordinates.</param>
    /// <param name="screens">The captured screens from the current screenshot pass.</param>
    /// <returns>
    /// A tuple of (screenPoint in desktop coords, displayBounds of the target screen),
    /// or <c>null</c> if the target screen cannot be found.
    /// </returns>
    public static (PointCoordinates ScreenPoint, Rectangle DisplayBounds)? ConvertToScreenCoordinates(
        PointDirective directive,
        IReadOnlyList<CapturedScreen> screens)
    {
        var detailed = ConvertToScreenCoordinatesDetailed(directive, screens);
        return detailed is null
            ? null
            : (detailed.ScreenPoint, detailed.DisplayBounds);
    }

    /// <summary>
    /// Converts a directive and returns all intermediate values for debug logging.
    /// </summary>
    public static PointConversionResult? ConvertToScreenCoordinatesDetailed(
        PointDirective directive,
        IReadOnlyList<CapturedScreen> screens)
    {
        if (screens.Count == 0)
            return null;

        // Select target screen: use screenNumber if specified, otherwise cursor screen
        CapturedScreen? target = null;

        if (directive.ScreenNumber.HasValue)
        {
            var idx = directive.ScreenNumber.Value - 1; // 1-based → 0-based
            if (idx >= 0 && idx < screens.Count)
                target = screens[idx];
        }

        target ??= screens.FirstOrDefault(s => s.IsCursorScreen) ?? screens[0];

        // Clamp coordinates to screenshot bounds
        var clampedX = Math.Max(0, Math.Min(directive.X, target.ScreenshotPixelWidth - 1));
        var clampedY = Math.Max(0, Math.Min(directive.Y, target.ScreenshotPixelHeight - 1));

        // Scale from screenshot pixels to display pixels
        var scaleX = (double)target.DisplayBounds.Width / target.ScreenshotPixelWidth;
        var scaleY = (double)target.DisplayBounds.Height / target.ScreenshotPixelHeight;

        var displayLocalX = clampedX * scaleX;
        var displayLocalY = clampedY * scaleY;

        // Convert to global desktop coordinates by adding the display's origin.
        var globalX = displayLocalX + target.DisplayBounds.X;
        var globalY = displayLocalY + target.DisplayBounds.Y;

        return new PointConversionResult
        {
            TargetScreen = target,
            ClampedX = clampedX,
            ClampedY = clampedY,
            ScaleX = scaleX,
            ScaleY = scaleY,
            DisplayLocalPoint = new PointCoordinates(displayLocalX, displayLocalY),
            ScreenPoint = new PointCoordinates(globalX, globalY),
            DisplayBounds = target.DisplayBounds,
        };
    }
}
