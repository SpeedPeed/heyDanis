using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Clicky.Api;
using Clicky.Capture;
using Clicky.Companion;
using Clicky.Hotkey;
using Xunit;

namespace Clicky.Tests;

/// <summary>
/// CI-safe end-to-end coverage for the deterministic Clicky turn.
/// Real desktop compositor checks stay in the tray-driven Desktop Smoke Test path.
/// </summary>
public sealed class FullTurnEndToEndTests
{
    [Fact]
    public async Task FullTurn_StreamsResponse_DispatchesOverlay_StartsTts_AndReturnsIdle()
    {
        var screen = CreateScreen();
        var llm = new FakeLlmClient("look at box a. ", "[POINT:200,120:box a]");
        var tts = new RecordingTtsClient();
        var overlayCalls = new List<OverlayCall>();
        var vm = new CompanionViewModel();
        var states = new List<VoiceState>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CompanionViewModel.VoiceState))
                states.Add(vm.VoiceState);
        };

        var harness = CreateHarness(
            vm,
            llm,
            tts,
            (_, _, _) => Task.FromResult(new List<CapturedScreen> { screen }),
            (point, bounds, label) => overlayCalls.Add(new OverlayCall(point, bounds, label)));

        harness.Manager.SendTranscriptToClaudeWithScreenshot("describe box a");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(VoiceState.Idle, vm.VoiceState);
        Assert.Contains(VoiceState.Processing, states);
        Assert.Contains(VoiceState.Responding, states);
        Assert.Equal("describe box a", llm.UserText);
        Assert.Single(llm.Screens);
        Assert.Equal(1, llm.CallCount);
        Assert.Contains("image dimensions: 400x240 pixels", llm.Screens[0].Label);
        Assert.Contains("clicky", llm.SystemPrompt);
        Assert.Equal("look at box a.", tts.Spoken.Single());

        var overlay = Assert.Single(overlayCalls);
        Assert.Equal("box a", overlay.Label);
        Assert.Equal(new Rectangle(10, 20, 800, 480), overlay.DisplayBounds);
        Assert.InRange(overlay.Point.X, 409, 411);
        Assert.InRange(overlay.Point.Y, 259, 261);
        Assert.Single(harness.Manager.ConversationHistory);
        Assert.Equal("look at box a.", harness.Manager.ConversationHistory[0].AssistantText.Trim());

        harness.Shutdown();
    }

    [Fact]
    public async Task FullTurn_PointingTurn_UsesOneLlmRequest()
    {
        var screen = new CapturedScreen
        {
            ImageBytes = CreateJpeg(1920, 1080),
            Label = "user's screen (cursor is here)",
            IsCursorScreen = true,
            DisplayBounds = new Rectangle(0, 0, 1920, 1080),
            ScreenshotPixelWidth = 1920,
            ScreenshotPixelHeight = 1080,
        };
        var llm = new FakeLlmClient("algeria is in northern africa. [POINT:960,520:algeria]");
        var overlayCalls = new List<OverlayCall>();
        var harness = CreateHarness(
            new CompanionViewModel(),
            llm,
            new RecordingTtsClient(),
            (_, _, _) => Task.FromResult(new List<CapturedScreen> { screen }),
            (point, bounds, label) => overlayCalls.Add(new OverlayCall(point, bounds, label)));

        harness.Manager.SendTranscriptToClaudeWithScreenshot("point to algeria");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, llm.CallCount);
        var overlay = Assert.Single(overlayCalls);
        Assert.Equal("algeria", overlay.Label);
        Assert.Equal(974, overlay.Point.X);
        Assert.Equal(452, overlay.Point.Y);

        harness.Shutdown();
    }

    [Fact]
    public async Task FullTurn_PointingTurn_DoesNotWaitForTtsPlaybackToFinish()
    {
        var screen = new CapturedScreen
        {
            ImageBytes = CreateJpeg(1920, 1080),
            Label = "user's screen (cursor is here)",
            IsCursorScreen = true,
            DisplayBounds = new Rectangle(0, 0, 1920, 1080),
            ScreenshotPixelWidth = 1920,
            ScreenshotPixelHeight = 1080,
        };
        var llm = new FakeLlmClient("algeria is in northern africa. [POINT:960,520:algeria]");
        var tts = new BlockingTtsClient();
        var overlayCalls = new List<OverlayCall>();
        var harness = CreateHarness(
            new CompanionViewModel(),
            llm,
            tts,
            (_, _, _) => Task.FromResult(new List<CapturedScreen> { screen }),
            (point, bounds, label) => overlayCalls.Add(new OverlayCall(point, bounds, label)));

        harness.Manager.SendTranscriptToClaudeWithScreenshot("point to algeria");

        await WaitUntilAsync(() => overlayCalls.Count == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1, llm.CallCount);
        Assert.False(harness.Manager.CurrentResponseTask!.IsCompleted);

        tts.Release();
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Shutdown();
    }

    [Fact]
    public async Task FullTurn_LlmKeyFailure_RequestsSettingsAndReturnsIdle()
    {
        var llm = new FakeLlmClient(new HttpRequestException("Unauthorized (401)"));
        var vm = new CompanionViewModel();
        var settingsRequested = false;
        var harness = CreateHarness(vm, llm, new RecordingTtsClient());
        harness.Manager.OpenSettingsRequested += (_, _) => settingsRequested = true;

        harness.Manager.SendTranscriptToClaudeWithScreenshot("hello");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(settingsRequested);
        Assert.Equal("Your API key is missing or invalid. Open Settings to fix.", vm.LastError);
        Assert.Equal(VoiceState.Idle, vm.VoiceState);

        harness.Shutdown();
    }

    [Fact]
    public async Task FullTurn_MalformedPointTag_DoesNotCrashOrDispatchOverlay()
    {
        var llm = new FakeLlmClient("this is still speakable. [POINT:not-a-point]");
        var tts = new RecordingTtsClient();
        var overlayCalls = new List<OverlayCall>();
        var vm = new CompanionViewModel();
        var harness = CreateHarness(
            vm,
            llm,
            tts,
            overlayFlyTo: (point, bounds, label) => overlayCalls.Add(new OverlayCall(point, bounds, label)));

        harness.Manager.SendTranscriptToClaudeWithScreenshot("what now?");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(overlayCalls);
        Assert.Equal("this is still speakable.", tts.Spoken.Single());
        Assert.Equal(VoiceState.Idle, vm.VoiceState);
        Assert.Null(vm.LastError);

        harness.Shutdown();
    }

    [Fact]
    public async Task FullTurn_SwapDuringLlmStream_CancelsCleanlyAndReturnsIdle()
    {
        var llm = new BlockingLlmClient();
        var tts = new RecordingTtsClient();
        var vm = new CompanionViewModel();
        var harness = CreateHarness(vm, llm, tts);

        harness.Manager.SendTranscriptToClaudeWithScreenshot("keep talking");
        await llm.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.Manager.SwapLlmClientAsync(new FakeLlmClient("new client response."));

        Assert.True(llm.WasCanceled);
        Assert.True(tts.StopPlaybackCalled);
        Assert.Equal(VoiceState.Idle, vm.VoiceState);

        harness.Shutdown();
    }

    [Fact]
    public async Task FullTurn_TtsFailure_SurfacesFriendlyErrorAndReturnsIdle()
    {
        var llm = new FakeLlmClient("this sentence should fail.");
        var tts = new RecordingTtsClient(new InvalidOperationException("speaker failed"));
        var vm = new CompanionViewModel();
        var harness = CreateHarness(vm, llm, tts);

        harness.Manager.SendTranscriptToClaudeWithScreenshot("say something");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Something went wrong - try again, and if it keeps happening, check Settings.", Normalize(vm.LastError));
        Assert.Equal(VoiceState.Idle, vm.VoiceState);

        harness.Shutdown();
    }

    [Fact]
    public async Task FullTurn_PreparesClickyUiBeforeCapture()
    {
        var order = new List<string>();
        var llm = new FakeLlmClient("nothing to point at. [POINT:none]");
        var tts = new RecordingTtsClient();
        var vm = new CompanionViewModel();
        var harness = CreateHarness(
            vm,
            llm,
            tts,
            captureScreensAsync: (_, _, _) =>
            {
                order.Add("capture");
                return Task.FromResult(new List<CapturedScreen> { CreateScreen() });
            },
            prepareForCaptureAsync: () =>
            {
                order.Add("prepare");
                return Task.CompletedTask;
            });

        harness.Manager.SendTranscriptToClaudeWithScreenshot("what's visible");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "prepare", "capture" }, order);

        harness.Shutdown();
    }

    [Fact]
    public async Task StructuredPointing_DispatchesBeforeBlockedTtsCompletes()
    {
        var screen = CreateScreen();
        var llm = new FakeLlmClient("""
            {"spokenText":"box a is here.","pointIntent":{"kind":"point","x":200,"y":120,"screen":1,"label":"box a","confidence":"high"}}
            """);
        var tts = new BlockingTtsClient();
        var overlayCalls = new List<OverlayCall>();
        var harness = CreateHarness(
            new CompanionViewModel(),
            llm,
            tts,
            (_, _, _) => Task.FromResult(new List<CapturedScreen> { screen }),
            (point, bounds, label) => overlayCalls.Add(new OverlayCall(point, bounds, label)),
            useRedesignedPointingProtocol: true);

        harness.Manager.SendTranscriptToClaudeWithScreenshot("show me box a");

        await WaitUntilAsync(() => overlayCalls.Count == 1, TimeSpan.FromSeconds(5));
        Assert.False(harness.Manager.CurrentResponseTask!.IsCompleted);
        Assert.Equal(1, llm.CallCount);
        Assert.Equal(StructuredPromptMarker(), llm.SystemPrompt);

        tts.Release();
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        var overlay = Assert.Single(overlayCalls);
        Assert.Equal("box a", overlay.Label);
        Assert.Equal("box a is here.", harness.Manager.ConversationHistory.Single().AssistantText);

        harness.Shutdown();
    }

    [Fact]
    public async Task StructuredPointing_NoneIntent_DoesNotDispatchOverlay()
    {
        var llm = new FakeLlmClient("""
            {"spokenText":"i can't point to that reliably from this view.","pointIntent":{"kind":"none","reason":"unsafe_low_confidence"}}
            """);
        var tts = new RecordingTtsClient();
        var overlayCalls = new List<OverlayCall>();
        var harness = CreateHarness(
            new CompanionViewModel(),
            llm,
            tts,
            overlayFlyTo: (point, bounds, label) => overlayCalls.Add(new OverlayCall(point, bounds, label)),
            useRedesignedPointingProtocol: true);

        harness.Manager.SendTranscriptToClaudeWithScreenshot("show me egypt");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(overlayCalls);
        Assert.Equal("i can't point to that reliably from this view.", tts.Spoken.Single());
        Assert.Equal(1, llm.CallCount);

        harness.Shutdown();
    }

    [Fact]
    public async Task StructuredPointing_OutsideBounds_DoesNotDispatchOverlay()
    {
        var llm = new FakeLlmClient("""
            {"spokenText":"that is over here.","pointIntent":{"kind":"point","x":999,"y":120,"screen":1,"label":"box a","confidence":"high"}}
            """);
        var overlayCalls = new List<OverlayCall>();
        var harness = CreateHarness(
            new CompanionViewModel(),
            llm,
            new RecordingTtsClient(),
            overlayFlyTo: (point, bounds, label) => overlayCalls.Add(new OverlayCall(point, bounds, label)),
            useRedesignedPointingProtocol: true);

        harness.Manager.SendTranscriptToClaudeWithScreenshot("show me box a");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(overlayCalls);
        Assert.Equal("that is over here.", harness.Manager.ConversationHistory.Single().AssistantText);

        harness.Shutdown();
    }

    [Fact]
    public async Task StructuredPointing_SwapDuringStream_CancelsCleanly()
    {
        var llm = new BlockingLlmClient();
        var tts = new RecordingTtsClient();
        var vm = new CompanionViewModel();
        var harness = CreateHarness(vm, llm, tts, useRedesignedPointingProtocol: true);

        harness.Manager.SendTranscriptToClaudeWithScreenshot("show me box a");
        await llm.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.Manager.SwapLlmClientAsync(new FakeLlmClient("""
            {"spokenText":"new response.","pointIntent":{"kind":"none","reason":"none"}}
            """));

        Assert.True(llm.WasCanceled);
        Assert.True(tts.StopPlaybackCalled);
        Assert.Equal(VoiceState.Idle, vm.VoiceState);

        harness.Shutdown();
    }

    [Fact]
    public async Task StructuredPointing_DispatchesAtMostOnce()
    {
        var llm = new FakeLlmClient("""
            {"spokenText":"box a is here.","pointIntent":{"kind":"point","x":200,"y":120,"screen":1,"label":"box a","confidence":"high"}}
            """);
        var overlayCalls = new List<OverlayCall>();
        var harness = CreateHarness(
            new CompanionViewModel(),
            llm,
            new RecordingTtsClient(),
            overlayFlyTo: (point, bounds, label) => overlayCalls.Add(new OverlayCall(point, bounds, label)),
            useRedesignedPointingProtocol: true);

        harness.Manager.SendTranscriptToClaudeWithScreenshot("show me box a");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(overlayCalls);

        harness.Shutdown();
    }

    [Fact]
    public async Task StructuredPointing_MultipleTargets_DispatchesInOrder()
    {
        var llm = new FakeLlmClient("""
            {"spokenText":"first the api key, then the model setting.","pointIntents":[{"kind":"point","x":120,"y":80,"screen":1,"label":"api key","confidence":"high"},{"kind":"point","x":300,"y":180,"screen":1,"label":"model setting","confidence":"high"}]}
            """);
        var overlayCalls = new List<OverlayCall>();
        var harness = CreateHarness(
            new CompanionViewModel(),
            llm,
            new RecordingTtsClient(),
            overlayFlyTo: (point, bounds, label) => overlayCalls.Add(new OverlayCall(point, bounds, label)),
            useRedesignedPointingProtocol: true);

        harness.Manager.SendTranscriptToClaudeWithScreenshot("show me the api key setting and then the model setting");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, overlayCalls.Count);
        Assert.Equal("api key", overlayCalls[0].Label);
        Assert.Equal("model setting", overlayCalls[1].Label);
        Assert.Equal(new System.Windows.Point(250, 180), overlayCalls[0].Point);
        Assert.Equal(new System.Windows.Point(610, 380), overlayCalls[1].Point);
        Assert.Equal("first the api key, then the model setting.", harness.Manager.ConversationHistory.Single().AssistantText);

        harness.Shutdown();
    }

    [Fact]
    public async Task StructuredPointing_SecondTurnCancelsFirstAndDispatchesOnlyLatest()
    {
        var llm = new FirstCallBlocksSecondCallRespondsLlmClient("""
            {"spokenText":"box a is here.","pointIntent":{"kind":"point","x":200,"y":120,"screen":1,"label":"box a","confidence":"high"}}
            """);
        var overlayCalls = new List<OverlayCall>();
        var harness = CreateHarness(
            new CompanionViewModel(),
            llm,
            new RecordingTtsClient(),
            overlayFlyTo: (point, bounds, label) => overlayCalls.Add(new OverlayCall(point, bounds, label)),
            useRedesignedPointingProtocol: true);

        harness.Manager.SendTranscriptToClaudeWithScreenshot("first turn");
        await llm.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.Manager.SendTranscriptToClaudeWithScreenshot("second turn");
        await harness.Manager.CurrentResponseTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(llm.FirstCallCanceled);
        Assert.Equal(2, llm.CallCount);
        Assert.Single(overlayCalls);
        Assert.Equal("box a", overlayCalls.Single().Label);

        harness.Shutdown();
    }

    [Fact]
    public async Task ProviderTimingDiagnostics_RunsTextImageAndTtsProbesWithFakes()
    {
        var llm = new FakeLlmClient("diagnostic response. [POINT:none]");
        var tts = new RecordingTtsClient();
        var captureCount = 0;
        var harness = CreateHarness(
            new CompanionViewModel(),
            llm,
            tts,
            captureScreensAsync: (_, _, _) =>
            {
                captureCount++;
                return Task.FromResult(new List<CapturedScreen> { CreateScreen() });
            });

        await harness.Manager.RunProviderTimingDiagnosticsAsync();

        Assert.Equal(2, llm.CallCount);
        Assert.Equal(new[] { 0, 1 }, llm.ScreenCounts);
        Assert.Equal(1, captureCount);
        Assert.Equal("clicky timing diagnostic.", tts.Spoken.Single());

        harness.Shutdown();
    }

    private static Harness CreateHarness(
        CompanionViewModel viewModel,
        ILlmClient llm,
        ElevenLabsTtsClient tts,
        Func<IReadOnlyList<IntPtr>?, CancellationToken, bool, Task<List<CapturedScreen>>>? captureScreensAsync = null,
        Action<System.Windows.Point, Rectangle, string>? overlayFlyTo = null,
        Func<Task>? prepareForCaptureAsync = null,
        bool useRedesignedPointingProtocol = false)
    {
        var dispatcher = StartDispatcher();
        var hook = new GlobalPushToTalkHook(PushToTalkShortcut.ControlAlt);
        var transcriber = new AssemblyAiStreamingTranscriber("fake-key");
        var manager = new CompanionManager(
            viewModel,
            hook,
            llm,
            transcriber,
            tts,
            dispatcher,
            captureScreensAsync: captureScreensAsync ?? ((_, _, _) => Task.FromResult(new List<CapturedScreen> { CreateScreen() })),
            overlayFlyTo: overlayFlyTo,
            prepareForCaptureAsync: prepareForCaptureAsync,
            useRedesignedPointingProtocol: useRedesignedPointingProtocol);

        return new Harness(manager, dispatcher);
    }

    private static Dispatcher StartDispatcher()
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
        return dispatcher!;
    }

    private static CapturedScreen CreateScreen()
    {
        return new CapturedScreen
        {
            ImageBytes = CreateJpeg(400, 240),
            Label = "user's screen (cursor is here)",
            IsCursorScreen = true,
            DisplayBounds = new Rectangle(10, 20, 800, 480),
            ScreenshotPixelWidth = 400,
            ScreenshotPixelHeight = 240,
        };
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(32, 34, 38));
        using var brush = new SolidBrush(Color.FromArgb(80, 170, 255));
        graphics.FillRectangle(brush, 170, 90, 60, 60);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Jpeg);
        return stream.ToArray();
    }

    private static string? Normalize(string? value)
    {
        return value?.Replace('\u2014', '-');
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            await Task.Delay(20, cts.Token);
        }
    }

    private static string StructuredPromptMarker() => CompanionManager.StructuredPointingSystemPrompt;

    private sealed record Harness(CompanionManager Manager, Dispatcher Dispatcher)
    {
        public void Shutdown()
        {
            try
            {
                Manager.Dispose();
            }
            catch (FileLoadException ex) when (ex.Message.Contains("Application Control policy", StringComparison.OrdinalIgnoreCase))
            {
                // Some local Windows hosts block freshly built audio DLLs during test cleanup.
                // The turn assertions have already completed; keep CI-safe tests focused on orchestration.
            }
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        }
    }

    private sealed record OverlayCall(System.Windows.Point Point, Rectangle DisplayBounds, string Label);

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly string[] _deltas;
        private readonly Exception? _exception;

        public FakeLlmClient(params string[] deltas)
        {
            _deltas = deltas;
        }

        public FakeLlmClient(Exception exception)
        {
            _exception = exception;
            _deltas = Array.Empty<string>();
        }

        public string? UserText { get; private set; }
        public string SystemPrompt { get; private set; } = "";
        public IReadOnlyList<CapturedScreen> Screens { get; private set; } = Array.Empty<CapturedScreen>();
        public int CallCount { get; private set; }
        public List<int> ScreenCounts { get; } = new();

        public async IAsyncEnumerable<string> SendAsync(
            IReadOnlyList<Message> history,
            IReadOnlyList<CapturedScreen> screens,
            string systemPrompt,
            string userText,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (CallCount == 0)
            {
                UserText = userText;
                Screens = screens;
                SystemPrompt = systemPrompt;
            }
            ScreenCounts.Add(screens.Count);
            CallCount++;

            if (_exception is not null)
                throw _exception;

            foreach (var delta in _deltas)
            {
                ct.ThrowIfCancellationRequested();
                yield return delta;
                await Task.Delay(25, ct);
            }
        }
    }

    private sealed class BlockingLlmClient : ILlmClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCanceled { get; private set; }

        public async IAsyncEnumerable<string> SendAsync(
            IReadOnlyList<Message> history,
            IReadOnlyList<CapturedScreen> screens,
            string systemPrompt,
            string userText,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                throw;
            }

            yield break;
        }
    }

    private sealed class FirstCallBlocksSecondCallRespondsLlmClient : ILlmClient
    {
        private readonly string _secondResponse;

        public FirstCallBlocksSecondCallRespondsLlmClient(string secondResponse)
        {
            _secondResponse = secondResponse;
        }

        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FirstCallCanceled { get; private set; }
        public int CallCount { get; private set; }

        public async IAsyncEnumerable<string> SendAsync(
            IReadOnlyList<Message> history,
            IReadOnlyList<CapturedScreen> screens,
            string systemPrompt,
            string userText,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                FirstCallStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    FirstCallCanceled = true;
                    throw;
                }
            }

            yield return _secondResponse;
        }
    }

    private sealed class RecordingTtsClient : ElevenLabsTtsClient
    {
        private readonly Exception? _exception;

        public RecordingTtsClient(Exception? exception = null)
            : base("fake-key", "fake-voice", new HttpClient(new OkHandler()))
        {
            _exception = exception;
        }

        public List<string> Spoken { get; } = new();
        public bool StopPlaybackCalled { get; private set; }

        public override Task SpeakAsync(string text, CancellationToken ct = default)
        {
            if (_exception is not null)
                throw _exception;

            Spoken.Add(text);
            return Task.CompletedTask;
        }

        public override void StopPlayback()
        {
            StopPlaybackCalled = true;
            base.StopPlayback();
        }
    }

    private sealed class BlockingTtsClient : ElevenLabsTtsClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingTtsClient()
            : base("fake-key", "fake-voice", new HttpClient(new OkHandler()))
        {
        }

        public override async Task SpeakAsync(string text, CancellationToken ct = default)
        {
            await _release.Task.WaitAsync(ct);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
