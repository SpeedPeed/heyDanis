using System.Runtime.CompilerServices;
using Clicky.Api;
using Clicky.Capture;
using Xunit;

namespace Clicky.Tests;

/// <summary>
/// Verifies that CompanionManager's ILlmClient dependency injection seam works
/// end-to-end with a fake implementation.
/// </summary>
public class FakeLlmClientTests
{
    [Fact]
    public async Task FakeLlmClient_YieldsCannedDeltas()
    {
        // Arrange
        var fake = new FakeLlmClient("Hello", " from", " fake!");
        var screens = Array.Empty<CapturedScreen>();
        var history = Array.Empty<Message>();

        // Act
        var deltas = new List<string>();
        await foreach (var delta in fake.SendAsync(history, screens, "system", "hi"))
        {
            deltas.Add(delta);
        }

        // Assert
        Assert.Equal(3, deltas.Count);
        Assert.Equal("Hello from fake!", string.Concat(deltas));
    }

    [Fact]
    public async Task FakeLlmClient_ReceivedInputs()
    {
        // Arrange
        var fake = new FakeLlmClient("ok");
        var history = new[] { new Message("prev user", "prev assistant") };
        var screens = Array.Empty<CapturedScreen>();

        // Act
        await foreach (var _ in fake.SendAsync(history, screens, "my system prompt", "hello world"))
        {
            // consume
        }

        // Assert — verify the fake captured what was passed in
        Assert.Equal("my system prompt", fake.LastSystemPrompt);
        Assert.Equal("hello world", fake.LastUserText);
        Assert.Single(fake.LastHistory!);
        Assert.Equal("prev user", fake.LastHistory![0].UserText);
    }

    /// <summary>
    /// A trivial ILlmClient implementation that yields canned deltas.
    /// Proves the DI seam on CompanionManager works with any implementation.
    /// </summary>
    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly string[] _deltas;

        public string? LastSystemPrompt { get; private set; }
        public string? LastUserText { get; private set; }
        public IReadOnlyList<Message>? LastHistory { get; private set; }

        public FakeLlmClient(params string[] deltas)
        {
            _deltas = deltas;
        }

        public async IAsyncEnumerable<string> SendAsync(
            IReadOnlyList<Message> history,
            IReadOnlyList<CapturedScreen> screens,
            string systemPrompt,
            string userText,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastHistory = history;
            LastSystemPrompt = systemPrompt;
            LastUserText = userText;

            foreach (var delta in _deltas)
            {
                ct.ThrowIfCancellationRequested();
                yield return delta;
                await Task.Yield(); // simulate async behavior
            }
        }
    }
}
