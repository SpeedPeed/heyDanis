using System.Collections.Concurrent;
using Clicky.Api;
using Clicky.Companion;
using Xunit;

namespace Clicky.Tests;

public class TtsPipelineTests
{
    [Fact]
    public async Task Pipeline_PreservesOrderUnderVaryingLatency()
    {
        // Mock TTS client that records the order sentences are spoken and adds
        // variable delays to simulate network jitter.
        var spoken = new ConcurrentQueue<string>();
        var delays = new Dictionary<string, int>
        {
            ["First sentence."] = 50,
            ["Second sentence."] = 10,  // faster than first
            ["Third sentence."] = 30,
        };

        using var client = new FakeStreamingTtsClient(spoken, delays);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pipeline = new TtsPipeline(client, cts.Token);

        pipeline.Enqueue("First sentence.");
        pipeline.Enqueue("Second sentence.");
        pipeline.Enqueue("Third sentence.");
        pipeline.Complete();

        await pipeline.WaitForCompletionAsync();

        var result = spoken.ToArray();
        Assert.Equal(3, result.Length);
        Assert.Equal("First sentence.", result[0]);
        Assert.Equal("Second sentence.", result[1]);
        Assert.Equal("Third sentence.", result[2]);
    }

    [Fact]
    public async Task Pipeline_CancellationStopsProcessing()
    {
        var spoken = new ConcurrentQueue<string>();
        using var client = new FakeStreamingTtsClient(spoken, new Dictionary<string, int>
        {
            ["A"] = 500,
            ["B"] = 500,
        });
        using var cts = new CancellationTokenSource();
        var pipeline = new TtsPipeline(client, cts.Token);

        pipeline.Enqueue("A");
        pipeline.Enqueue("B");
        pipeline.Complete();

        // Cancel quickly — should not process all sentences
        await Task.Delay(50);
        cts.Cancel();

        await pipeline.WaitForCompletionAsync();

        // At most one sentence should have started (the pipeline is sequential)
        Assert.True(spoken.Count <= 1);
    }

    [Fact]
    public async Task Pipeline_EmptyQueue_CompletesImmediately()
    {
        var spoken = new ConcurrentQueue<string>();
        using var client = new FakeStreamingTtsClient(spoken, new Dictionary<string, int>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pipeline = new TtsPipeline(client, cts.Token);

        pipeline.Complete();
        await pipeline.WaitForCompletionAsync();

        Assert.Empty(spoken);
    }

    /// <summary>
    /// Fake TTS client that records spoken text in order and applies per-sentence delays.
    /// Implements just enough of ElevenLabsTtsClient for the pipeline to consume.
    /// </summary>
    private sealed class FakeStreamingTtsClient : ElevenLabsTtsClient
    {
        private readonly ConcurrentQueue<string> _spoken;
        private readonly Dictionary<string, int> _delays;

        public FakeStreamingTtsClient(ConcurrentQueue<string> spoken, Dictionary<string, int> delays)
            : base("fake-key", "fake-voice")
        {
            _spoken = spoken;
            _delays = delays;
        }

        public override async Task SpeakAsync(string text, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (_delays.TryGetValue(text, out var delay))
                await Task.Delay(delay, ct);

            _spoken.Enqueue(text);
        }
    }
}
