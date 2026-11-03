using HeyDanis.Api;
using HeyDanis.Companion;
using Xunit;

namespace HeyDanis.Tests;

public class UsageCostTrackerTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), "HeyDanisUsage_" + Guid.NewGuid().ToString("N"), "usage.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_tempFile)!, recursive: true); } catch { }
    }

    [Fact]
    public void HaikuPricing_IsCheap()
    {
        // 5,000 input + 200 output tokens on Haiku 5.5 = 0.0005 + 0.0001 = 0.0006 $
        var cost = ClaudePricing.EstimateCostUsd(new LlmUsage("claude-haiku-5-5", 5_000, 200, 0, 0));

        Assert.Equal(0.0006m, cost);
    }

    [Fact]
    public void UnknownModel_UsesSonnetPricing()
    {
        var unknown = ClaudePricing.EstimateCostUsd(new LlmUsage("claude-future-9", 1_000_000, 0, 0, 0));

        Assert.Equal(2.00m, unknown);
    }

    [Fact]
    public void Record_AccumulatesAndPersists()
    {
        var now = new DateTime(2026, 11, 3);
        var tracker = new UsageCostTracker(_tempFile, () => now);
        tracker.Record(new LlmUsage("claude-sonnet-5-5", 1_000_000, 0, 0, 0));
        tracker.Record(new LlmUsage("claude-sonnet-5-5", 0, 100_000, 0, 0));

        var reloaded = new UsageCostTracker(_tempFile, () => now);

        Assert.Equal(3.00m, reloaded.CurrentMonthCostUsd);
        Assert.Equal(2, reloaded.CurrentMonthRequestCount);
        Assert.Equal("3,00 $ (2 Fragen)", reloaded.FormatCurrentMonth());
    }

    [Fact]
    public void NewMonth_StartsAtZero()
    {
        var now = new DateTime(2026, 11, 30);
        var tracker = new UsageCostTracker(_tempFile, () => now);
        tracker.Record(new LlmUsage("claude-haiku-5-5", 10_000, 1_000, 0, 0));

        now = new DateTime(2026, 12, 1);

        Assert.Equal(0m, tracker.CurrentMonthCostUsd);
        Assert.Equal("0,00 $ (0 Fragen)", tracker.FormatCurrentMonth());
    }

    [Fact]
    public void TinyCost_IsShownAsLessThanOneCent()
    {
        var tracker = new UsageCostTracker(_tempFile, () => new DateTime(2026, 11, 3));
        tracker.Record(new LlmUsage("claude-haiku-5-5", 5_000, 200, 0, 0));

        Assert.Equal("< 0,01 $ (1 Frage)", tracker.FormatCurrentMonth());
    }

    [Fact]
    public void UsageAccumulator_ReadsInputAndOutputTokensFromStream()
    {
        var accumulator = new AnthropicDirectClient.UsageAccumulator();
        accumulator.Observe("""{"type":"message_start","message":{"usage":{"input_tokens":4321,"output_tokens":1,"cache_read_input_tokens":0}}}""");
        accumulator.Observe("""{"type":"content_block_delta","delta":{"type":"text_delta","text":"hi"}}""");
        accumulator.Observe("""{"type":"message_delta","usage":{"output_tokens":87}}""");

        var usage = accumulator.ToUsage("claude-haiku-5-5");

        Assert.True(accumulator.HasData);
        Assert.Equal(4321, usage.InputTokens);
        Assert.Equal(87, usage.OutputTokens);
    }

    [Fact]
    public void RequestBody_DisablesThinkingForHaikuOnly()
    {
        var haiku = AnthropicDirectClient.BuildRequestBody("claude-haiku-5-5", "sys", Array.Empty<object>());
        var sonnet = AnthropicDirectClient.BuildRequestBody("claude-sonnet-5-5", "sys", Array.Empty<object>());

        Assert.True(haiku.ContainsKey("thinking"));
        Assert.False(sonnet.ContainsKey("thinking"));
        Assert.True(sonnet.ContainsKey("output_config"));
    }
}
