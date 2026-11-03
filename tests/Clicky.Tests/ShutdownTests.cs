using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Clicky.Api;
using Clicky.Capture;
using Clicky.Companion;
using Clicky.Hotkey;
using Xunit;

namespace Clicky.Tests;

/// <summary>
/// Tests for US-026: Quit reliably terminates the process.
/// Verifies that CompanionManager.DisposeAsync completes within its timeout
/// even when background tasks are blocked or long-running.
/// </summary>
public class ShutdownTests
{
    [Fact]
    public async Task DisposeAsync_CompletesWithinTimeout_WhenNoTasksRunning()
    {
        var vm = new CompanionViewModel();
        var (manager, _) = CreateManager(vm, new FakeClient("hello"));

        // DisposeAsync should complete nearly instantly with no background tasks.
        var disposeTask = manager.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(3000));

        Assert.Same(disposeTask, completed);
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithinTimeout_WhenHookConsumerRunning()
    {
        var vm = new CompanionViewModel();
        var (manager, _) = CreateManager(vm, new FakeClient("hello"));
        manager.Start(); // starts the hook consumer task

        // DisposeAsync should cancel the hook consumer and return within 2s.
        var disposeTask = manager.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(3000));

        Assert.Same(disposeTask, completed);
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithinTimeout_WhenBlockingLlmClientRunning()
    {
        var vm = new CompanionViewModel();
        var blockingClient = new BlockingLlmClient();
        var (manager, _) = CreateManager(vm, blockingClient);
        manager.Start();

        // Trigger a response pipeline that will block on the LLM client.
        manager.SendTranscriptToClaudeWithScreenshot("test transcript");

        // Give the response task a moment to start and hit the blocking client.
        await Task.Delay(200);

        // DisposeAsync must still complete within 2s by cancelling the blocked task.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var disposeTask = manager.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(5000));
        sw.Stop();

        Assert.Same(disposeTask, completed);
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"DisposeAsync took {sw.ElapsedMilliseconds}ms, expected < 3000ms");
    }

    [Fact]
    public async Task DisposeAsync_Idempotent_CanCallMultipleTimes()
    {
        var vm = new CompanionViewModel();
        var (manager, _) = CreateManager(vm, new FakeClient("hello"));
        manager.Start();

        await manager.DisposeAsync();
        // Second call should not throw or hang.
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_Synchronous_DoesNotHang()
    {
        var vm = new CompanionViewModel();
        var (manager, _) = CreateManager(vm, new FakeClient("hello"));
        manager.Start();

        // Synchronous Dispose should return quickly (cancels but doesn't await).
        var task = Task.Run(() => manager.Dispose());
        var completed = await Task.WhenAny(task, Task.Delay(3000));

        Assert.Same(task, completed);
    }

    // ───────── Helpers ─────────

    private static (CompanionManager manager, Dispatcher dispatcher) CreateManager(
        CompanionViewModel vm, ILlmClient client)
    {
        Dispatcher? dispatcher = null;
        var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();

        var hook = new GlobalPushToTalkHook(PushToTalkShortcut.ControlAlt);
        var transcriber = new AssemblyAiStreamingTranscriber("fake-key");
        var ttsClient = new ElevenLabsTtsClient("fake-key", "fake-voice");

        var manager = new CompanionManager(
            vm, hook, client, transcriber, ttsClient, dispatcher!);

        return (manager, dispatcher!);
    }

    /// <summary>
    /// An ILlmClient that blocks forever on SendAsync until cancellation.
    /// Simulates a hung HTTP stream that would prevent process exit.
    /// </summary>
    private sealed class BlockingLlmClient : ILlmClient
    {
        public async IAsyncEnumerable<string> SendAsync(
            IReadOnlyList<Message> history,
            IReadOnlyList<CapturedScreen> screens,
            string systemPrompt,
            string userText,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Yield one delta then block forever (simulating a hung HTTP stream).
            yield return "blocking...";
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — shutdown cancelled us.
                yield break;
            }
        }
    }

    private sealed class FakeClient : ILlmClient
    {
        private readonly string[] _deltas;

        public FakeClient(params string[] deltas) => _deltas = deltas;

        public async IAsyncEnumerable<string> SendAsync(
            IReadOnlyList<Message> history,
            IReadOnlyList<CapturedScreen> screens,
            string systemPrompt,
            string userText,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var delta in _deltas)
            {
                ct.ThrowIfCancellationRequested();
                yield return delta;
                await Task.Yield();
            }
        }
    }
}
