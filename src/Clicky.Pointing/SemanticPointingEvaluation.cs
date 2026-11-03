using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clicky.Pointing;

public enum SemanticPointingExpectedOutcome
{
    PointRequired,
    PointOrNone,
    NoneRequired,
}

public sealed record SemanticPointingFixtureSet
{
    public required IReadOnlyList<SemanticPointingFixture> Fixtures { get; init; }

    public static SemanticPointingFixtureSet Load(string catalogPath)
    {
        var json = File.ReadAllText(catalogPath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        return JsonSerializer.Deserialize<SemanticPointingFixtureSet>(json, options)
            ?? throw new InvalidOperationException($"Could not load pointing fixture catalog: {catalogPath}");
    }
}

public sealed record SemanticPointingFixture
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string TargetText { get; init; }
    public required string ImagePath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required SemanticPointingExpectedOutcome ExpectedOutcome { get; init; }
    public required IReadOnlyList<SemanticPointingRegion> AllowedRegions { get; init; }
    public required IReadOnlyList<SemanticPointingRegion> DistractorRegions { get; init; }
}

public sealed record SemanticPointingRegion
{
    public required string Name { get; init; }
    public required IReadOnlyList<SemanticPoint> Polygon { get; init; }

    public bool Contains(Point point)
    {
        if (Polygon.Count < 3)
            return false;

        var inside = false;
        for (int i = 0, j = Polygon.Count - 1; i < Polygon.Count; j = i++)
        {
            var pi = Polygon[i];
            var pj = Polygon[j];
            var crossesY = pi.Y > point.Y != pj.Y > point.Y;
            if (!crossesY)
                continue;

            var xAtY = (double)(pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X;
            if (point.X < xAtY)
                inside = !inside;
        }

        return inside || IsOnBoundary(point);
    }

    private bool IsOnBoundary(Point point)
    {
        for (int i = 0, j = Polygon.Count - 1; i < Polygon.Count; j = i++)
        {
            if (PointOnSegment(point, Polygon[j], Polygon[i]))
                return true;
        }

        return false;
    }

    private static bool PointOnSegment(Point point, SemanticPoint a, SemanticPoint b)
    {
        var cross = (point.Y - a.Y) * (b.X - a.X) - (point.X - a.X) * (b.Y - a.Y);
        if (cross != 0)
            return false;

        return point.X >= Math.Min(a.X, b.X)
            && point.X <= Math.Max(a.X, b.X)
            && point.Y >= Math.Min(a.Y, b.Y)
            && point.Y <= Math.Max(a.Y, b.Y);
    }
}

public sealed record SemanticPoint
{
    public required int X { get; init; }
    public required int Y { get; init; }
}

public sealed record SemanticPointingEvaluationResult
{
    public required bool Passed { get; init; }
    public required string Reason { get; init; }
    public string? MatchedAllowedRegion { get; init; }
    public string? MatchedDistractorRegion { get; init; }
    public PointDirective? Directive { get; init; }
}

public static class SemanticPointingEvaluator
{
    public static SemanticPointingEvaluationResult EvaluateResponse(
        SemanticPointingFixture fixture,
        string modelResponse)
    {
        var parseResult = PointTagParser.Parse(modelResponse);
        if (parseResult.Directive is null)
            return EvaluateNoPoint(fixture);

        var directive = parseResult.Directive;
        var point = new Point(directive.X, directive.Y);

        if (point.X < 0 || point.X >= fixture.Width || point.Y < 0 || point.Y >= fixture.Height)
        {
            return Fail($"point {point.X},{point.Y} is outside the fixture image bounds", directive: directive);
        }

        var distractor = fixture.DistractorRegions.FirstOrDefault(region => region.Contains(point));
        if (distractor is not null)
        {
            return Fail(
                $"point {point.X},{point.Y} landed in distractor region '{distractor.Name}'",
                matchedDistractorRegion: distractor.Name,
                directive: directive);
        }

        if (fixture.ExpectedOutcome == SemanticPointingExpectedOutcome.NoneRequired)
            return Fail("fixture requires no point, but the response returned a point", directive: directive);

        var allowed = fixture.AllowedRegions.FirstOrDefault(region => region.Contains(point));
        if (allowed is not null)
        {
            return new SemanticPointingEvaluationResult
            {
                Passed = true,
                Reason = $"point landed in allowed region '{allowed.Name}'",
                MatchedAllowedRegion = allowed.Name,
                Directive = directive,
            };
        }

        return Fail($"point {point.X},{point.Y} did not land in any allowed region", directive: directive);
    }

    private static SemanticPointingEvaluationResult EvaluateNoPoint(SemanticPointingFixture fixture)
    {
        if (fixture.ExpectedOutcome is SemanticPointingExpectedOutcome.PointOrNone or SemanticPointingExpectedOutcome.NoneRequired)
        {
            return new SemanticPointingEvaluationResult
            {
                Passed = true,
                Reason = "response returned no point, which this fixture allows",
            };
        }

        return Fail("fixture requires a point, but the response returned no point");
    }

    private static SemanticPointingEvaluationResult Fail(
        string reason,
        string? matchedDistractorRegion = null,
        PointDirective? directive = null)
    {
        return new SemanticPointingEvaluationResult
        {
            Passed = false,
            Reason = reason,
            MatchedDistractorRegion = matchedDistractorRegion,
            Directive = directive,
        };
    }
}
