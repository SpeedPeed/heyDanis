using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using HeyDanis.Api;
using HeyDanis.Audio;
using HeyDanis.Capture;
using HeyDanis.Hotkey;
using HeyDanis.Overlay;
using HeyDanis.Pointing;

namespace HeyDanis.Companion;

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
    private readonly ISpeechTranscriber _transcriber;
    private readonly ITextToSpeechClient _ttsClient;
    private readonly Dispatcher _dispatcher;
    private readonly OverlayWindowManager? _overlayManager;
    private readonly string? _microphoneDeviceId;
    private readonly Func<IReadOnlyList<IntPtr>?, CancellationToken, bool, Task<List<CapturedScreen>>> _captureScreensAsync;
    private readonly Action<System.Windows.Point, Rectangle, string>? _overlayFlyTo;
    private readonly Func<Task>? _prepareForCaptureAsync;
    private readonly bool _useRedesignedPointingProtocol;
    private string _llmProvider;
    private bool _hasOpenedSettingsForKeyError;
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
    private ISpeechTranscriptionSession? _activeTranscriptionSession;
    private CancellationTokenSource? _micCaptureCts;

    // Capture-on-press: kick off screen capture when the key is first pressed so
    // the capture completes in parallel with the user speaking. The task is awaited
    // when the LLM request is built (after key release). Cancelled if the press
    // is shorter than ~100 ms (accidental tap).
    private Task<List<CapturedScreen>>? _captureOnPressTask;
    private CancellationTokenSource? _captureOnPressCts;
    private DateTime _pressTimestamp;

    /// <summary>
    /// Shared personality for every prompt: German, casual and short, written for the ear.
    /// The user is an HTL student (Austrian technical high school) who mostly asks about
    /// school, programming, CAD and game development.
    /// </summary>
    private const string BuddyPersonality = """
        du bist heydanis, ein lockerer ki-buddy, der in der windows-taskleiste wohnt. der nutzer ist htl-schüler und fragt dich meistens zu schule, programmieren, cad und spieleentwicklung. du siehst seine bildschirme und deine antwort wird laut vorgelesen.

        stil:
        - antworte auf deutsch. fachbegriffe bleiben englisch (zum beispiel "for-loop", "commit", "sketch", "collider").
        - locker und kurz, wie ein guter kumpel, der sich auskennt. duzen. standardmäßig ein bis drei kurze sätze. nur wenn er ausdrücklich mehr wissen will ("erklär genauer", "warum"), gehst du richtig ins detail.
        - schreib fürs ohr: keine listen, kein markdown, keine emojis, keine sonderzeichen. zahlen bis zwölf ausschreiben. "zum beispiel" statt "z. b.".
        - code nie vorlesen. sag stattdessen, was zu tun ist ("schreib in zeile zwölf ein semikolon dazu").
        - wenn es um etwas auf dem bildschirm geht, bezieh dich konkret darauf. wenn der screenshot nichts damit zu tun hat, ignorier ihn.
        - bei hausübungen und lernstoff: hilf beim verstehen und gib gezielte hinweise. die komplette lösung nur, wenn er danach fragt.
        - keine füllfragen wie "soll ich dir mehr erklären?". wenn es passt, ende mit einem kurzen tipp für den nächsten schritt.
        - wenn du dir nicht sicher bist, sag das ehrlich statt zu raten.
        - wenn mehrere bildschirme mitgeschickt werden, ist der mit "primary focus" der, auf dem gerade die maus ist.
        """;

    /// <summary>Legacy free-text protocol with a trailing [POINT:x,y:label:screenN] tag.</summary>
    public static readonly string CompanionSystemPrompt = BuddyPersonality + """

        zeigen:
        du hast einen kleinen orangen cursor, der zu dingen auf dem bildschirm fliegen kann. nutze ihn, wenn er wirklich hilft: ein menü finden, einen button suchen, sich in einem programm zurechtfinden. bei allgemeinen wissensfragen zeigst du nicht.

        wenn du zeigst, häng ganz am ende deiner antwort ein tag an: [POINT:x,y:label]. x und y sind pixelkoordinaten im screenshot (die bildgröße steht im bild-label), ursprung oben links. label sind ein bis drei wörter, zum beispiel "speichern button". liegt das ziel auf einem anderen bildschirm als die maus, häng :screenN an, zum beispiel [POINT:400,300:terminal:screen2]. wenn zeigen nicht hilft: [POINT:none].

        beispiele:
        - "klick oben rechts auf das zahnrad, da sind die einstellungen. [POINT:1840,24:einstellungen]"
        - "html ist das grundgerüst jeder webseite, css macht es dann hübsch. [POINT:none]"
        """;

    /// <summary>Default protocol: one JSON object with spoken text and up to five point intents.</summary>
    public static readonly string StructuredPointingSystemPrompt = BuddyPersonality + """

        antwortformat: gib genau ein json-objekt zurück, kein markdown, kein text davor oder danach.

        schema:
        {"spokenText":"kurzer gesprochener text","pointIntents":[{"kind":"point","x":0,"y":0,"screen":1,"label":"erstes ziel","confidence":"high"},{"kind":"point","x":0,"y":0,"screen":1,"label":"zweites ziel","confidence":"high"}]}
        oder:
        {"spokenText":"kurzer gesprochener text","pointIntents":[{"kind":"none","reason":"not_visible"}]}

        regeln fürs zeigen:
        - zeig nur, wenn ein zeiger wirklich hilft: zeigen, finden, klicken, wo ist, navigation in einem programm. das ziel muss sichtbar und eindeutig sein.
        - für schritt-für-schritt-anleitungen ("wie exportiere ich das") gib die schritte als mehrere punkte in der richtigen reihenfolge zurück, maximal fünf. spokenText nennt die schritte in derselben reihenfolge, ein kurzer satz pro schritt.
        - sonst genau ein punkt.
        - koordinaten sind screenshot-pixel, ursprung oben links im gewählten bild. x nach rechts, y nach unten. screen ist die nummer aus dem bild-label.
        - bei landkarten: wähle einen punkt innerhalb des gesuchten landes, nie beschriftungen oder nachbarländer.
        - wenn du unsicher bist, nimm kind "none". lieber nicht zeigen als falsch zeigen.
        - confidence muss "high" sein, sonst kind "none".
        - label kurz und konkret auf deutsch, es steht neben dem zeiger.
        """;

    /// <summary>Gamer mode: draw circles and arrows over the game instead of moving the pointer.</summary>
    public static readonly string GamerModeStructuredPointingSystemPrompt = BuddyPersonality + """

        gamer-modus: der nutzer spielt gerade. halte dich extra kurz.

        antwortformat: gib genau ein json-objekt zurück, kein markdown.

        schema:
        {"spokenText":"kurzer gesprochener text","pointIntents":[{"kind":"circle","x":0,"y":0,"radius":48,"screen":1,"label":"ziel","confidence":"high"},{"kind":"arrow","x1":0,"y1":0,"x2":120,"y2":80,"screen":1,"label":"richtung","confidence":"high"}]}
        oder:
        {"spokenText":"kurzer gesprochener text","pointIntents":[{"kind":"none","reason":"not_visible"}]}

        regeln:
        - du kannst kurzzeitig über dem spiel zeichnen. nimm kreise und pfeile statt des zeigers.
        - kreis: einheit, item, gegner, minimap-bereich, button oder sichtbare region hervorheben. x,y ist die mitte, radius in screenshot-pixeln.
        - pfeil: richtung, laufweg, drehung oder kamerabewegung zeigen. x1,y1 ist das ende, x2,y2 die spitze.
        - ein bis drei markierungen, außer er will mehr.
        - nur was sichtbar und eindeutig ist. bei unsicherheit kind "none".
        - confidence muss "high" sein.
        - spokenText erklärt die markierung kurz, ohne json oder koordinaten zu erwähnen.
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
        ISpeechTranscriber transcriber,
        ITextToSpeechClient ttsClient,
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
            case ShortcutTransition.Cancelled:
                HandleCancelled();
                break;
        }
    }

    /// <summary>
    /// The chord turned out to be part of a normal keyboard shortcut (e.g. Strg+Alt+L):
    /// throw the recording away without asking Claude and without an error message.
    /// </summary>
    private void HandleCancelled()
    {
        _viewModel.IsShortcutPressed = false;
        if (_viewModel.VoiceState != VoiceState.Listening)
            return;

        DebugLog.Write("[VOICE] shortcut cancelled: another key was pressed, discarding recording");
        _micCaptureCts?.Cancel();
        _activeMicCapture?.Dispose();
        _activeMicCapture = null;

        var session = _activeTranscriptionSession;
        _activeTranscriptionSession = null;
        if (session is not null)
        {
            _ = Task.Run(async () =>
            {
                try { await session.CancelAsync().ConfigureAwait(false); }
                catch { /* best effort */ }
                session.Dispose();
            });
        }

        ClearCaptureOnPress();
        _viewModel.VoiceState = VoiceState.Idle;
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

        // Hide the overlay cursor and the last answer bubble so they don't appear in the capture.
        if (_overlayManager is not null)
        {
            _dispatcher.Invoke(() =>
            {
                _overlayManager.SuspendCursorFollowing();
                _overlayManager.HideResponseText();
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
        catch (SpeechServiceNotReadyException notReady)
        {
            // Local Whisper model still downloading/loading — the message is user-facing.
            DebugLog.Write($"Transcription not ready: {notReady.Message}");
            await _dispatcher.InvokeAsync(() =>
            {
                _viewModel.LastError = notReady.Message;
                _viewModel.VoiceState = VoiceState.Idle;
            });
            return;
        }
        catch (Exception ex) when (IsTranscriptionAuthError(ex))
        {
            DebugLog.Write("Transcription auth error (AssemblyAI key missing or invalid)", ex);
            await _dispatcher.InvokeAsync(() =>
            {
                _viewModel.LastError = "Dein AssemblyAI-Key fehlt oder stimmt nicht. Öffne die Einstellungen, um ihn zu korrigieren.";
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
                _viewModel.LastError = $"Spracherkennung konnte nicht starten \u2014 prüf Internet bzw. Einstellungen. ({ex.Message})";
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
                _viewModel.LastError = $"Aufnahme konnte nicht starten \u2014 ist das Mikrofon angeschlossen und in den Windows-Einstellungen erlaubt? ({ex.Message})";
                _viewModel.VoiceState = VoiceState.Idle;
            });
        }
    }

    /// <summary>
    /// Detects whether an exception from <see cref="ISpeechTranscriber.StartSessionAsync"/>
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
                    _viewModel.LastError = "Ich hab nichts verstanden. Halte die Tasten gedrückt, während du sprichst, und lass dann los.";
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
                _viewModel.LastError = "Deine Sprache konnte nicht erkannt werden \u2014 probier es nochmal.";
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
            Exception? speakFailure = null;

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
                }, speakError =>
                {
                    DebugLog.Write("TTS playback failed", speakError);
                    speakFailure = speakError;
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

                    if (speakFailure is not null && !ct.IsCancellationRequested)
                    {
                        var failureMessage = speakFailure.Message;
                        await _dispatcher.InvokeAsync(() =>
                            _viewModel.LastError = $"Vorlesen hat nicht geklappt \u2014 prüf in den Einstellungen, ob die richtigen Lautsprecher gewählt sind. ({failureMessage})");
                    }
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

                await ShowAnswerBubbleAsync(spokenText).ConfigureAwait(false);

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
                    _viewModel.LastError = DescribeKeyError(httpEx);
                    // Open the settings only once per configuration — popping the window up on
                    // every single question is annoying and doesn't tell the user anything new.
                    if (!_hasOpenedSettingsForKeyError)
                    {
                        _hasOpenedSettingsForKeyError = true;
                        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
                    }
                });
            }
            catch (HttpRequestException httpEx) when (!IsKeyError(httpEx))
            {
                timing.Mark("turn-error", "kind=http");
                DebugLog.Write("Response pipeline HTTP error", httpEx);
                await _dispatcher.InvokeAsync(() =>
                {
                    _viewModel.LastError = DescribeHttpError(httpEx);
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not HttpRequestException)
            {
                timing.Mark("turn-error", $"kind=unhandled type={ex.GetType().Name}");
                DebugLog.Write("Response pipeline unhandled error", ex);
                await _dispatcher.InvokeAsync(() =>
                {
                    _viewModel.LastError = "Da ist was schiefgelaufen \u2014 probier es nochmal. Wenn es öfter passiert, schau in die Einstellungen.";
                });
            }

            if (speakFailure is not null && !ct.IsCancellationRequested)
            {
                // The answer is still visible in the bubble; tell the user why it wasn't read aloud.
                var failureMessage = speakFailure.Message;
                await _dispatcher.InvokeAsync(() =>
                    _viewModel.LastError = $"Vorlesen hat nicht geklappt \u2014 prüf in den Einstellungen, ob die richtigen Lautsprecher gewählt sind. ({failureMessage})");
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
        DebugLog.Write($"[VOICE] answer chars={cleaned.Length}");
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            await ShowAnswerBubbleAsync(cleaned).ConfigureAwait(false);
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

    /// <summary>Shows the answer as text next to the mouse (readable even without sound).</summary>
    private async Task ShowAnswerBubbleAsync(string answer)
    {
        if (_overlayManager is null || string.IsNullOrWhiteSpace(answer)) return;
        await _dispatcher.InvokeAsync(() => _overlayManager.ShowResponseText(answer));
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
            DebugLog.Write("[POINT] capture-prep: requesting heyDanis UI hide before screenshot");
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
    /// <summary>
    /// User-facing text for a rejected API key, including Anthropic's own reason
    /// (e.g. "invalid x-api-key") so the user knows what is actually wrong.
    /// </summary>
    internal static string DescribeKeyError(HttpRequestException ex)
    {
        var apiMessage = AnthropicDirectClient.ExtractApiErrorMessage(ex.Message);
        var reason = string.IsNullOrWhiteSpace(apiMessage) ? "" : $" (Anthropic sagt: „{apiMessage}“)";
        return "Anthropic lehnt deinen API-Key ab" + reason +
               ". Klick in den Einstellungen neben dem Key auf „Test“. Der Key muss mit sk-ant-api beginnen und von console.anthropic.com stammen.";
    }

    /// <summary>User-facing text for any other Claude API error, with the API's reason.</summary>
    internal static string DescribeHttpError(HttpRequestException ex)
    {
        var apiMessage = AnthropicDirectClient.ExtractApiErrorMessage(ex.Message);
        if (apiMessage is null)
            return "Keine Antwort von Claude \u2014 prüf deine Internetverbindung und probier es nochmal.";

        if (apiMessage.Contains("credit balance", StringComparison.OrdinalIgnoreCase))
            return "Dein Anthropic-Guthaben ist aufgebraucht \u2014 lade auf console.anthropic.com unter „Billing“ ein paar Dollar auf.";

        return $"Claude hat einen Fehler gemeldet: {apiMessage}";
    }

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
            await _ttsClient.SpeakAsync("heydanis zeitmessung.", ct).ConfigureAwait(false);
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
                "heyDanis",
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
