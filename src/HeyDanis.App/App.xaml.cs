using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using HeyDanis.Api;
using HeyDanis.Audio;
using HeyDanis.Capture;
using HeyDanis.Companion;
using HeyDanis.Hotkey;
using HeyDanis.LocalSpeech;
using HeyDanis.Overlay;

namespace HeyDanis.App;

public partial class App : Application
{
    internal const bool UseRedesignedPointingProtocolByDefault = true;

    private static Mutex? _singleInstanceMutex;
    private TrayIconManager? _trayIconManager;
    private MainWindow? _mainWindow;
    private CompanionPanelWindow? _companionPanel;
    private CompanionViewModel? _companionViewModel;
    private GlobalPushToTalkHook? _pushToTalkHook;
    private CompanionManager? _companionManager;
    private OverlayWindowManager? _overlayManager;
    private DispatcherTimer? _permissionPollTimer;
    private SettingsStore? _settingsStore;
    private SecretsStore? _secretsStore;
    private PointingSmokeWindow? _pointingSmokeWindow;

    // Local speech models are expensive to load, so the Whisper transcriber is kept
    // across settings changes as long as the user keeps the same model.
    private readonly LocalModelStore _localModelStore = new();
    private readonly UsageCostTracker _usageCostTracker = new();
    private WhisperLocalTranscriber? _whisperTranscriber;
    private string? _whisperTranscriberModelId;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard: if another HeyDanis is already running, exit immediately.
        _singleInstanceMutex = new Mutex(true, "Global\\heyDanisAppSingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "heyDanis is already running.",
                "heyDanis",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Initialize settings and secrets stores under %APPDATA%\HeyDanis.
        _settingsStore = new SettingsStore();
        _secretsStore = new SecretsStore();

        // Dev-mode: seed secrets from environment variables so developers
        // don't have to re-enter keys after every rebuild/wipe.
        SeedSecretsFromEnvironment(_secretsStore);

        // Migrate US-014 registry values to SettingsStore on first run.
        MigrateRegistrySettings(_settingsStore);

        // ViewModel that the panel binds to.
        _companionViewModel = new CompanionViewModel();

        // Load onboarding state from SettingsStore (migrated from registry above).
        _companionViewModel.HasCompletedOnboarding = _settingsStore.OnboardingComplete;
        _companionViewModel.GamerModeEnabled = _settingsStore.GamerModeEnabled;

        // Check if first-run setup is needed: required keys missing OR not onboarded.
        if (!HasRequiredKeys(_secretsStore, _settingsStore) || !_settingsStore.OnboardingComplete)
        {
            var vm = new SettingsViewModel(_secretsStore, _settingsStore);
            var settingsWindow = new SettingsWindow(vm, isFirstRun: true);
            bool saved = false;

            settingsWindow.SettingsSaved += (_, _) =>
            {
                saved = true;
                _settingsStore.OnboardingComplete = true;
                _companionViewModel.HasCompletedOnboarding = true;
            };

            // ShowDialog blocks until the window is closed.
            // If the user closes via X or Quit without saving, the app exits
            // (handled in SettingsWindow.OnClosing).
            settingsWindow.ShowDialog();

            if (!saved)
            {
                // The app is shutting down — don't continue initialization.
                return;
            }
        }

        // All required keys are present — proceed with full app initialization.
        InitializeApp();
    }

