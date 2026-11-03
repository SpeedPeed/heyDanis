using System.Drawing;
using System.Text.Json;
using Clicky.Capture;

namespace Clicky.Pointing;

public enum PointIntentKind
{
    None,
    Point,
    Circle,
    Arrow,
}

public sealed record PointIntent
{
    public required PointIntentKind Kind { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? X1 { get; init; }
    public int? Y1 { get; init; }
    public int? X2 { get; init; }
    public int? Y2 { get; init; }
    public int? Radius { get; init; }
    public int? ScreenNumber { get; init; }
    public string? Label { get; init; }
    public string? NoPointReason { get; init; }

    public static PointIntent None(string? reason = null) => new()
    {
        Kind = PointIntentKind.None,
        NoPointReason = reason,
    };
}

public sealed record PointingTurnResult
{
    public required string SpokenText { get; init; }
    public required PointIntent PointIntent { get; init; }
    public IReadOnlyList<PointIntent> PointIntents { get; init; } = [];
}

public enum DrawingDirectiveKind
{
    Circle,
    Arrow,
}

public sealed record DrawingDirective
{
    public required DrawingDirectiveKind Kind { get; init; }
    public required int ScreenNumber { get; init; }
    public required string Label { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Radius { get; init; }
    public int X1 { get; init; }
    public int Y1 { get; init; }
    public int X2 { get; init; }
    public int Y2 { get; init; }
}

public static class StructuredPointingTurnParser
{
    public static PointingTurnResult Parse(string modelResponse)
    {
        if (TryParseJsonObject(modelResponse, out var document))
        {
            using (document)
            {
                var root = document.RootElement;
                var spokenText = ReadString(root, "spokenText") ?? string.Empty;
                var intents = ParsePointIntents(root);
                var intent = intents.FirstOrDefault() ?? PointIntent.None("missing_point_intent");
                return new PointingTurnResult
                {
                    SpokenText = spokenText.Trim(),
                    PointIntent = intent,
                    PointIntents = intents,
                };
            }
        }

        return new PointingTurnResult
        {
            SpokenText = PointTagParser.Parse(modelResponse).SpokenText.Trim(),
            PointIntent = PointIntent.None("invalid_schema"),
            PointIntents = [PointIntent.None("invalid_schema")],
        };
    }

    public static PointDirective? ToDirective(PointIntent intent, IReadOnlyList<CapturedScreen> screens)
    {
        if (intent.Kind != PointIntentKind.Point)
            return null;

        if (intent.X is not { } x || intent.Y is not { } y)
            return null;

        if (x < 0 || y < 0)
            return null;

        var targetScreen = SelectScreen(intent.ScreenNumber, screens);
        if (targetScreen is null)
            return null;

        if (x >= targetScreen.ScreenshotPixelWidth || y >= targetScreen.ScreenshotPixelHeight)
            return null;

        return new PointDirective
        {
            X = x,
            Y = y,
            Label = string.IsNullOrWhiteSpace(intent.Label) ? "target" : intent.Label.Trim(),
            ScreenNumber = intent.ScreenNumber,
        };
    }

    public static IReadOnlyList<PointDirective> ToDirectives(
        IReadOnlyList<PointIntent> intents,
        IReadOnlyList<CapturedScreen> screens,
        int maxDirectives = 5)
    {
        var directives = new List<PointDirective>();
        foreach (var intent in intents)
        {
            if (directives.Count >= maxDirectives)
                break;

            var directive = ToDirective(intent, screens);
            if (directive is not null)
                directives.Add(directive);
        }

        return directives;
    }

    public static IReadOnlyList<DrawingDirective> ToDrawingDirectives(
        IReadOnlyList<PointIntent> intents,
        IReadOnlyList<CapturedScreen> screens,
        int maxDirectives = 5)
    {
        var directives = new List<DrawingDirective>();
        foreach (var intent in intents)
        {
            if (directives.Count >= maxDirectives)
                break;

            var directive = ToDrawingDirective(intent, screens);
            if (directive is not null)
                directives.Add(directive);
        }

        return directives;
    }

    public static DrawingDirective? ToDrawingDirective(PointIntent intent, IReadOnlyList<CapturedScreen> screens)
    {
        if (intent.Kind is not (PointIntentKind.Circle or PointIntentKind.Arrow))
            return null;

        var targetScreen = SelectScreen(intent.ScreenNumber, screens);
        if (targetScreen is null)
            return null;

        var screenNumber = intent.ScreenNumber ?? (IndexOfScreen(targetScreen, screens) + 1);
        var label = string.IsNullOrWhiteSpace(intent.Label) ? "target" : intent.Label.Trim();

        if (intent.Kind == PointIntentKind.Circle)
        {
            if (intent.X is not { } x || intent.Y is not { } y)
                return null;

            if (!IsInside(x, y, targetScreen) || intent.Radius is not { } radius || radius <= 0)
                return null;

            return new DrawingDirective
            {
                Kind = DrawingDirectiveKind.Circle,
                ScreenNumber = screenNumber,
                Label = label,
                X = x,
                Y = y,
                Radius = radius,
            };
        }

        if (intent.X1 is not { } x1 || intent.Y1 is not { } y1 ||
            intent.X2 is not { } x2 || intent.Y2 is not { } y2)
        {
            return null;
        }

        if (!IsInside(x1, y1, targetScreen) || !IsInside(x2, y2, targetScreen))
            return null;

        return new DrawingDirective
        {
            Kind = DrawingDirectiveKind.Arrow,
            ScreenNumber = screenNumber,
            Label = label,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
        };
    }

