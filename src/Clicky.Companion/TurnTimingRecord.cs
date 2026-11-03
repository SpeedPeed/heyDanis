using System.Diagnostics;
using System.Drawing;
using Clicky.Capture;
using Clicky.Pointing;

namespace Clicky.Companion;

internal sealed class TurnTimingRecord
{
    private readonly Func<long> _elapsedMs;
    private readonly List<string> _events = new();

    public TurnTimingRecord(string provider, string model, Func<long> elapsedMs)
    {
        Provider = provider;
        Model = model;
        CorrelationId = Guid.NewGuid().ToString("N")[..8];
        _elapsedMs = elapsedMs;
    }

    public string CorrelationId { get; }
    public string Provider { get; }
    public string Model { get; }
    public IReadOnlyList<string> Events => _events;

    public static TurnTimingRecord Start(string provider, string model)
    {
        var sw = Stopwatch.StartNew();
        return new TurnTimingRecord(provider, model, () => sw.ElapsedMilliseconds);
    }

    public void Mark(string name, string? details = null)
    {
        var line = $"[TURN] id={CorrelationId} t={_elapsedMs()}ms provider={Provider} model={Model} event={name}";
        if (!string.IsNullOrWhiteSpace(details))
            line += $" {details}";
        _events.Add(line);
        DebugLog.Write(line);
    }

    public static string FormatScreenEvidence(
        IReadOnlyList<CapturedScreen> screens,
        bool gridAnnotated,
        bool cursorScreenFiltering)
    {
        var totalBytes = screens.Sum(s => s.ImageBytes.Length);
        var screenParts = screens.Select((s, i) =>
            $"#{i + 1}:{s.ScreenshotPixelWidth}x{s.ScreenshotPixelHeight},bytes={s.ImageBytes.Length},cursor={s.IsCursorScreen},bounds={FormatBounds(s.DisplayBounds)}");

        return $"screens={screens.Count} totalImageBytes={totalBytes} gridAnnotated={gridAnnotated} cursorScreenFiltering={cursorScreenFiltering} images=[{string.Join(";", screenParts)}]";
    }

    public static string FormatPointDirective(PointDirective? directive)
    {
        return directive is null
            ? "point=none"
            : $"point=({directive.X},{directive.Y}) label=\"{directive.Label}\" screen={directive.ScreenNumber?.ToString() ?? "null"}";
    }

    public static string FormatBounds(Rectangle bounds)
    {
        return $"({bounds.X},{bounds.Y},{bounds.Width},{bounds.Height})";
    }
}