    /// <summary>
    /// Full app initialization: tray icon, overlay, companion manager, permissions, auto-update.
    /// Called after keys are confirmed to be present (either from prior launch or first-run setup).
    /// </summary>
    private void InitializeApp()
    {
        _mainWindow = new MainWindow(_companionViewModel!);
        _mainWindow.SettingsRequested += OnSettingsClicked;
        _mainWindow.GamerModeChanged += OnGamerModeChanged;
        MainWindow = _mainWindow;

        _companionPanel = new CompanionPanelWindow(_companionViewModel!);

        // Set up system tray icon with menu and left-click event.
        _trayIconManager = new TrayIconManager();
        _trayIconManager.TrayIconClicked += OnTrayIconClicked;
        _trayIconManager.SettingsClicked += OnSettingsClicked;
        _trayIconManager.ModelSelected += OnModelSelected;
        _trayIconManager.OverlayTestRequested += OnOverlayTestRequested;
        _trayIconManager.DesktopSmokeTestRequested += OnDesktopSmokeTestRequested;
        _trayIconManager.ProviderTimingDiagnosticsRequested += OnProviderTimingDiagnosticsRequested;

        // Register for auto-start on first launch (mirrors SMAppService.mainApp.register).
        AutoStartRegistration.EnsureRegistered();

        // Probe the Windows 10/11 microphone privacy gate in the background.
        _ = ProbeMicrophonePermissionAsync();

        // Probe screen capture availability.
        _ = ProbeScreenCapturePermissionAsync();

        // Start a permission polling timer (mirrors Mac's 1.5s timer) so the
        // panel updates in real time as the user grants permissions in Settings.
        _permissionPollTimer = new DispatcherTimer
        {
            Interval = System.TimeSpan.FromSeconds(1.5),
        };
        _permissionPollTimer.Tick += OnPermissionPollTick;
        _permissionPollTimer.Start();

        // Open the real application window on launch. The tray icon remains as
        // a convenience for reopening the app after the window is hidden.
        Dispatcher.InvokeAsync(() =>
        {
            _mainWindow!.ShowApplicationWindow();
        }, DispatcherPriority.Loaded);

        // Install the global push-to-talk hook on the dispatcher thread.
        _pushToTalkHook = new GlobalPushToTalkHook(_companionViewModel!.PushToTalkShortcut);
        _pushToTalkHook.Start();

        // Create transparent overlay windows for each monitor (US-011).
        _overlayManager = new OverlayWindowManager();
        _overlayManager.Logger = DebugLog.Write;
        _overlayManager.Start();
        _overlayManager.GamerModeEnabled = _settingsStore!.GamerModeEnabled;

        // Build and start the CompanionManager with current keys/config.
        BuildAndStartCompanionManager();

        // Errors are shown in the bubble next to the mouse too: the main window is hidden
        // while heyDanis takes screenshots, so a banner there would go unnoticed.
        _companionViewModel!.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CompanionViewModel.LastError)
                && !string.IsNullOrWhiteSpace(_companionViewModel.LastError))
            {
                _overlayManager?.ShowResponseText("Hinweis: " + _companionViewModel.LastError);
            }
        };

        // Show this month's Claude costs and keep them updated after every answer.
        _companionViewModel!.MonthlyCostDisplay = _usageCostTracker.FormatCurrentMonth();
        _usageCostTracker.Changed += (_, _) => Dispatcher.InvokeAsync(() =>
            _companionViewModel!.MonthlyCostDisplay = _usageCostTracker.FormatCurrentMonth());

        // Set the initial model display name on the ViewModel.
        _companionViewModel!.LlmModelDisplay = GetModelDisplayName(
            _settingsStore!.LlmProvider, _settingsStore.LlmModel);

        // Populate the tray Model submenu.
        RefreshModelMenu();
    }

    /// <summary>
    /// Constructs an ILlmClient + audio clients from the current SecretsStore/SettingsStore
    /// state, creates a CompanionManager, wires up error handling, and starts it.
    /// </summary>
    private void BuildAndStartCompanionManager()
    {
        // Dispose old manager if rebuilding after a settings change.
        if (_companionManager is not null)
        {
            _companionManager.OpenSettingsRequested -= OnPipelineKeyError;
            _companionManager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            _companionManager = null;
        }

        var llmClient = BuildLlmClient();
        var transcriber = BuildTranscriber();
        var ttsClient = BuildTtsClient();

        _companionManager = new CompanionManager(
            _companionViewModel!,
            _pushToTalkHook!,
            llmClient,
            transcriber,
            ttsClient,
            Dispatcher,
            _overlayManager,
            microphoneDeviceId: _settingsStore!.MicrophoneDeviceId,
            prepareForCaptureAsync: HideHeyDanisUiForCaptureAsync,
            llmProvider: _settingsStore.LlmProvider,
            llmModel: _settingsStore.LlmModel,
            useRedesignedPointingProtocol: UseRedesignedPointingProtocolByDefault);
        _companionManager.OpenSettingsRequested += OnPipelineKeyError;
        _companionManager.Start();
    }

    private async Task HideHeyDanisUiForCaptureAsync()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            var hidAny = false;

            if (_companionPanel?.Visibility == Visibility.Visible)
            {
                _companionPanel.Hide();
                hidAny = true;
            }

            if (_mainWindow?.Visibility == Visibility.Visible)
            {
                _mainWindow.Hide();
                hidAny = true;
            }

            _trayIconManager?.HideOpenPopups();

            if (hidAny)
            {
                DebugLog.Write("[POINT] capture-prep: hid visible heyDanis companion panel before screenshot");
            }
        });
    }

    private ILlmClient BuildLlmClient()
    {
        NormalizeAnthropicModelSettings();
        var apiKey = _secretsStore!.Read(SecretsStore.AnthropicApiKey) ?? "";
        return new AnthropicDirectClient(
            apiKey,
            model: _settingsStore!.LlmModel,
            onUsageReported: _usageCostTracker.Record);
    }

    private ISpeechTranscriber BuildTranscriber()
    {
        var choice = SettingsViewModel.NormalizeChoice(
            _settingsStore!.SpeechRecognitionChoice,
            SettingsViewModel.SpeechRecognitionChoices,
            SettingsViewModel.DefaultSpeechRecognitionId);

        if (choice == SettingsViewModel.AssemblyAiSpeechRecognitionId)
        {
            _companionViewModel!.SpeechRecognitionStatus = "AssemblyAI (Cloud)";
            var key = _secretsStore!.Read(SecretsStore.AssemblyAiApiKey) ?? "";
            return new AssemblyAiStreamingTranscriber(key);
        }

        var modelChoice = WhisperModelChoice.FromId(choice.Replace("whisper:", ""));
        if (_whisperTranscriber is null || _whisperTranscriberModelId != modelChoice.Id)
        {
            _whisperTranscriber?.Dispose();
            _whisperTranscriber = new WhisperLocalTranscriber(_localModelStore, modelChoice);
            _whisperTranscriberModelId = modelChoice.Id;
        }

        var transcriber = _whisperTranscriber;
        var modelName = modelChoice.DisplayName.Split(" (")[0];
        if (!transcriber.IsReady)
        {
            _companionViewModel!.SpeechRecognitionStatus = $"{modelName} – wird geladen …";
            var progress = new Progress<ModelDownloadProgress>(report =>
                _companionViewModel!.SpeechRecognitionStatus = report.Percent is { } percent
                    ? $"{modelName} – Download {percent} %"
                    : $"{modelName} – wird geladen …");
            _ = WarmUpLocalModelAsync(
                () => transcriber.EnsureLoadedAsync(progress),
                status => _companionViewModel!.SpeechRecognitionStatus = status,
                $"{modelName} – bereit (lokal)",
                "Spracherkennung");
        }
        else
        {
            _companionViewModel!.SpeechRecognitionStatus = $"{modelName} – bereit (lokal)";
        }
        return transcriber;
    }

    private ITextToSpeechClient BuildTtsClient()
    {
        var choice = SettingsViewModel.NormalizeChoice(
            _settingsStore!.VoiceChoice,
            SettingsViewModel.VoiceChoices,
            SettingsViewModel.DefaultVoiceId);
        var outputDeviceId = _settingsStore.SpeakerDeviceId;
        var speakingRate = _settingsStore.SpeakingRate;

        if (choice == SettingsViewModel.ElevenLabsVoiceChoiceId)
        {
            _companionViewModel!.VoiceStatus = "ElevenLabs (Cloud)";
            var key = _secretsStore!.Read(SecretsStore.ElevenLabsApiKey) ?? "";
            return new ElevenLabsTtsClient(key, _settingsStore.ElevenLabsVoiceId, outputDeviceId);
        }

        var windowsVoice = new WindowsVoiceSpeechClient(outputDeviceId, speakingRate) { Log = DebugLog.Write };
        DebugLog.Write($"[TTS] Stimme={choice} Gerät={(string.IsNullOrEmpty(outputDeviceId) ? "Windows-Standard" : outputDeviceId)} Windows-Stimme={windowsVoice.SelectedVoiceName}");
        if (choice == "windows")
        {
            _companionViewModel!.VoiceStatus = $"Windows-Stimme: {windowsVoice.SelectedVoiceName}";
            return windowsVoice;
        }

        var voiceChoice = PiperVoiceChoice.FromId(choice.Replace("piper:", ""));
        var piper = new PiperLocalSpeechClient(_localModelStore, voiceChoice, (float)speakingRate, outputDeviceId) { Log = DebugLog.Write };
        var voiceName = voiceChoice.DisplayName.Split(" (")[0];
        _companionViewModel!.VoiceStatus = $"{voiceName} – wird geladen (solange Windows-Stimme)";
        var progress = new Progress<ModelDownloadProgress>(report =>
            _companionViewModel!.VoiceStatus = report.Percent is { } percent
                ? $"{voiceName} – Download {percent} % (solange Windows-Stimme)"
                : $"{voiceName} – wird geladen (solange Windows-Stimme)");
        _ = WarmUpLocalModelAsync(
            () => piper.EnsureLoadedAsync(progress),
            status => _companionViewModel!.VoiceStatus = status,
            $"{voiceName} – bereit (lokal)",
            "Stimme");
        return new FallbackSpeechClient(piper, windowsVoice, DebugLog.Write);
    }

    /// <summary>
    /// Used by the "Stimme testen" button: speaks a short sentence with the voice and
    /// speaker currently selected in the settings window (even before saving) and
    /// returns a German result text — the error message if it didn't work.
    /// </summary>
    internal async Task<string> TestVoiceAsync(string voiceChoiceId, string outputDeviceId, double speakingRate)
    {
        const string testSentence = "Hallo! So klinge ich. Wenn du das hörst, passt alles.";
        ITextToSpeechClient? client = null;
        try
        {
            if (voiceChoiceId == SettingsViewModel.ElevenLabsVoiceChoiceId)
                return "ElevenLabs kann erst nach dem Speichern getestet werden.";

            if (voiceChoiceId == "windows")
            {
                var windowsVoice = new WindowsVoiceSpeechClient(outputDeviceId, speakingRate) { Log = DebugLog.Write };
                client = windowsVoice;
                await windowsVoice.SpeakAsync(testSentence);
                return $"✓ Fertig gesprochen (Windows-Stimme: {windowsVoice.SelectedVoiceName}). Nichts gehört? Lautstärke und Ausgabegerät prüfen.";
            }

            var voiceChoice = PiperVoiceChoice.FromId(voiceChoiceId.Replace("piper:", ""));
            var piper = new PiperLocalSpeechClient(_localModelStore, voiceChoice, (float)speakingRate, outputDeviceId) { Log = DebugLog.Write };
            client = piper;
            await piper.EnsureLoadedAsync();
            await piper.SpeakAsync(testSentence);
            return "✓ Fertig gesprochen (Piper). Nichts gehört? Lautstärke und Ausgabegerät prüfen.";
        }
        catch (Exception ex)
        {
            DebugLog.Write("[TTS] Stimmtest fehlgeschlagen", ex);
            return $"✗ Fehler: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// Downloads/loads a local model in the background and reports the result in the
    /// panel. Failures (usually no internet on first start) are shown, not thrown —
    /// the model is retried automatically the next time the user talks.
    /// </summary>
    private async Task WarmUpLocalModelAsync(
        Func<Task> loadAsync,
        Action<string> setStatus,
        string readyStatus,
        string modelKindForErrors)
    {
        try
        {
            await loadAsync();
            await Dispatcher.InvokeAsync(() => setStatus(readyStatus));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[LOCAL] {modelKindForErrors} konnte nicht geladen werden", ex);
            await Dispatcher.InvokeAsync(() =>
                setStatus($"{modelKindForErrors}: Laden fehlgeschlagen – Internet prüfen, wird beim nächsten Sprechen erneut versucht"));
        }
    }

    /// <summary>
    /// Builds the list of (provider, model) entries for the tray Model submenu.
    /// Items whose required API key is missing are disabled with a tooltip.
    /// </summary>
    internal List<ModelMenuEntry> BuildModelMenuEntries()
    {
        return new List<ModelMenuEntry>
        {
            new() { Provider = "anthropic", Model = "claude-haiku-5-5", DisplayName = "Claude Haiku 5.5 (günstig)", IsEnabled = true },
            new() { Provider = "anthropic", Model = "claude-sonnet-5-5", DisplayName = "Claude Sonnet 5.5 (genauer)", IsEnabled = true },
        };
    }

    /// <summary>
    /// Returns a user-friendly display name for the current (provider, model) pair.
    /// </summary>
    internal static string GetModelDisplayName(string provider, string model)
    {
        return (provider, model) switch
        {
            ("anthropic", "claude-haiku-5-5") => "Claude Haiku 5.5",
            ("anthropic", "claude-sonnet-5-5") => "Claude Sonnet 5.5",
            _ => model,
        };
    }

    private void RefreshModelMenu()
    {
        if (_trayIconManager is null || _settingsStore is null) return;
        var entries = BuildModelMenuEntries();
        _trayIconManager.UpdateModelMenu(entries, _settingsStore.LlmProvider, _settingsStore.LlmModel);
    }

    private async void OnModelSelected(object? sender, ModelSelectedEventArgs e)
    {
        if (_settingsStore is null || _secretsStore is null || _companionManager is null) return;

        var fromProvider = _settingsStore.LlmProvider;
        var fromModel = _settingsStore.LlmModel;

        // No-op if the user clicked the already-active model.
        if (fromProvider == e.Provider && fromModel == e.Model) return;

        // Update settings store.
        _settingsStore.LlmProvider = e.Provider;
        _settingsStore.LlmModel = e.Model;

        // Build new LLM client and swap it into the running CompanionManager.
        var newClient = BuildLlmClient();
        await _companionManager.SwapLlmClientAsync(newClient, e.Provider, e.Model);

        // Update the ViewModel display.
        if (_companionViewModel is not null)
            _companionViewModel.LlmModelDisplay = GetModelDisplayName(e.Provider, e.Model);

        // Refresh the tray menu to reflect the new active model.
        RefreshModelMenu();
    }

    /// <summary>
    /// Checks whether the Anthropic key is present, plus the key of any cloud speech
    /// service the user picked. With the default local Whisper/Piper no other key is needed.
    /// </summary>
    internal static bool HasRequiredKeys(SecretsStore secrets, SettingsStore settings)
    {
        var needsAssemblyAiKey = settings.SpeechRecognitionChoice == SettingsViewModel.AssemblyAiSpeechRecognitionId;
        var needsElevenLabsKey = settings.VoiceChoice == SettingsViewModel.ElevenLabsVoiceChoiceId;

        return secrets.Exists(SecretsStore.AnthropicApiKey)
            && (!needsAssemblyAiKey || secrets.Exists(SecretsStore.AssemblyAiApiKey))
            && (!needsElevenLabsKey || secrets.Exists(SecretsStore.ElevenLabsApiKey));
    }

    private void NormalizeAnthropicModelSettings()
    {
        if (_settingsStore is null) return;

        if (_settingsStore.LlmProvider != "anthropic")
            _settingsStore.LlmProvider = "anthropic";

        var normalizedModel = SettingsViewModel.NormalizeModel(_settingsStore.LlmModel);
        if (_settingsStore.LlmModel != normalizedModel)
            _settingsStore.LlmModel = normalizedModel;
    }

    /// <summary>
    /// Called when CompanionManager detects a key-related pipeline error (e.g. 401).
    /// Surfaces the error to the user and auto-opens SettingsWindow.
    /// </summary>
    private void OnPipelineKeyError(object? sender, System.EventArgs e)
    {
        OpenSettingsWindow(isFirstRun: false);
    }

    private async Task ProbeMicrophonePermissionAsync()
    {
        var granted = await MicrophonePermissions.ProbeAsync().ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_companionViewModel is not null)
            {
                _companionViewModel.HasMicrophonePermission = granted;
            }
        });
    }

    private async Task ProbeScreenCapturePermissionAsync()
    {
        var granted = await ScreenCapturePermissions.ProbeAsync().ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_companionViewModel is not null)
            {
                _companionViewModel.HasScreenCapturePermission = granted;
            }
        });
    }

    private void OnTrayIconClicked(object? sender, System.EventArgs e)
    {
        _mainWindow?.ShowApplicationWindow();
    }

    private void OnSettingsClicked(object? sender, System.EventArgs e)
    {
        OpenSettingsWindow(isFirstRun: false);
    }

    private void OnGamerModeChanged(object? sender, bool enabled)
    {
        if (_settingsStore is not null)
            _settingsStore.GamerModeEnabled = enabled;

        if (_companionViewModel is not null)
            _companionViewModel.GamerModeEnabled = enabled;

        if (_overlayManager is not null)
            _overlayManager.GamerModeEnabled = enabled;
    }

    private void OnOverlayTestRequested(object? sender, OverlayTestRequestedEventArgs e)
    {
        _overlayManager?.TestFlyToPreset(e.PresetId);
    }

    private void OnDesktopSmokeTestRequested(object? sender, System.EventArgs e)
    {
        if (_companionManager is null)
            return;

        if (_pointingSmokeWindow is null)
        {
            _pointingSmokeWindow = new PointingSmokeWindow(_companionManager);
            _pointingSmokeWindow.Closed += (_, _) => _pointingSmokeWindow = null;
        }

        _pointingSmokeWindow.Show();
        _pointingSmokeWindow.Activate();
    }

    private void OnProviderTimingDiagnosticsRequested(object? sender, System.EventArgs e)
    {
        if (_companionManager is null)
            return;

        _ = Task.Run(() => _companionManager.RunProviderTimingDiagnosticsAsync());
    }

    private void OpenSettingsWindow(bool isFirstRun)
    {
        if (_settingsStore is null || _secretsStore is null) return;

        var vm = new SettingsViewModel(_secretsStore, _settingsStore);
        var window = new SettingsWindow(vm, isFirstRun);
        window.SettingsSaved += OnSettingsWindowSaved;
        window.Show();
    }

    private void OnSettingsWindowSaved(object? sender, System.EventArgs e)
    {
        // Settings were saved — rebuild all clients with the new keys/config.
        _companionViewModel?.ClearError();
        if (_companionViewModel is not null && _settingsStore is not null)
            _companionViewModel.GamerModeEnabled = _settingsStore.GamerModeEnabled;
        if (_overlayManager is not null && _settingsStore is not null)
            _overlayManager.GamerModeEnabled = _settingsStore.GamerModeEnabled;
        BuildAndStartCompanionManager();

        // Update model display and refresh menu (keys may have changed).
        if (_companionViewModel is not null && _settingsStore is not null)
            _companionViewModel.LlmModelDisplay = GetModelDisplayName(
                _settingsStore.LlmProvider, _settingsStore.LlmModel);
        RefreshModelMenu();
    }

    /// <summary>
    /// Polls permissions every 1.5 s so the onboarding panel reflects real-time
    /// state as the user grants access in Windows Settings.
    /// </summary>
    private void OnPermissionPollTick(object? sender, System.EventArgs e)
    {
        _ = RefreshPermissionsAsync();
    }

    private async Task RefreshPermissionsAsync()
    {
        var micTask = MicrophonePermissions.ProbeAsync();
        var captureTask = ScreenCapturePermissions.ProbeAsync();

        var micGranted = await micTask.ConfigureAwait(false);
        var captureGranted = await captureTask.ConfigureAwait(false);

        await Dispatcher.InvokeAsync(() =>
        {
            if (_companionViewModel is null) return;

            _companionViewModel.HasMicrophonePermission = micGranted;
            _companionViewModel.HasScreenCapturePermission = captureGranted;

            if (_companionViewModel.AllPermissionsGranted &&
                !_companionViewModel.HasCompletedOnboarding)
            {
                if (_settingsStore is not null)
                    _settingsStore.OnboardingComplete = true;
                _companionViewModel.HasCompletedOnboarding = true;
            }
        });
    }

    /// <summary>
    /// Seeds API keys from a <c>.env</c> file in the exe's directory (or repo root)
    /// and/or system environment variables. The .env file is gitignored so keys
    /// never leak into source control or AI context. Keys already in secrets.bin
    /// are not overwritten.
    /// </summary>
    private static void SeedSecretsFromEnvironment(SecretsStore secrets)
    {
        // Load .env file next to the exe (repo root for published builds).
        var envFile = Path.Combine(AppContext.BaseDirectory, ".env");
        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(envFile))
        {
            foreach (var line in File.ReadAllLines(envFile))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = trimmed[..eqIdx].Trim();
                var val = trimmed[(eqIdx + 1)..].Trim();
                envVars[key] = val;
            }
        }

        static void Seed(SecretsStore s, string envVar, string secretKey,
            Dictionary<string, string> fileVars)
        {
            if (s.Exists(secretKey)) return;
            // .env file takes precedence, fall back to system env var.
            if (!fileVars.TryGetValue(envVar, out var value) || string.IsNullOrWhiteSpace(value))
                value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(value))
                s.Write(secretKey, value);
        }

        Seed(secrets, "HEYDANIS_ANTHROPIC_KEY", SecretsStore.AnthropicApiKey, envVars);
        Seed(secrets, "HEYDANIS_ASSEMBLYAI_KEY", SecretsStore.AssemblyAiApiKey, envVars);
        Seed(secrets, "HEYDANIS_ELEVENLABS_KEY", SecretsStore.ElevenLabsApiKey, envVars);
    }

    /// <summary>
    /// Migrates onboarded and analyticsOptOut values from HKCU\Software\HeyDanis
    /// registry keys (US-014/US-016) to SettingsStore on first run of US-019 code.
    /// </summary>
    private static void MigrateRegistrySettings(SettingsStore settings)
    {
        try
        {
            if (!settings.OnboardingComplete && OnboardingService.HasCompletedOnboarding())
            {
                settings.OnboardingComplete = true;
            }
        }
        catch
        {
            // Migration is best-effort — don't block startup.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Hard deadline: force-kill the process after 3s no matter what.
        // This runs on a thread-pool thread so a blocked UI thread can't prevent it.
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            Environment.Exit(0);
        });

        // 1. Stop timers first (no async work).
        _permissionPollTimer?.Stop();
        _permissionPollTimer = null;

        // 2. Async-dispose CompanionManager with 2s timeout for background tasks.
        if (_companionManager is not null)
        {
            _companionManager.OpenSettingsRequested -= OnPipelineKeyError;
            try { _companionManager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
            _companionManager = null;
        }

        _whisperTranscriber?.Dispose();
        _whisperTranscriber = null;

        // 3. Dispose overlay manager.
        _overlayManager?.Dispose();
        _overlayManager = null;

        // 4. Dispose push-to-talk hook.
        _pushToTalkHook?.Dispose();
        _pushToTalkHook = null;

        // 5. Dispose tray icon (must happen on UI thread to avoid ghost icons).
        if (_trayIconManager is not null)
        {
            _trayIconManager.TrayIconClicked -= OnTrayIconClicked;
            _trayIconManager.SettingsClicked -= OnSettingsClicked;
            _trayIconManager.ModelSelected -= OnModelSelected;
            _trayIconManager.OverlayTestRequested -= OnOverlayTestRequested;
            _trayIconManager.DesktopSmokeTestRequested -= OnDesktopSmokeTestRequested;
            _trayIconManager.ProviderTimingDiagnosticsRequested -= OnProviderTimingDiagnosticsRequested;
            _trayIconManager.Dispose();
            _trayIconManager = null;
        }
        _pointingSmokeWindow?.Close();
        _pointingSmokeWindow = null;
        _companionPanel?.Close();
        if (_mainWindow is not null)
        {
            _mainWindow.SettingsRequested -= OnSettingsClicked;
            _mainWindow.GamerModeChanged -= OnGamerModeChanged;
            _mainWindow.Close();
            _mainWindow = null;
        }

        // Dispose the mutex (releases ownership automatically).
        // Do NOT call ReleaseMutex() — OnExit may run on a different thread
        // than OnStartup, causing an ApplicationException.
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);

        // Explicit exit — don't rely on WPF's natural shutdown which can stall
        // if any foreground thread is still alive.
        Environment.Exit(0);
    }
}
