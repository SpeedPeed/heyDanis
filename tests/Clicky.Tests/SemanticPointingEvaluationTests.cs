using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clicky.Pointing;
using Xunit;

namespace Clicky.Tests;

public class SemanticPointingEvaluationTests
{
    [Fact]
    public void FixtureCatalog_LoadsAllRequiredCategoriesAndImages()
    {
        var catalog = LoadCatalog();
        var fixtures = catalog.Fixtures;

        Assert.Contains(fixtures, f => f.Id == "world-map-egypt");
        Assert.Contains(fixtures, f => f.Id == "world-map-algeria");
        Assert.Contains(fixtures, f => f.Id == "nearby-country-distractors");
        Assert.Contains(fixtures, f => f.Id == "dense-settings-panel");
        Assert.Contains(fixtures, f => f.Id == "simple-labeled-ui-target");

        foreach (var fixture in fixtures)
        {
            Assert.NotEmpty(fixture.TargetText);
            Assert.NotEmpty(fixture.AllowedRegions);

            var imagePath = FixturePath(fixture.ImagePath);
            Assert.True(File.Exists(imagePath), $"Missing fixture image {imagePath}");

            using var image = Image.FromFile(imagePath);
            Assert.Equal(fixture.Width, image.Width);
            Assert.Equal(fixture.Height, image.Height);
        }
    }

    [Theory]
    [MemberData(nameof(CannedOutputs))]
    public void Evaluator_GradesCannedModelOutputs(CannedOutput canned)
    {
        var fixture = LoadCatalog().Fixtures.Single(f => f.Id == canned.FixtureId);

        var result = SemanticPointingEvaluator.EvaluateResponse(fixture, canned.Response);

        Assert.Equal(canned.ExpectedPass, result.Passed);
        if (!string.IsNullOrWhiteSpace(canned.ExpectedReasonContains))
        {
            Assert.Contains(canned.ExpectedReasonContains, result.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Evaluator_AcceptsPointNoneForAmbiguousMapFixtures()
    {
        var fixture = LoadCatalog().Fixtures.Single(f => f.Id == "world-map-egypt");

        var result = SemanticPointingEvaluator.EvaluateResponse(
            fixture,
            "I cannot point to this precisely enough. [POINT:none]");

        Assert.True(result.Passed);
        Assert.Contains("no point", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluator_RejectsPointNoneWhenFixtureRequiresPoint()
    {
        var fixture = LoadCatalog().Fixtures.Single(f => f.Id == "simple-labeled-ui-target");

        var result = SemanticPointingEvaluator.EvaluateResponse(
            fixture,
            "I cannot find box B. [POINT:none]");

        Assert.False(result.Passed);
        Assert.Contains("requires a point", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluator_FailsWrongButPlausibleNeighboringMapPoint()
    {
        var fixture = LoadCatalog().Fixtures.Single(f => f.Id == "nearby-country-distractors");

        var result = SemanticPointingEvaluator.EvaluateResponse(
            fixture,
            "Egypt is here. [POINT:526,354:Egypt:screen1]");

        Assert.False(result.Passed);
        Assert.Equal("sudan", result.MatchedDistractorRegion);
    }

    [Theory]
    [InlineData("simple-labeled-ui-target", 456, 260, true)]
    [InlineData("dense-settings-panel", 520, 404, true)]
    [InlineData("world-map-egypt", 452, 250, false)]
    [InlineData("world-map-algeria", 310, 160, false)]
    public void StructuredPointingResults_UseSemanticFixtureGate(
        string fixtureId,
        int x,
        int y,
        bool expectedPass)
    {
        var fixture = LoadCatalog().Fixtures.Single(f => f.Id == fixtureId);
        var result = new PointingTurnResult
        {
            SpokenText = "fixture result",
            PointIntent = new PointIntent
            {
                Kind = PointIntentKind.Point,
                X = x,
                Y = y,
                ScreenNumber = 1,
                Label = fixture.TargetText,
            },
        };

        var evaluation = StructuredPointingTurnParser.EvaluateFixture(fixture, result);

        Assert.Equal(expectedPass, evaluation.Passed);
    }

    [Theory]
    [InlineData("world-map-egypt")]
    [InlineData("world-map-algeria")]
    public void StructuredPointingResults_AcceptNoPointForHardMapFixtures(string fixtureId)
    {
        var fixture = LoadCatalog().Fixtures.Single(f => f.Id == fixtureId);
        var result = new PointingTurnResult
        {
            SpokenText = "i can't point to that reliably.",
            PointIntent = PointIntent.None("unsafe_low_confidence"),
        };

        var evaluation = StructuredPointingTurnParser.EvaluateFixture(fixture, result);

        Assert.True(evaluation.Passed);
    }

    private static SemanticPointingFixtureSet LoadCatalog()
    {
        return SemanticPointingFixtureSet.Load(FixturePath("fixtures.json"));
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "pointing", fileName);
    }

    public static IEnumerable<object[]> CannedOutputs()
    {
        var json = File.ReadAllText(FixturePath("canned-model-outputs.json"));
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        var outputs = JsonSerializer.Deserialize<List<CannedOutput>>(json, options)
            ?? throw new InvalidOperationException("Could not load canned semantic pointing outputs.");

        return outputs.Select(output => new object[] { output });
    }

    public sealed record CannedOutput
    {
        public required string FixtureId { get; init; }
        public required string Name { get; init; }
        public required string Response { get; init; }
        public required bool ExpectedPass { get; init; }
        public string? ExpectedReasonContains { get; init; }

        public override string ToString() => $"{FixtureId}: {Name}";
    }
}