    public static SemanticPointingEvaluationResult EvaluateFixture(
        SemanticPointingFixture fixture,
        PointingTurnResult result)
    {
        var directive = ToDirective(result.PointIntent, new[]
        {
            new CapturedScreen
            {
                ImageBytes = Array.Empty<byte>(),
                Label = "fixture",
                IsCursorScreen = true,
                DisplayBounds = new Rectangle(0, 0, fixture.Width, fixture.Height),
                ScreenshotPixelWidth = fixture.Width,
                ScreenshotPixelHeight = fixture.Height,
            }
        });

        if (directive is null)
            return SemanticPointingEvaluator.EvaluateResponse(fixture, "[POINT:none]");

        return SemanticPointingEvaluator.EvaluateResponse(
            fixture,
            $"[POINT:{directive.X},{directive.Y}:{directive.Label}]");
    }

    private static IReadOnlyList<PointIntent> ParsePointIntents(JsonElement root)
    {
        if (TryReadIntentArray(root, "pointIntents", out var pointIntents) ||
            TryReadIntentArray(root, "pointSequence", out pointIntents))
        {
            return pointIntents;
        }

        return [ParsePointIntent(root)];
    }

    private static bool TryReadIntentArray(
        JsonElement root,
        string propertyName,
        out IReadOnlyList<PointIntent> intents)
    {
        intents = [];
        if (!root.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<PointIntent>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                parsed.Add(ParsePointIntentObject(item));
        }

        intents = parsed.Count == 0 ? [PointIntent.None("empty_point_sequence")] : parsed;
        return true;
    }

    private static PointIntent ParsePointIntent(JsonElement root)
    {
        if (!root.TryGetProperty("pointIntent", out var intentElement) ||
            intentElement.ValueKind != JsonValueKind.Object)
        {
            return PointIntent.None("missing_point_intent");
        }

        return ParsePointIntentObject(intentElement);
    }

    private static PointIntent ParsePointIntentObject(JsonElement intentElement)
    {
        var kind = ReadString(intentElement, "kind");
        if (!string.Equals(kind, "point", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(kind, "circle", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(kind, "arrow", StringComparison.OrdinalIgnoreCase))
        {
            return PointIntent.None(ReadString(intentElement, "reason")
                ?? ReadString(intentElement, "noPointReason")
                ?? "none");
        }

        var confidence = ReadString(intentElement, "confidence");
        if (confidence is not null &&
            !string.Equals(confidence, "high", StringComparison.OrdinalIgnoreCase))
        {
            return PointIntent.None("unsafe_low_confidence");
        }

        if (string.Equals(kind, "point", StringComparison.OrdinalIgnoreCase))
        {
            var x = ReadInt(intentElement, "x");
            var y = ReadInt(intentElement, "y");
            if (x is null || y is null)
                return PointIntent.None("missing_coordinates");

            return new PointIntent
            {
                Kind = PointIntentKind.Point,
                X = x,
                Y = y,
                ScreenNumber = ReadInt(intentElement, "screen") ?? ReadInt(intentElement, "screenNumber"),
                Label = ReadString(intentElement, "label"),
            };
        }

        if (string.Equals(kind, "circle", StringComparison.OrdinalIgnoreCase))
        {
            return new PointIntent
            {
                Kind = PointIntentKind.Circle,
                X = ReadInt(intentElement, "x"),
                Y = ReadInt(intentElement, "y"),
                Radius = ReadInt(intentElement, "radius"),
                ScreenNumber = ReadInt(intentElement, "screen") ?? ReadInt(intentElement, "screenNumber"),
                Label = ReadString(intentElement, "label"),
            };
        }

        return new PointIntent
        {
            Kind = PointIntentKind.Arrow,
            X1 = ReadInt(intentElement, "x1") ?? ReadInt(intentElement, "fromX"),
            Y1 = ReadInt(intentElement, "y1") ?? ReadInt(intentElement, "fromY"),
            X2 = ReadInt(intentElement, "x2") ?? ReadInt(intentElement, "toX"),
            Y2 = ReadInt(intentElement, "y2") ?? ReadInt(intentElement, "toY"),
            ScreenNumber = ReadInt(intentElement, "screen") ?? ReadInt(intentElement, "screenNumber"),
            Label = ReadString(intentElement, "label"),
        };
    }

    private static CapturedScreen? SelectScreen(int? screenNumber, IReadOnlyList<CapturedScreen> screens)
    {
        if (screens.Count == 0)
            return null;

        if (screenNumber is null)
            return screens.FirstOrDefault(s => s.IsCursorScreen) ?? screens[0];

        var index = screenNumber.Value - 1;
        if (index < 0 || index >= screens.Count)
            return null;

        return screens[index];
    }

    private static bool TryParseJsonObject(string text, out JsonDocument document)
    {
        document = null!;
        var trimmed = StripCodeFence(text.Trim());
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            return false;

        var json = trimmed[start..(end + 1)];
        try
        {
            document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsInside(int x, int y, CapturedScreen screen) =>
        x >= 0 &&
        y >= 0 &&
        x < screen.ScreenshotPixelWidth &&
        y < screen.ScreenshotPixelHeight;

    private static int IndexOfScreen(CapturedScreen target, IReadOnlyList<CapturedScreen> screens)
    {
        for (var i = 0; i < screens.Count; i++)
        {
            if (ReferenceEquals(screens[i], target))
                return i;
        }

        return 0;
    }

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        var withoutOpening = text[(firstNewline + 1)..];
        var closing = withoutOpening.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? withoutOpening[..closing].Trim() : withoutOpening.Trim();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), out var stringValue))
        {
            return stringValue;
        }

        return null;
    }
}
