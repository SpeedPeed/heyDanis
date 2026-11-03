using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using Clicky.Api;
using Clicky.Audio;
using Clicky.Capture;
using Clicky.Hotkey;
using Clicky.Overlay;
using Clicky.Pointing;

namespace Clicky.Companion;

/// <summary>
/// Orchestrates the push-to-talk â†’ capture â†’ transcribe â†’ Claude â†’ TTS pipeline,
/// mirroring <c>CompanionManager.swift</c> in the Mac reference implementation.
/// </summary>
public sealed class CompanionManager : IAsyncDisposable, IDisposable
{
    /// <summary>Max conversation history exchanges to keep (prevents unbounded context growth).</summary>
    private const int MaxConversationHistory = 10;
    private static readonly object PointDebugArtifactLock = new();
    private static readonly IReadOnlyDictionary<string, GeoTarget> GeoTargets =
        new Dictionary<string, GeoTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["algeria"] = new("Algeria", 28.0, 2.6),
            ["canada"] = new("Canada", 56.1, -106.3),
            ["egypt"] = new("Egypt", 26.8, 30.8),
            ["france"] = new("France", 46.2, 2.2),
            ["germany"] = new("Germany", 51.2, 10.4),
            ["deutschland"] = new("Germany", 51.2, 10.4),
            ["italy"] = new("Italy", 42.8, 12.5),
            ["poland"] = new("Poland", 52.0, 19.1),
            ["spain"] = new("Spain", 40.5, -3.7),
            ["united kingdom"] = new("United Kingdom", 54.8, -2.5),
            ["uk"] = new("United Kingdom", 54.8, -2.5),
            ["united states"] = new("United States", 39.8, -98.6),
            ["usa"] = new("United States", 39.8, -98.6),
        };

    private readonly CompanionViewModel _viewModel;
    private readonly GlobalPushToTalkHook _hook;
    private ILlmClient _llmClient;
    private readonly object _swapLock = new();
    private readonly AssemblyAiStreamingTranscriber _transcriber;
    private readonly ElevenLabsTtsClient _ttsClient;
    private readonly Dispatcher _dispatcher;
    private readonly OverlayWindowManager? _overlayManager;
    private readonly string? _microphoneDeviceId;
    private readonly Func<IReadOnlyList<IntPtr>?, CancellationToken, bool, Task<List<CapturedScreen>>> _captureScreensAsync;
    private readonly Action<System.Windows.Point, Rectangle, string>? _overlayFlyTo;
    private readonly Func<Task>? _prepareForCaptureAsync;
    private readonly bool _useRedesignedPointingProtocol;
    private string _llmProvider;
    private string _llmModel;

    private readonly List<Message> _conversationHistory = new();
    private CancellationTokenSource? _hookConsumerCts;
    private CancellationTokenSource? _responseCts;
    private Task? _currentResponseTask;
    private Task? _hookConsumerTask;
    private Task? _recordingTask;
    private Task? _stopRecordingTask;

    // Active recording session state
    private MicrophoneCapture? _activeMicCapture;
    private TranscriptionSession? _activeTranscriptionSession;
    private CancellationTokenSource? _micCaptureCts;

    // Capture-on-press: kick off screen capture when the key is first pressed so
    // the capture completes in parallel with the user speaking. The task is awaited
    // when the LLM request is built (after key release). Cancelled if the press
    // is shorter than ~100 ms (accidental tap).
    private Task<List<CapturedScreen>>? _captureOnPressTask;
    private CancellationTokenSource? _captureOnPressCts;
    private DateTime _pressTimestamp;

    /// <summary>The system prompt sent to Claude, mirroring Mac's companionVoiceResponseSystemPrompt.</summary>
    public static readonly string CompanionSystemPrompt = """
        you're clicky, a friendly always-on companion that lives in the user's system tray. the user just spoke to you via push-to-talk and you can see their screen(s). your reply will be spoken aloud via text-to-speech, so write the way you'd actually talk. this is an ongoing conversation â€” you remember everything they've said before.

        rules:
        - default to one or two sentences. be direct and dense. BUT if the user asks you to explain more, go deeper, or elaborate, then go all out â€” give a thorough, detailed explanation with no length limit.
        - all lowercase, casual, warm. no emojis.
        - write for the ear, not the eye. short sentences. no lists, bullet points, markdown, or formatting â€” just natural speech.
        - don't use abbreviations or symbols that sound weird read aloud. write "for example" not "e.g.", spell out small numbers.
        - if the user's question relates to what's on their screen, reference specific things you see.
        - if the screenshot doesn't seem relevant to their question, just answer the question directly.
        - you can help with anything â€” coding, writing, general knowledge, brainstorming.
        - never say "simply" or "just".
        - don't read out code verbatim. describe what the code does or what needs to change conversationally.
        - focus on giving a thorough, useful explanation. don't end with simple yes/no questions like "want me to explain more?" or "should i show you?" â€” those are dead ends that force the user to just say yes.
        - instead, when it fits naturally, end by planting a seed â€” mention something bigger or more ambitious they could try, a related concept that goes deeper, or a next-level technique that builds on what you just explained. make it something worth coming back for, not a question they'd just nod to. it's okay to not end with anything extra if the answer is complete on its own.
        - if you receive multiple screen images, the one labeled "primary focus" is where the cursor is â€” prioritize that one but reference others if relevant.

        element pointing:
        you have a small blue triangle cursor that can fly to and point at things on screen. use it whenever pointing would genuinely help the user â€” if they're asking how to do something, looking for a menu, trying to find a button, or need help navigating an app, point at the relevant element. err on the side of pointing rather than not pointing, because it makes your help way more useful and concrete.

        don't point at things when it would be pointless â€” like if the user asks a general knowledge question, or the conversation has nothing to do with what's on screen, or you'd just be pointing at something obvious they're already looking at. but if there's a specific UI element, menu, button, or area on screen that's relevant to what you're helping with, point at it.

        when you point, append a coordinate tag at the very end of your response, AFTER your spoken text. the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. the origin (0,0) is the top-left corner of the image. x increases rightward, y increases downward.

        format: [POINT:x,y:label] where x,y are integer pixel coordinates in the screenshot's coordinate space, and label is a short 1-3 word description of the element (like "search bar" or "save button"). if the element is on the cursor's screen you can omit the screen number. if the element is on a DIFFERENT screen, append :screenN where N is the screen number from the image label (e.g. :screen2). this is important â€” without the screen number, the cursor will point at the wrong place.

        if pointing wouldn't help, append [POINT:none].

        examples:
        - user asks how to color grade in final cut: "you'll want to open the color inspector â€” it's right up in the top right area of the toolbar. click that and you'll get all the color wheels and curves. [POINT:1100,42:color inspector]"
        - user asks what html is: "html stands for hypertext markup language, it's basically the skeleton of every web page. curious how it connects to the css you're looking at? [POINT:none]"
        - user asks how to commit in xcode: "see that source control menu up top? click that and hit commit, or you can use command option c as a shortcut. [POINT:285,11:source control]"
        - element is on screen 2 (not where cursor is): "that's over on your other monitor â€” see the terminal window? [POINT:400,300:terminal:screen2]"
        """;

    public static readonly string StructuredPointingSystemPrompt = """
        you're clicky, a friendly always-on companion that can see the user's screen. return only one json object. do not use markdown.

        schema:
        {"spokenText":"short natural sentence for text-to-speech","pointIntents":[{"kind":"point","x":0,"y":0,"screen":1,"label":"first target","confidence":"high"},{"kind":"point","x":0,"y":0,"screen":1,"label":"second target","confidence":"high"}]}
        or:
        {"spokenText":"short natural sentence for text-to-speech","pointIntents":[{"kind":"none","reason":"not_visible"}]}

        rules:
        - decide whether a visible pointer would help answer the user.
        - point only for show, find, click, locate, identify, navigation, or app-control requests where the target is visible and unambiguous.
        - if the user asks for multiple visible things, return them in the order clicky should showcase them.
        - use one point for ordinary single-target requests. use two to five points only when the user's request genuinely needs multiple targets.
        - for multiple targets, spokenText should mention the targets in the same order, using short clauses or short sentences so the pointer can advance naturally while speaking.
        - coordinates are screenshot pixels with origin at the top-left of the selected image. x increases rightward, y increases downward.
        - for map regions, choose a coordinate inside the visible filled region or border of the requested country or region. never choose nearby labels, neighboring countries, oceans, legends, or text outside the region.
        - if unsure, return kind "none". a skipped pointer is better than a wrong pointer.
        - confidence must be "high" for a point. use "none" for low confidence.
        - keep each label short and specific, because it appears next to the pointer.
        """;

    public static readonly string GamerModeStructuredPointingSystemPrompt = """
        you're clicky, a gamer-mode voice companion that can see the user's screen. return only one json object. do not use markdown.

        schema:
        {"spokenText":"short natural sentence for text-to-speech","pointIntents":[{"kind":"circle","x":0,"y":0,"radius":48,"screen":1,"label":"target","confidence":"high"},{"kind":"arrow","x1":0,"y1":0,"x2":120,"y2":80,"screen":1,"label":"direction","confidence":"high"}]}
        or:
        {"spokenText":"short natural sentence for text-to-speech","pointIntents":[{"kind":"none","reason":"not_visible"}]}

        rules:
        - gamer mode can draw temporary annotations over the game. prefer circles and arrows over the pointer.
        - use a circle to highlight a unit, item, enemy, minimap area, lane spot, button, or other visible region.
        - use an arrow to show direction, pathing, rotation, camera movement, or where pressure should move.
        - use one to three annotations unless the user asks for more.
        - only draw what is visible and unambiguous in the screenshot.
        - coordinates are screenshot pixels with origin at the top-left of the selected image. x increases rightward, y increases downward.
        - for circles, x and y are the center and radius is in screenshot pixels.
        - for arrows, x1,y1 is the tail and x2,y2 is the arrow tip.
        - if unsure, return kind "none". a skipped drawing is better than a wrong drawing.
        - confidence must be "high" for every circle or arrow. use "none" for low confidence.
        - spokenText should explain the annotation briefly, without mentioning json or coordinates.
        """;

    /// <summary>
    /// Raised when the pipeline encounters a key-related error (401, empty key)
    /// that requires the user to open Settings and fix their configuration.
    /// </summary>
    public event EventHandler? OpenSettingsRequested;

    public CompanionManager(
        CompanionViewModel viewModel,
        GlobalPushToTalkHook hook,
        ILlmClient llmClient,
        AssemblyAiStreamingTranscriber transcriber,
        ElevenLabsTtsClient ttsClient,
        Dispatcher dispatcher,
        OverlayWindowManager? overlayManager = null,
        string? microphoneDeviceId = null,
        Func<IReadOnlyList<IntPtr>?, CancellationToken, bool, Task<List<CapturedScreen>>>? captureScreensAsync = null,
        Action<System.Windows.Point, Rectangle, string>? overlayFlyTo = null,
        Func<Task>? prepareForCaptureAsync = null,
        string llmProvider = "unknown",
        string llmModel = "unknown",
        bool useRedesignedPointingProtocol = false)
    {
        _viewModel = viewModel;
        _hook = hook;
        _llmClient = llmClient;
        _transcriber = transcriber;
        _ttsClient = ttsClient;
        _dispatcher = dispatcher;
        _overlayManager = overlayManager;
        _microphoneDeviceId = microphoneDeviceId;
        _captureScreensAsync = captureScreensAsync ?? ((excludeHwnds, ct, drawGrid) =>
            ScreenCapture.CaptureAllScreensAsJpegAsync(excludeHwnds, ct, drawGrid));
        _overlayFlyTo = overlayFlyTo;
        _prepareForCaptureAsync = prepareForCaptureAsync;
        _llmProvider = llmProvider;
        _llmModel = llmModel;
        _useRedesignedPointingProtocol = useRedesignedPointingProtocol;
    }

    /// <summary>
    /// Starts consuming hotkey transitions and reacting to push-to-talk.
    /// </summary>
    public void Start()
    {
        _hookConsumerCts = new CancellationTokenSource();
        _hookConsumerTask = ConsumeHotkeyTransitionsAsync(_hookConsumerCts.Token);
    }

    /// <summary>Conversation history, exposed for testing.</summary>
    internal IReadOnlyList<Message> ConversationHistory => _conversationHistory;

    /// <summary>The active response task, exposed so deterministic tests can await the full turn.</summary>
    internal Task? CurrentResponseTask => _currentResponseTask;

    private async Task ConsumeHotkeyTransitionsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var transition in _hook.Transitions.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Marshal to UI thread for ViewModel updates and state management
                await _dispatcher.InvokeAsync(() => HandleShortcutTransition(transition));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private void HandleShortcutTransition(ShortcutTransition transition)
    {
        switch (transition)
        {
            case ShortcutTransition.Pressed:
                HandlePressed();
                break;
            case ShortcutTransition.Released:
                HandleReleased();
                break;
        }
    }

    private void HandlePressed()
    {
        // Don't start a new recording if already listening
        if (_viewModel.VoiceState == VoiceState.Listening)
            return;

        // Cancel any in-progress response and TTS from a previous utterance
        // (mirrors currentResponseTask?.cancel() + elevenLabsTTSClient.stopPlayback())
        CancelCurrentResponse();

        _viewModel.IsShortcutPressed = true;
        _viewModel.VoiceState = VoiceState.Listening;
        _pressTimestamp = DateTime.UtcNow;
        DebugLog.Write("[VOICE] shortcut pressed: recording started");

        // Hide the overlay cursor so it doesn't appear in the capture.
        if (_overlayManager is not null)
        {
            _dispatcher.Invoke(() =>
            {
                _overlayManager.SuspendCursorFollowing();
            });
        }

        // Kick off screen capture immediately on key press so it runs in parallel
        // with the user speaking. The result is awaited when the LLM request is built.
        _captureOnPressCts = new CancellationTokenSource();
        var excludeHwnds = _overlayManager?.OverlayHwnds;
        _captureOnPressTask = CaptureScreensForLlmAsync(excludeHwnds, _captureOnPressCts.Token);

        // Start mic capture + transcription session on a background task
        _micCaptureCts = new CancellationTokenSource();
        _recordingTask = StartRecordingAsync(_micCaptureCts.Token);
    }

    private void HandleReleased()
    {
        _viewModel.IsShortcutPressed = false;

        if (_viewModel.VoiceState != VoiceState.Listening)
            return;

        _viewModel.VoiceState = VoiceState.Processing;
        DebugLog.Write("[VOICE] shortcut released: finalizing transcript");

        // Stop mic capture and request final transcript
        _stopRecordingTask = StopRecordingAndProcessAsync();
    }

    private async Task StartRecordingAsync(CancellationToken ct)
    {
        // Step 1: start the transcription session (token fetch + websocket).
        // Failures here are network / AssemblyAI-key related, NOT mic related â€”
        // keep this error path separate so we don't blame the user's microphone
        // for a missing API key or a broken connection.
        try
        {
            _activeTranscriptionSession = await _transcriber.StartSessionAsync(ct: ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (IsTranscriptionAuthError(ex))
        {
            DebugLog.Write("Transcription auth error (AssemblyAI key missing or invalid)", ex);
            await _dispatcher.InvokeAsync(() =>
            {
                _viewModel.LastError = "Your AssemblyAI key is missing or invalid. Open Settings to fix.";
                _viewModel.VoiceState = VoiceState.Idle;
                OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
            });
            return;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Transcription start failed", ex);
            await _dispatcher.InvokeAsync(() =>
            {
                _viewModel.LastError = $"Couldn't start transcription \u2014 check your internet connection and AssemblyAI key. ({ex.Message})";
                _viewModel.VoiceState = VoiceState.Idle;
            });
            return;
        }

        // Step 2: open the mic and stream frames into the transcription session.
        // Failures here ARE mic related (device unplugged, permission denied,
        // unsupported format) â€” show a mic-specific error.
        try
        {
            _activeMicCapture = new MicrophoneCapture(_microphoneDeviceId);
            await foreach (var frame in _activeMicCapture.CaptureFramesAsync(ct).ConfigureAwait(false))
            {
                if (_activeTranscriptionSession is not null)
                {
                    await _activeTranscriptionSession.SendAudioAsync(frame, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when recording is stopped
        }
        catch (Exception ex)
        {
            DebugLog.Write("Microphone capture failed", ex);

            // Best-effort cleanup of the transcription session we just opened.
            try
            {
                if (_activeTranscriptionSession is not null)
                {
                    await _activeTranscriptionSession.CancelAsync().ConfigureAwait(false);
                    _activeTranscriptionSession.Dispose();
                }
            }
            catch { /* best effort */ }
            _activeTranscriptionSession = null;

            await _dispatcher.InvokeAsync(() =>
            {
                _viewModel.LastError = $"Couldn't start recording \u2014 check that your microphone is connected and allowed in Windows Settings. ({ex.Message})";
                _viewModel.VoiceState = VoiceState.Idle;
            });
        }
    }

    /// <summary>
    /// Detects whether an exception from <see cref="AssemblyAiStreamingTranscriber.StartSessionAsync"/>
    /// indicates a missing or unauthorized API key (vs. a transient network issue).
    /// </summary>
    private static bool IsTranscriptionAuthError(Exception ex)
    {
        if (ex is HttpRequestException http)
        {
            var msg = http.Message;
            return msg.Contains("(401)")
                || msg.Contains("(403)")
                || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private async Task StopRecordingAndProcessAsync()
    {
        // Stop mic capture
        _micCaptureCts?.Cancel();
        _activeMicCapture?.Dispose();
        _activeMicCapture = null;

        var session = _activeTranscriptionSession;
        _activeTranscriptionSession = null;

        if (session is null)
        {
            ClearCaptureOnPress();
            DebugLog.Write("[VOICE] no active transcription session on release");
            await _dispatcher.InvokeAsync(() => _viewModel.VoiceState = VoiceState.Idle);
            return;
        }

        try
        {
            // Request final transcript (waits up to 1.4s grace period)
            var transcript = await session.RequestFinalTranscriptAsync().ConfigureAwait(false);
            session.Dispose();
            DebugLog.Write($"[VOICE] final transcript chars={transcript?.Length ?? 0}");

            if (string.IsNullOrWhiteSpace(transcript))
            {
                ClearCaptureOnPress();
                DebugLog.Write("[VOICE] final transcript empty: returning to idle without LLM request");
                await _dispatcher.InvokeAsync(() =>
                {
                    _viewModel.LastError = "I didn't catch any speech. Hold the shortcut while speaking, then release.";
                    _viewModel.VoiceState = VoiceState.Idle;
                });
                return;
            }

            // Send transcript to Claude with screenshots
            SendTranscriptToClaudeWithScreenshot(transcript);
        }
        catch (Exception ex)
        {
            ClearCaptureOnPress();
            DebugLog.Write("Transcription error (stop/finalize)", ex);
            session.Dispose();
            await _dispatcher.InvokeAsync(() =>
            {
                _viewModel.LastError = "Couldn't transcribe your speech \u2014 check your internet connection or your AssemblyAI key in Settings.";
                _viewModel.VoiceState = VoiceState.Idle;
            });
        }
    }

    /// <summary>
    /// Captures screenshots, sends transcript + images to Claude, streams response,
    /// then plays TTS. Mirrors <c>sendTranscriptToClaudeWithScreenshot</c> on Mac.
    /// </summary>
    internal void SendTranscriptToClaudeWithScreenshot(string transcript)
    {
        CancelCurrentResponse();

        _responseCts = new CancellationTokenSource();
        var ct = _responseCts.Token;

        // Grab the capture-on-press task (started in HandlePressed) so we can
        // await its result instead of re-capturing after key release.
        var captureTask = _captureOnPressTask;
        var capturePressCts = _captureOnPressCts;
        var pressDuration = DateTime.UtcNow - _pressTimestamp;
        _captureOnPressTask = null;
        _captureOnPressCts = null;

        _currentResponseTask = Task.Run(async () =>
        {
            await _dispatcher.InvokeAsync(() => _viewModel.VoiceState = VoiceState.Processing);

            var timing = TurnTimingRecord.Start(_llmProvider, _llmModel);
            timing.Mark("turn-start", $"transcriptChars={transcript.Length}");
            timing.Mark("transcript-final-available", $"chars={transcript.Length}");
            var firstLlmTokenSeen = false;
            var firstTtsEnqueueSeen = false;
            var firstTtsPlaybackSeen = false;

            try
            {
                List<CapturedScreen> screens;

                // If the press was shorter than 100 ms (accidental tap), cancel the
                // capture-on-press and re-capture fresh.
                if (pressDuration.TotalMilliseconds < 100 || captureTask is null)
                {
                    capturePressCts?.Cancel();
                    var excludeHwnds = _overlayManager?.OverlayHwnds;
                    timing.Mark("capture-start", "source=release");
                    screens = await CaptureScreensForLlmAsync(excludeHwnds, CancellationToken.None).ConfigureAwait(false);
                    timing.Mark("capture-end", TurnTimingRecord.FormatScreenEvidence(screens, gridAnnotated: false, cursorScreenFiltering: false));
                }
                else
                {
                    // Await the capture that was started on key press
                    timing.Mark("capture-start", "source=press alreadyStarted=true");
                    screens = await captureTask.ConfigureAwait(false);
                    timing.Mark("capture-end", TurnTimingRecord.FormatScreenEvidence(screens, gridAnnotated: false, cursorScreenFiltering: false));
                }

                if (ct.IsCancellationRequested) return;

                // â”€â”€ [POINT] diagnostic logging: screen capture results â”€â”€
                foreach (var s in screens)
                {
                    DebugLog.Write($"[POINT] capture: {s.Label} bounds=({s.DisplayBounds.X},{s.DisplayBounds.Y},{s.DisplayBounds.Width},{s.DisplayBounds.Height}) " +
                        $"screenshot={s.ScreenshotPixelWidth}x{s.ScreenshotPixelHeight} cursor={s.IsCursorScreen}");
                }

                // Append dimension info to labels (so Claude knows the coordinate space)
                var labeledScreens = screens.Select(s => new CapturedScreen
                {
                    ImageBytes = s.ImageBytes,
                    Label = $"{s.Label} (image dimensions: {s.ScreenshotPixelWidth}x{s.ScreenshotPixelHeight} pixels)",
                    IsCursorScreen = s.IsCursorScreen,
                    DisplayBounds = s.DisplayBounds,
                    ScreenshotPixelWidth = s.ScreenshotPixelWidth,
                    ScreenshotPixelHeight = s.ScreenshotPixelHeight,
                }).ToList();

                // Build history snapshot for the API call
                var historySnapshot = _conversationHistory.ToList();

                // Stream Claude's response, split into sentences, and pipeline TTS
                var responseBuilder = new StringBuilder();
                var tokenizer = new SentenceTokenizer();
                var pipeline = new TtsPipeline(_ttsClient, ct, sentence =>
                {
                    if (!firstTtsPlaybackSeen)
                    {
                        firstTtsPlaybackSeen = true;
                        timing.Mark("first-tts-playback-start", $"sentenceChars={sentence.Length}");
                    }
                });
                var ttsStarted = false;

                if (_useRedesignedPointingProtocol)
                {
                    await RunStructuredPointingTurnAsync(
                        historySnapshot,
                        labeledScreens,
                        screens,
                        transcript,
                        responseBuilder,
                        pipeline,
                        timing,
                        ct,
                        firstLlmTokenSeen,
                        firstTtsEnqueueSeen,
                        firstTtsPlaybackSeen).ConfigureAwait(false);
                    return;
                }

                timing.Mark("llm-request-start", $"history={historySnapshot.Count} screens={labeledScreens.Count}");
                await foreach (var delta in _llmClient.SendAsync(
                    historySnapshot,
                    labeledScreens,
                    KnowledgeContextProvider.BuildPrompt(CompanionSystemPrompt),
                    transcript,
                    ct).ConfigureAwait(false))
                {
                    if (!firstLlmTokenSeen)
                    {
                        firstLlmTokenSeen = true;
                        timing.Mark("first-llm-token", $"deltaChars={delta.Length}");
                    }

                    responseBuilder.Append(delta);

                    // Feed delta to sentence tokenizer; enqueue complete sentences for TTS
                    var sentences = tokenizer.Feed(delta);
                    foreach (var sentence in sentences)
                    {
                        // Strip [POINT:...] tags from each sentence before speaking
                        var cleaned = PointTagParser.Parse(sentence).SpokenText.Trim();
                        if (string.IsNullOrWhiteSpace(cleaned)) continue;

                        if (!ttsStarted)
                        {
                            ttsStarted = true;
                            if (!firstTtsEnqueueSeen)
                            {
                                firstTtsEnqueueSeen = true;
                                timing.Mark("first-tts-enqueue", $"sentenceChars={cleaned.Length}");
                            }
                            await _dispatcher.InvokeAsync(() => _viewModel.VoiceState = VoiceState.Responding);
                        }
                        pipeline.Enqueue(cleaned);
                    }
                }

                // Flush any remaining text from the tokenizer
                var remainder = tokenizer.Flush();
                if (remainder is not null)
                {
                    var cleaned = PointTagParser.Parse(remainder).SpokenText.Trim();
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        if (!ttsStarted)
                        {
                            ttsStarted = true;
                            if (!firstTtsEnqueueSeen)
                            {
                                firstTtsEnqueueSeen = true;
                                timing.Mark("first-tts-enqueue", $"sentenceChars={cleaned.Length}");
                            }
                            await _dispatcher.InvokeAsync(() => _viewModel.VoiceState = VoiceState.Responding);
                        }
                        pipeline.Enqueue(cleaned);
                    }
                }

                pipeline.Complete();
                timing.Mark("llm-response-end", $"responseChars={responseBuilder.Length}");

                if (ct.IsCancellationRequested) return;

                var fullResponse = responseBuilder.ToString();

                // Parse [POINT:...] tags from the full response for the overlay cursor
                var parseResult = PointTagParser.Parse(fullResponse);
                var spokenText = parseResult.SpokenText;
                timing.Mark("point-parse", TurnTimingRecord.FormatPointDirective(parseResult.Directive));

                // â”€â”€ [POINT] diagnostic logging: parse result â”€â”€
                if (parseResult.Directive is { } dir)
                {
                    DebugLog.Write($"[POINT] parse: directive found x={dir.X} y={dir.Y} label=\"{dir.Label}\" screen={dir.ScreenNumber?.ToString() ?? "null"}");
                }
                else
                {
                    DebugLog.Write("[POINT] parse: no directive found");
                }

                // Save this exchange to conversation history (with point tags stripped
                // so they don't confuse future context â€” mirrors Mac behavior)
                _conversationHistory.Add(new Message(transcript, spokenText));
                if (_conversationHistory.Count > MaxConversationHistory)
                {
                    _conversationHistory.RemoveRange(0, _conversationHistory.Count - MaxConversationHistory);
                }

                // Dispatch point directive to overlay cursor if present.
                // Mac sets voiceState=Idle BEFORE setting pointing coordinates so the
                // triangle cursor becomes visible before the fly animation.
                if (parseResult.Directive is { } directive && (_overlayManager is not null || _overlayFlyTo is not null))
                {
                    directive = ApplyGeoMapOverrideIfAvailable(transcript, directive, screens);
                    if (await DispatchPointDirectiveAsync(directive, screens, ct).ConfigureAwait(false))
                    {
                        timing.Mark("overlay-dispatch");
                    }
                }

                // Wait for all queued TTS sentences to finish playing
                await pipeline.WaitForCompletionAsync().ConfigureAwait(false);
                timing.Mark("tts-complete");
                timing.Mark("turn-complete");
            }
            catch (OperationCanceledException)
            {
                // User spoke again â€” response was interrupted. Expected.
                timing.Mark("turn-canceled");
            }
            catch (HttpRequestException httpEx) when (IsKeyError(httpEx))
            {
                timing.Mark("turn-error", "kind=llm-key");
                DebugLog.Write("LLM key error in pipeline", httpEx);
                await _dispatcher.InvokeAsync(() =>
                {
                    _viewModel.LastError = "Your API key is missing or invalid. Open Settings to fix.";
                    OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (HttpRequestException httpEx) when (!IsKeyError(httpEx))
            {
                timing.Mark("turn-error", "kind=http");
                DebugLog.Write("Response pipeline HTTP error", httpEx);
                await _dispatcher.InvokeAsync(() =>
                {
                    _viewModel.LastError = "Couldn't get a response \u2014 check your internet connection and try again.";
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not HttpRequestException)
            {
                timing.Mark("turn-error", $"kind=unhandled type={ex.GetType().Name}");
                DebugLog.Write("Response pipeline unhandled error", ex);
                await _dispatcher.InvokeAsync(() =>
                {
                    _viewModel.LastError = "Something went wrong \u2014 try again, and if it keeps happening, check Settings.";
                });
            }

            if (!ct.IsCancellationRequested)
            {
                await _dispatcher.InvokeAsync(() => _viewModel.VoiceState = VoiceState.Idle);
            }
        }, ct);
    }

    private async Task RunStructuredPointingTurnAsync(
        IReadOnlyList<Message> historySnapshot,
        IReadOnlyList<CapturedScreen> labeledScreens,
        IReadOnlyList<CapturedScreen> screens,
        string transcript,
        StringBuilder responseBuilder,
        TtsPipeline pipeline,
        TurnTimingRecord timing,
        CancellationToken ct,
        bool firstLlmTokenSeen,
        bool firstTtsEnqueueSeen,
        bool firstTtsPlaybackSeen)
    {
        var gamerModeEnabled = _viewModel.GamerModeEnabled;
        var systemPrompt = gamerModeEnabled
            ? GamerModeStructuredPointingSystemPrompt
            : StructuredPointingSystemPrompt;

        timing.Mark("llm-request-start", $"history={historySnapshot.Count} screens={labeledScreens.Count} protocol={(gamerModeEnabled ? "gamer-drawing" : "structured-pointing")}");
        await foreach (var delta in _llmClient.SendAsync(
            historySnapshot,
            labeledScreens,
            KnowledgeContextProvider.BuildPrompt(systemPrompt),
            transcript,
            ct).ConfigureAwait(false))
        {
            if (!firstLlmTokenSeen)
            {
                firstLlmTokenSeen = true;
                timing.Mark("first-llm-token", $"deltaChars={delta.Length}");
            }

            responseBuilder.Append(delta);
        }

        timing.Mark("llm-response-end", $"responseChars={responseBuilder.Length} protocol=structured-pointing");
        if (ct.IsCancellationRequested) return;

        var result = StructuredPointingTurnParser.Parse(responseBuilder.ToString());
        var directives = StructuredPointingTurnParser.ToDirectives(result.PointIntents, screens);
        var drawingDirectives = gamerModeEnabled
            ? StructuredPointingTurnParser.ToDrawingDirectives(result.PointIntents, screens)
            : [];
        var directive = directives.FirstOrDefault();
        timing.Mark("point-parse", directives.Count == 0
            ? $"kind={result.PointIntent.Kind} drawings={drawingDirectives.Count} reason={result.PointIntent.NoPointReason ?? "none"}"
            : $"points={directives.Count} first={TurnTimingRecord.FormatPointDirective(directive)}");

        if (directive is { } dir)
        {
            DebugLog.Write($"[POINT] parse: structured directive x={dir.X} y={dir.Y} label=\"{dir.Label}\" screen={dir.ScreenNumber?.ToString() ?? "null"}");
        }
        else
        {
            DebugLog.Write($"[POINT] parse: structured no point reason=\"{result.PointIntent.NoPointReason ?? result.PointIntent.Kind.ToString()}\"");
        }

        _conversationHistory.Add(new Message(transcript, result.SpokenText));
        if (_conversationHistory.Count > MaxConversationHistory)
        {
            _conversationHistory.RemoveRange(0, _conversationHistory.Count - MaxConversationHistory);
        }

        if (gamerModeEnabled && drawingDirectives.Count > 0 && _overlayManager is not null)
        {
            if (await DispatchDrawingDirectivesAsync(drawingDirectives, screens, ct).ConfigureAwait(false))
            {
                timing.Mark("overlay-drawing-dispatch");
            }
        }
        else if (directives.Count > 0 && (_overlayManager is not null || _overlayFlyTo is not null))
        {
            var adjustedDirectives = directives
                .Select(d => ApplyGeoMapOverrideIfAvailable(transcript, d, screens))
                .ToArray();
            if (await DispatchPointDirectivesAsync(adjustedDirectives, screens, ct).ConfigureAwait(false))
            {
                timing.Mark("overlay-dispatch");
            }
        }

        var cleaned = result.SpokenText.Trim();
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            if (!firstTtsEnqueueSeen)
            {
                firstTtsEnqueueSeen = true;
                timing.Mark("first-tts-enqueue", $"sentenceChars={cleaned.Length}");
            }

            pipeline.Enqueue(cleaned);
            if (!firstTtsPlaybackSeen)
            {
                firstTtsPlaybackSeen = true;
            }

            await _dispatcher.InvokeAsync(() => _viewModel.VoiceState = VoiceState.Responding);
        }

        pipeline.Complete();
        await pipeline.WaitForCompletionAsync().ConfigureAwait(false);
        timing.Mark("tts-complete");
        timing.Mark("turn-complete");
        if (!ct.IsCancellationRequested)
        {
            await _dispatcher.InvokeAsync(() => _viewModel.VoiceState = VoiceState.Idle);
        }
    }

    private void ClearCaptureOnPress()
    {
        _captureOnPressCts?.Cancel();
        _captureOnPressCts?.Dispose();
        _captureOnPressCts = null;
        _captureOnPressTask = null;
    }

    internal static PointDirective ApplyGeoMapOverrideIfAvailable(
        string transcript,
        PointDirective directive,
        IReadOnlyList<CapturedScreen> screens)
    {
        if (!TryFindGeoTarget($"{transcript} {directive.Label}", out var target))
            return directive;

        var screen = SelectDirectiveScreen(directive, screens);
        if (screen is null)
            return directive;

        var (x, y) = ProjectWebMercator(target.Latitude, target.Longitude, screen.ScreenshotPixelWidth, screen.ScreenshotPixelHeight);
        var overridden = directive with
        {
            X = x,
            Y = y,
        };

        DebugLog.Write(
            $"[POINT] geo-override: target=\"{target.Label}\" lat={target.Latitude:F3} lon={target.Longitude:F3} " +
            $"model=({directive.X},{directive.Y}) projected=({overridden.X},{overridden.Y}) screen={directive.ScreenNumber?.ToString() ?? "cursor"}");

        return overridden;
    }

    private static CapturedScreen? SelectDirectiveScreen(PointDirective directive, IReadOnlyList<CapturedScreen> screens)
    {
        if (screens.Count == 0)
            return null;

        if (directive.ScreenNumber is { } screenNumber)
        {
            var index = screenNumber - 1;
            if (index >= 0 && index < screens.Count)
                return screens[index];
        }

        return screens.FirstOrDefault(s => s.IsCursorScreen) ?? screens[0];
    }

    private static bool TryFindGeoTarget(string text, out GeoTarget target)
    {
        foreach (var entry in GeoTargets)
        {
            if (ContainsWordOrPhrase(text, entry.Key))
            {
                target = entry.Value;
                return true;
            }
        }

        target = default;
        return false;
    }

    private static bool ContainsWordOrPhrase(string text, string term)
    {
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + term.Length;
            var after = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (before && after)
                return true;

            index = text.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    internal static (int X, int Y) ProjectWebMercator(double latitude, double longitude, int width, int height)
    {
        var clampedLatitude = Math.Clamp(latitude, -85.05112878, 85.05112878);
        var latRad = clampedLatitude * Math.PI / 180.0;
        var x = (longitude + 180.0) / 360.0 * width;
        var mercatorY = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * height;

        return (
            (int)Math.Clamp(Math.Round(x), 0, width - 1),
            (int)Math.Clamp(Math.Round(mercatorY), 0, height - 1));
    }

    internal readonly record struct GeoTarget(string Label, double Latitude, double Longitude);

    /// <summary>
    /// Cancels any in-flight Claude request and TTS playback.
    /// </summary>
    private void CancelCurrentResponse()
    {
        _responseCts?.Cancel();
        _responseCts?.Dispose();
        _responseCts = null;
        _captureOnPressCts?.Cancel();
        _captureOnPressCts?.Dispose();
        _captureOnPressCts = null;
        _captureOnPressTask = null;
        _ttsClient.StopPlayback();
    }

    private async Task<List<CapturedScreen>> CaptureScreensForLlmAsync(
        IReadOnlyList<IntPtr>? excludeHwnds,
        CancellationToken ct)
    {
        if (_overlayManager is not null)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _overlayManager.SuspendCursorFollowing();
            });
        }

        if (_prepareForCaptureAsync is not null)
        {
            DebugLog.Write("[POINT] capture-prep: requesting Clicky UI hide before screenshot");
            await _prepareForCaptureAsync().ConfigureAwait(false);
        }

        try
        {
            return await _captureScreensAsync(excludeHwnds, ct, false).ConfigureAwait(false);
        }
        finally
        {
            if (_overlayManager is not null)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    _overlayManager.ResumeCursorFollowing();
                });
            }
        }
    }

    private async Task<bool> DispatchPointDirectiveAsync(
        PointDirective directive,
        IReadOnlyList<CapturedScreen> screens,
        CancellationToken ct)
    {
        return await DispatchPointDirectivesAsync([directive], screens, ct).ConfigureAwait(false);
    }

    private async Task<bool> DispatchPointDirectivesAsync(
        IReadOnlyList<PointDirective> directives,
        IReadOnlyList<CapturedScreen> screens,
        CancellationToken ct)
    {
        var targets = new List<OverlayPointTarget>();
        foreach (var directive in directives)
        {
            var converted = PointTagParser.ConvertToScreenCoordinatesDetailed(directive, screens);
            if (converted is null)
            {
                DebugLog.Write($"[POINT] convert: returned null - screens.Count={screens.Count}, screenNumber={directive.ScreenNumber?.ToString() ?? "null"}");
                continue;
            }

            SavePointDebugArtifact(directive, converted);
            DebugLog.Write(
                $"[POINT] convert: targetLabel=\"{converted.TargetScreen.Label}\" directive=({directive.X},{directive.Y}) " +
                $"clamped=({converted.ClampedX},{converted.ClampedY}) screenshot={converted.TargetScreen.ScreenshotPixelWidth}x{converted.TargetScreen.ScreenshotPixelHeight} " +
                $"displayBounds=({converted.DisplayBounds.X},{converted.DisplayBounds.Y},{converted.DisplayBounds.Width},{converted.DisplayBounds.Height}) " +
                $"scale=({converted.ScaleX:F4},{converted.ScaleY:F4}) displayLocal=({converted.DisplayLocalPoint.X:F2},{converted.DisplayLocalPoint.Y:F2}) " +
                $"screenPoint=({converted.ScreenPoint.X:F1},{converted.ScreenPoint.Y:F1})");
            targets.Add(new OverlayPointTarget(new System.Windows.Point(converted.ScreenPoint.X, converted.ScreenPoint.Y), converted.DisplayBounds, directive.Label));
        }

        if (targets.Count == 0)
            return false;

        await _dispatcher.InvokeAsync(() =>
        {
            _viewModel.VoiceState = VoiceState.Idle;
            DispatchOverlayFlyToSequence(targets);
        }, DispatcherPriority.Normal, ct);
        DebugLog.Write($"[POINT] flyto: dispatched sequence count={targets.Count}");
        return true;
    }

    private async Task<bool> DispatchDrawingDirectivesAsync(
        IReadOnlyList<DrawingDirective> directives,
        IReadOnlyList<CapturedScreen> screens,
        CancellationToken ct)
    {
        if (_overlayManager is null)
            return false;

        var targets = new List<OverlayAnnotationTarget>();
        foreach (var directive in directives)
        {
            var target = ConvertDrawingDirectiveToOverlayTarget(directive, screens);
            if (target is null)
            {
                DebugLog.Write($"[DRAW] convert: returned null - kind={directive.Kind} screen={directive.ScreenNumber}");
                continue;
            }

            targets.Add(target);
        }

        if (targets.Count == 0)
            return false;

        await _dispatcher.InvokeAsync(() =>
        {
            _viewModel.VoiceState = VoiceState.Idle;
            _overlayManager.DrawAnnotations(targets);
        }, DispatcherPriority.Normal, ct);
        DebugLog.Write($"[DRAW] overlay: dispatched annotations count={targets.Count}");
        return true;
    }

    private static OverlayAnnotationTarget? ConvertDrawingDirectiveToOverlayTarget(
        DrawingDirective directive,
        IReadOnlyList<CapturedScreen> screens)
    {
        var screenIndex = directive.ScreenNumber - 1;
        if (screenIndex < 0 || screenIndex >= screens.Count)
            return null;

        var screen = screens[screenIndex];
        var scaleX = (double)screen.DisplayBounds.Width / screen.ScreenshotPixelWidth;
        var scaleY = (double)screen.DisplayBounds.Height / screen.ScreenshotPixelHeight;

        System.Windows.Point ToScreenPoint(int x, int y) => new(
            screen.DisplayBounds.X + x * scaleX,
            screen.DisplayBounds.Y + y * scaleY);

        if (directive.Kind == DrawingDirectiveKind.Circle)
        {
            return new OverlayAnnotationTarget
            {
                Kind = OverlayAnnotationKind.Circle,
                DisplayBounds = screen.DisplayBounds,
                Label = directive.Label,
                Point = ToScreenPoint(directive.X, directive.Y),
                Radius = directive.Radius * ((scaleX + scaleY) / 2.0),
            };
        }

        return new OverlayAnnotationTarget
        {
            Kind = OverlayAnnotationKind.Arrow,
            DisplayBounds = screen.DisplayBounds,
            Label = directive.Label,
            StartPoint = ToScreenPoint(directive.X1, directive.Y1),
            EndPoint = ToScreenPoint(directive.X2, directive.Y2),
        };
    }


    /// <summary>
    /// Detects whether an HttpRequestException indicates a key-related error
    /// (401 Unauthorized) that the user should fix in Settings.
    /// </summary>
    private static bool IsKeyError(HttpRequestException ex)
    {
        var msg = ex.Message;
        return msg.Contains("(401)") || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private void DispatchOverlayFlyTo(System.Windows.Point point, Rectangle displayBounds, string label)
    {
        if (_overlayFlyTo is not null)
        {
            _overlayFlyTo(point, displayBounds, label);
            return;
        }

        _overlayManager?.FlyTo(point, displayBounds, label);
    }

    private void DispatchOverlayFlyToSequence(IReadOnlyList<OverlayPointTarget> targets)
    {
        if (_overlayFlyTo is not null)
        {
            foreach (var target in targets)
            {
                _overlayFlyTo(target.ScreenPoint, target.DisplayBounds, target.Label);
            }
            return;
        }

        _overlayManager?.FlyToSequence(targets);
    }

    /// <summary>
    /// Strips [POINT:...] tags from Claude's response so TTS speaks clean text.
    /// Delegates to <see cref="PointTagParser.Parse"/> for consistency.
    /// </summary>
    internal static string StripPointTags(string text)
    {
        return PointTagParser.Parse(text).SpokenText;
    }

    /// <summary>
    /// Developer diagnostic from the tray menu: times one text-only LLM request,
    /// one screenshot LLM request, and one TTS request against the current config.
    /// </summary>
    public async Task RunProviderTimingDiagnosticsAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var timing = TurnTimingRecord.Start(_llmProvider, _llmModel);
        timing.Mark("diagnostic-start");

        try
        {
            var textChars = 0;
            timing.Mark("diagnostic-text-llm-start");
            await foreach (var delta in _llmClient.SendAsync(
                Array.Empty<Message>(),
                Array.Empty<CapturedScreen>(),
                KnowledgeContextProvider.BuildPrompt(CompanionSystemPrompt),
                "timing diagnostic: reply with one short sentence.",
                ct).ConfigureAwait(false))
            {
                textChars += delta.Length;
            }
            timing.Mark("diagnostic-text-llm-end", $"responseChars={textChars}");

            timing.Mark("capture-start", "source=diagnostic");
            var screens = await CaptureScreensForLlmAsync(_overlayManager?.OverlayHwnds, ct).ConfigureAwait(false);
            timing.Mark("capture-end", TurnTimingRecord.FormatScreenEvidence(screens, gridAnnotated: false, cursorScreenFiltering: false));

            var cursorScreen = screens.FirstOrDefault(s => s.IsCursorScreen);
            if (cursorScreen is not null)
            {
                screens = new List<CapturedScreen>
                {
                    new()
                    {
                        ImageBytes = cursorScreen.ImageBytes,
                        Label = ScreenCapture.BuildLabel(1, 0, true),
                        IsCursorScreen = cursorScreen.IsCursorScreen,
                        DisplayBounds = cursorScreen.DisplayBounds,
                        ScreenshotPixelWidth = cursorScreen.ScreenshotPixelWidth,
                        ScreenshotPixelHeight = cursorScreen.ScreenshotPixelHeight,
                    }
                };
                timing.Mark("capture-filter", TurnTimingRecord.FormatScreenEvidence(screens, gridAnnotated: false, cursorScreenFiltering: true));
            }

            var labeledScreens = screens.Select(s => new CapturedScreen
            {
                ImageBytes = s.ImageBytes,
                Label = $"{s.Label} (image dimensions: {s.ScreenshotPixelWidth}x{s.ScreenshotPixelHeight} pixels)",
                IsCursorScreen = s.IsCursorScreen,
                DisplayBounds = s.DisplayBounds,
                ScreenshotPixelWidth = s.ScreenshotPixelWidth,
                ScreenshotPixelHeight = s.ScreenshotPixelHeight,
            }).ToList();

            var imageChars = 0;
            timing.Mark("diagnostic-image-llm-start", $"screens={labeledScreens.Count}");
            await foreach (var delta in _llmClient.SendAsync(
                Array.Empty<Message>(),
                labeledScreens,
                KnowledgeContextProvider.BuildPrompt(CompanionSystemPrompt),
                "timing diagnostic: describe the screen in one short sentence and end with [POINT:none].",
                ct).ConfigureAwait(false))
            {
                imageChars += delta.Length;
            }
            timing.Mark("diagnostic-image-llm-end", $"responseChars={imageChars}");

            timing.Mark("diagnostic-tts-start", "sentenceChars=25");
            await _ttsClient.SpeakAsync("clicky timing diagnostic.", ct).ConfigureAwait(false);
            timing.Mark("diagnostic-tts-end");
            timing.Mark("diagnostic-complete");
        }
        catch (OperationCanceledException)
        {
            timing.Mark("diagnostic-canceled");
        }
        catch (Exception ex)
        {
            timing.Mark("diagnostic-error", $"type={ex.GetType().Name}");
            DebugLog.Write("Provider timing diagnostic failed", ex);
        }
    }

    private static void SavePointDebugArtifact(PointDirective directive, PointConversionResult? converted)
    {
        if (converted is null)
        {
            DebugLog.Write("[POINT] artifact: skipped because conversion returned null");
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Clicky",
                "point-debug");
            Directory.CreateDirectory(dir);

            string stamp;
            lock (PointDebugArtifactLock)
            {
                stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            }

            var baseName = $"{stamp}-{SanitizeFileName(directive.Label)}";
            var rawPath = Path.Combine(dir, $"{baseName}-raw.jpg");
            var annotatedPath = Path.Combine(dir, $"{baseName}-annotated.png");
            var metadataPath = Path.Combine(dir, $"{baseName}-metadata.txt");

            File.WriteAllBytes(rawPath, converted.TargetScreen.ImageBytes);

            using var ms = new MemoryStream(converted.TargetScreen.ImageBytes);
            using var bitmap = new Bitmap(ms);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var targetX = Math.Clamp(directive.X, 0, bitmap.Width - 1);
            var targetY = Math.Clamp(directive.Y, 0, bitmap.Height - 1);
            var radius = 12;

            using var redPen = new Pen(Color.FromArgb(255, 230, 57, 70), 3f);
            using var whitePen = new Pen(Color.FromArgb(255, 255, 255, 255), 1.5f);
            using var redBrush = new SolidBrush(Color.FromArgb(255, 230, 57, 70));
            using var blackBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            using var whiteBrush = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);

            graphics.DrawEllipse(redPen, targetX - radius, targetY - radius, radius * 2, radius * 2);
            graphics.DrawLine(redPen, targetX - 18, targetY, targetX + 18, targetY);
            graphics.DrawLine(redPen, targetX, targetY - 18, targetX, targetY + 18);
            graphics.FillEllipse(redBrush, targetX - 3, targetY - 3, 6, 6);
            graphics.DrawEllipse(whitePen, targetX - radius - 2, targetY - radius - 2, (radius + 2) * 2, (radius + 2) * 2);

            var text = $"POINT {directive.X},{directive.Y}  label={directive.Label}\n" +
                       $"screenshot={converted.TargetScreen.ScreenshotPixelWidth}x{converted.TargetScreen.ScreenshotPixelHeight}\n" +
                       $"displayBounds=({converted.DisplayBounds.X},{converted.DisplayBounds.Y},{converted.DisplayBounds.Width},{converted.DisplayBounds.Height})\n" +
                       $"screenPoint=({converted.ScreenPoint.X:F1},{converted.ScreenPoint.Y:F1})";

            var textOrigin = new PointF(14, 14);
            var textSize = graphics.MeasureString(text, font);
            graphics.FillRectangle(blackBrush, textOrigin.X - 6, textOrigin.Y - 6, textSize.Width + 12, textSize.Height + 12);
            graphics.DrawString(text, font, whiteBrush, textOrigin);

            bitmap.Save(annotatedPath, ImageFormat.Png);

            var metadata =
                "This raw image is the exact screenshot image sent to the model for the selected screen." + Environment.NewLine +
                $"returnedPoint=({directive.X},{directive.Y})" + Environment.NewLine +
                $"returnedLabel=\"{directive.Label}\"" + Environment.NewLine +
                $"targetScreenLabel=\"{converted.TargetScreen.Label}\"" + Environment.NewLine +
                $"screenshotPixels={converted.TargetScreen.ScreenshotPixelWidth}x{converted.TargetScreen.ScreenshotPixelHeight}" + Environment.NewLine +
                $"displayBounds=({converted.DisplayBounds.X},{converted.DisplayBounds.Y},{converted.DisplayBounds.Width},{converted.DisplayBounds.Height})" + Environment.NewLine +
                $"clampedPoint=({converted.ClampedX},{converted.ClampedY})" + Environment.NewLine +
                $"screenPoint=({converted.ScreenPoint.X:F1},{converted.ScreenPoint.Y:F1})" + Environment.NewLine +
                $"rawImage={rawPath}" + Environment.NewLine +
                $"annotatedImage={annotatedPath}" + Environment.NewLine;
            File.WriteAllText(metadataPath, metadata);

            DebugLog.Write($"[POINT] artifact: exactModelImage={rawPath} annotated={annotatedPath} metadata={metadataPath} returned=({directive.X},{directive.Y}) label=\"{directive.Label}\"");
        }
        catch (Exception ex)
        {
            DebugLog.Write("[POINT] artifact save failed", ex);
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "point" : sanitized;
    }

    /// <summary>
    /// Exercises the full overlay pipeline without needing a real LLM response:
    /// captures all screens, builds a fake [POINT:centerX,centerY:test target] tag
    /// using the primary screen's center, runs it through Parse â†’ ConvertToScreenCoordinates â†’ FlyTo,
    /// and logs each step via DebugLog.
    /// </summary>
    public async Task TestOverlayAsync()
    {
        if (_overlayManager is null)
        {
            DebugLog.Write("[TEST] TestOverlayAsync: no overlay manager â€” skipping");
            return;
        }

        try
        {
            // Step 1: capture all screens (excluding overlay HWNDs)
            DebugLog.Write("[TEST] TestOverlayAsync: capturing screens...");
            var excludeHwnds = _overlayManager.OverlayHwnds;
            var screens = await ScreenCapture.CaptureAllScreensAsJpegAsync(excludeHwnds, drawGrid: false).ConfigureAwait(false);

            if (screens.Count == 0)
            {
                DebugLog.Write("[TEST] TestOverlayAsync: no screens captured â€” aborting");
                return;
            }

            foreach (var s in screens)
            {
                DebugLog.Write($"[TEST] capture: {s.Label} bounds=({s.DisplayBounds.X},{s.DisplayBounds.Y},{s.DisplayBounds.Width},{s.DisplayBounds.Height}) " +
                    $"screenshot={s.ScreenshotPixelWidth}x{s.ScreenshotPixelHeight} cursor={s.IsCursorScreen}");
            }

            // Step 2: build a fake POINT tag targeting the center of the primary (cursor) screen
            var primary = screens.FirstOrDefault(s => s.IsCursorScreen) ?? screens[0];
            var centerX = primary.ScreenshotPixelWidth / 2;
            var centerY = primary.ScreenshotPixelHeight / 2;
            var fakeResponse = $"[POINT:{centerX},{centerY}:test target]";
            DebugLog.Write($"[TEST] fake response: {fakeResponse}");

            // Step 3: parse
            var parseResult = PointTagParser.Parse(fakeResponse);
            if (parseResult.Directive is not { } directive)
            {
                DebugLog.Write("[TEST] parse: no directive found â€” aborting");
                return;
            }
            DebugLog.Write($"[TEST] parse: directive x={directive.X} y={directive.Y} label=\"{directive.Label}\" screen={directive.ScreenNumber?.ToString() ?? "null"}");

            // Step 4: convert to screen coordinates
            var converted = PointTagParser.ConvertToScreenCoordinates(directive, screens);
            if (!converted.HasValue)
            {
                DebugLog.Write("[TEST] convert: returned null â€” aborting");
                return;
            }

            var (screenPoint, displayBounds) = converted.Value;
            DebugLog.Write($"[TEST] convert: screenPoint=({screenPoint.X:F1},{screenPoint.Y:F1}) displayBounds=({displayBounds.X},{displayBounds.Y},{displayBounds.Width},{displayBounds.Height})");

            // Step 5: fly to the point on the UI thread
            await _dispatcher.InvokeAsync(() =>
            {
                _overlayManager.FlyTo(new System.Windows.Point(screenPoint.X, screenPoint.Y), displayBounds, directive.Label);
            });
            DebugLog.Write($"[TEST] flyto: dispatched screenPoint=({screenPoint.X:F1},{screenPoint.Y:F1}) bubble=\"{directive.Label}\"");
        }
        catch (Exception ex)
        {
            DebugLog.Write("[TEST] TestOverlayAsync failed", ex);
        }
    }

    /// <summary>
    /// Local desktop smoke path: capture the live desktop, synthesize a POINT tag
    /// for a known physical target, run the normal parse/convert path, save
    /// artifacts, and dispatch the overlay.
    /// </summary>
    public async Task TestPointingAtScreenPointAsync(System.Windows.Point targetScreenPoint, string label)
    {
        if (_overlayManager is null)
        {
            DebugLog.Write("[SMOKE] TestPointingAtScreenPointAsync: no overlay manager - skipping");
            return;
        }

        try
        {
            DebugLog.Write($"[SMOKE] start: label=\"{label}\" targetPhysical=({targetScreenPoint.X:F1},{targetScreenPoint.Y:F1})");
            var screens = await ScreenCapture.CaptureAllScreensAsJpegAsync(_overlayManager.OverlayHwnds, drawGrid: false).ConfigureAwait(false);
            foreach (var screen in screens)
            {
                DebugLog.Write(
                    $"[SMOKE] capture: {screen.Label} bounds=({screen.DisplayBounds.X},{screen.DisplayBounds.Y},{screen.DisplayBounds.Width},{screen.DisplayBounds.Height}) " +
                    $"screenshot={screen.ScreenshotPixelWidth}x{screen.ScreenshotPixelHeight} cursor={screen.IsCursorScreen}");
            }

            var directive = BuildPointDirectiveForScreenPoint(targetScreenPoint, label, screens);
            if (directive is null)
            {
                DebugLog.Write("[SMOKE] directive: target point is outside all captured screens - aborting");
                return;
            }

            var fakeResponse = $"[POINT:{directive.X},{directive.Y}:{directive.Label}:screen{directive.ScreenNumber}]";
            DebugLog.Write($"[SMOKE] fake response: {fakeResponse}");

            var parseResult = PointTagParser.Parse(fakeResponse);
            if (parseResult.Directive is not { } parsedDirective)
            {
                DebugLog.Write("[SMOKE] parse: no directive found - aborting");
                return;
            }

            var converted = PointTagParser.ConvertToScreenCoordinatesDetailed(parsedDirective, screens);
            SavePointDebugArtifact(parsedDirective, converted);
            if (converted is null)
            {
                DebugLog.Write("[SMOKE] convert: returned null - aborting");
                return;
            }

            DebugLog.Write(
                $"[SMOKE] convert: targetLabel=\"{converted.TargetScreen.Label}\" directive=({parsedDirective.X},{parsedDirective.Y}) " +
                $"screenPoint=({converted.ScreenPoint.X:F1},{converted.ScreenPoint.Y:F1}) " +
                $"displayBounds=({converted.DisplayBounds.X},{converted.DisplayBounds.Y},{converted.DisplayBounds.Width},{converted.DisplayBounds.Height})");

            await _dispatcher.InvokeAsync(() =>
            {
                _overlayManager.FlyTo(new System.Windows.Point(converted.ScreenPoint.X, converted.ScreenPoint.Y), converted.DisplayBounds, parsedDirective.Label);
            });
            DebugLog.Write($"[SMOKE] flyto: dispatched screenPoint=({converted.ScreenPoint.X:F1},{converted.ScreenPoint.Y:F1}) bubble=\"{parsedDirective.Label}\"");
        }
        catch (Exception ex)
        {
            DebugLog.Write("[SMOKE] TestPointingAtScreenPointAsync failed", ex);
        }
    }

    internal static PointDirective? BuildPointDirectiveForScreenPoint(
        System.Windows.Point targetScreenPoint,
        string label,
        IReadOnlyList<CapturedScreen> screens)
    {
        for (var i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            var bounds = screen.DisplayBounds;
            if (targetScreenPoint.X < bounds.Left ||
                targetScreenPoint.X >= bounds.Right ||
                targetScreenPoint.Y < bounds.Top ||
                targetScreenPoint.Y >= bounds.Bottom)
            {
                continue;
            }

            var scaleX = (double)screen.ScreenshotPixelWidth / bounds.Width;
            var scaleY = (double)screen.ScreenshotPixelHeight / bounds.Height;
            var x = (int)Math.Round((targetScreenPoint.X - bounds.X) * scaleX);
            var y = (int)Math.Round((targetScreenPoint.Y - bounds.Y) * scaleY);

            return new PointDirective
            {
                X = Math.Clamp(x, 0, screen.ScreenshotPixelWidth - 1),
                Y = Math.Clamp(y, 0, screen.ScreenshotPixelHeight - 1),
                Label = label,
                ScreenNumber = i + 1,
            };
        }

        return null;
    }

    /// <summary>
    /// Swaps the active LLM client at runtime without restarting the CompanionManager.
    /// If a response is in-flight, it is cancelled first. The next push-to-talk turn
    /// will use the new client. Safe to call from any VoiceState.
    /// </summary>
    public async Task SwapLlmClientAsync(ILlmClient newClient, string? provider = null, string? model = null)
    {
        // Cancel any in-flight response/TTS before swapping.
        CancelCurrentResponse();

        // Wait for the current response task to finish if it's running.
        var task = _currentResponseTask;
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
        }

        lock (_swapLock)
        {
            _llmClient = newClient;
            if (!string.IsNullOrWhiteSpace(provider))
                _llmProvider = provider;
            if (!string.IsNullOrWhiteSpace(model))
                _llmModel = model;
        }

        await _dispatcher.InvokeAsync(() => _viewModel.VoiceState = VoiceState.Idle);
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // 1. Cancel all token sources to signal shutdown.
        _hookConsumerCts?.Cancel();
        CancelCurrentResponse();
        _micCaptureCts?.Cancel();

        // 2. Await all tracked background tasks with a 2-second timeout.
        var tasks = new List<Task>();
        if (_hookConsumerTask is not null) tasks.Add(_hookConsumerTask);
        if (_currentResponseTask is not null) tasks.Add(_currentResponseTask);
        if (_recordingTask is not null) tasks.Add(_recordingTask);
        if (_stopRecordingTask is not null) tasks.Add(_stopRecordingTask);

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Tasks didn't finish within 2 seconds â€” proceed with cleanup anyway.
            }
            catch (OperationCanceledException)
            {
                // Expected â€” tasks were cancelled.
            }
            catch
            {
                // Swallow any other exceptions during shutdown.
            }
        }

        // 3. Synchronous cleanup.
        _activeMicCapture?.Dispose();
        _activeTranscriptionSession?.Dispose();
        _ttsClient.Dispose();
        _hookConsumerCts?.Dispose();
        _micCaptureCts?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Synchronous fallback â€” cancels everything but cannot await tasks.
        _hookConsumerCts?.Cancel();
        CancelCurrentResponse();
        _micCaptureCts?.Cancel();

        _activeMicCapture?.Dispose();
        _activeTranscriptionSession?.Dispose();
        _ttsClient.Dispose();
        _hookConsumerCts?.Dispose();
        _micCaptureCts?.Dispose();
    }
}
