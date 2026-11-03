using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Clicky.Api;
using Clicky.Audio;
using Clicky.Capture;
using Clicky.Companion;
using Clicky.Hotkey;
using Clicky.Overlay;

namespace Clicky.App;

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
    private AutoUpdateService? _autoUpdateService;
    private SettingsStore? _settingsStore;
    private SecretsStore? _secretsStore;
    private PointingSmokeWindow? _pointingSmokeWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard: if another Clicky is already running, exit immediately.
        _singleInstanceMutex = new Mutex(true, "Global\\ClickyAppSingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Clicky is already running.",
                "Clicky",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Initialize settings and secrets stores under %APPDATA%\Clicky.
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
                ClickyAnalytics.TrackOnboardingCompleted();
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

        // Configure PostHog analytics (mirrors ClickyAnalytics.configure() in Mac).
        ClickyAnalytics.Configure();
        ClickyAnalytics.TrackAppOpened();

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

        // Set the initial model display name on the ViewModel.
        _companionViewModel!.LlmModelDisplay = GetModelDisplayName(
            _settingsStore!.LlmProvider, _settingsStore.LlmModel);

        // Populate the tray Model submenu.
        RefreshModelMenu();

        // Initialize WinSparkle auto-update (mirrors Sparkle integration in Mac).
        _autoUpdateService = new AutoUpdateService();
        _autoUpdateService.Initialize(
            "https://raw.githubusercontent.com/julianjear/makesomething-mac-app/main/appcast.xml");
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
            prepareForCaptureAsync: HideClickyUiForCaptureAsync,
            llmProvider: _settingsStore.LlmProvider,
            llmModel: _settingsStore.LlmModel,
            useRedesignedPointingProtocol: UseRedesignedPointingProtocolByDefault);
        _companionManager.OpenSettingsRequested += OnPipelineKeyError;
        _companionManager.Start();
    }

    private async Task HideClickyUiForCaptureAsync()
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
                DebugLog.Write("[POINT] capture-prep: hid visible Clicky companion panel before screenshot");
            }
        });
    }

    private ILlmClient BuildLlmClient()
    {
        NormalizeCodexProviderSettings();
        return new CodexAppServerClient(model: _settingsStore!.LlmModel);
    }

    private AssemblyAiStreamingTranscriber BuildTranscriber()
    {
        var key = _secretsStore!.Read(SecretsStore.AssemblyAiApiKey) ?? "";
        return new AssemblyAiStreamingTranscriber(key);
    }

    private ElevenLabsTtsClient BuildTtsClient()
    {
        var key = _secretsStore!.Read(SecretsStore.ElevenLabsApiKey) ?? "";
        var voiceId = _settingsStore!.ElevenLabsVoiceId;
        var outputDeviceId = _settingsStore.SpeakerDeviceId;
        return new ElevenLabsTtsClient(key, voiceId, outputDeviceId);
    }

    /// <summary>
    /// Builds the list of (provider, model) entries for the tray Model submenu.
    /// Items whose required API key is missing are disabled with a tooltip.
    /// </summary>
    internal List<ModelMenuEntry> BuildModelMenuEntries()
    {
        return new List<ModelMenuEntry>
        {
            new() { Provider = "codex", Model = "gpt-5.5", DisplayName = "GPT-5.5 (Codex OAuth)", IsEnabled = true },
            new() { Provider = "codex", Model = "gpt-5.4", DisplayName = "GPT-5.4 (Codex OAuth)", IsEnabled = true },
            new() { Provider = "codex", Model = "gpt-5.4-mini", DisplayName = "GPT-5.4 Mini (Codex OAuth)", IsEnabled = true },
            new() { Provider = "codex", Model = "gpt-5.3-codex", DisplayName = "GPT-5.3 Codex", IsEnabled = true },
            new() { Provider = "codex", Model = "gpt-5.3-codex-spark", DisplayName = "GPT-5.3 Codex Spark", IsEnabled = true },
        };
    }

    /// <summary>
    /// Returns a user-friendly display name for the current (provider, model) pair.
    /// </summary>
    internal static string GetModelDisplayName(string provider, string model)
    {
        return (provider, model) switch
        {
            ("codex", "gpt-5.5") => "GPT-5.5 (Codex OAuth)",
            ("codex", "gpt-5.4") => "GPT-5.4 (Codex OAuth)",
            ("codex", "gpt-5.4-mini") => "GPT-5.4 Mini (Codex OAuth)",
            ("codex", "gpt-5.3-codex") => "GPT-5.3 Codex",
            ("codex", "gpt-5.3-codex-spark") => "GPT-5.3 Codex Spark",
            ("anthropic", "claude-sonnet-4-6") => "Claude Sonnet 4.6",
            ("anthropic", "claude-haiku-4-5") => "Claude Haiku 4.5",
            ("anthropic", "claude-opus-4-6") => "Claude Opus 4.6",
            ("openai", "gpt-5.2") => "GPT-5.2",
            ("zai", "glm-4.6v") => "GLM-4.6V",
            ("zai", "glm-4.5v") => "GLM-4.5V",
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

        // Track analytics event.
        ClickyAnalytics.TrackModelSwitched(fromProvider, fromModel, e.Provider, e.Model);
    }

    /// <summary>
    /// Checks whether the currently-selected provider's LLM key and both audio
    /// keys are present in the SecretsStore.
    /// </summary>
    internal static bool HasRequiredKeys(SecretsStore secrets, SettingsStore settings)
    {
        return secrets.Exists(SecretsStore.AssemblyAiApiKey)
            && secrets.Exists(SecretsStore.ElevenLabsApiKey);
    }

    private void NormalizeCodexProviderSettings()
    {
        if (_settingsStore is null) return;

        if (_settingsStore.LlmProvider != "codex")
            _settingsStore.LlmProvider = "codex";

        var model = _settingsStore.LlmModel;
        var supported = model is "gpt-5.5" or "gpt-5.4" or "gpt-5.4-mini" or "gpt-5.3-codex" or "gpt-5.3-codex-spark";
        if (!supported)
            _settingsStore.LlmModel = "gpt-5.5";
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
        ClickyAnalytics.TrackSettingsOpened();
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

        Seed(secrets, "CLICKY_ANTHROPIC_KEY", SecretsStore.AnthropicApiKey, envVars);
        Seed(secrets, "CLICKY_OPENAI_KEY", SecretsStore.OpenAiApiKey, envVars);
        Seed(secrets, "CLICKY_ZAI_KEY", SecretsStore.ZaiApiKey, envVars);
        Seed(secrets, "CLICKY_ASSEMBLYAI_KEY", SecretsStore.AssemblyAiApiKey, envVars);
        Seed(secrets, "CLICKY_ELEVENLABS_KEY", SecretsStore.ElevenLabsApiKey, envVars);
    }

    /// <summary>
    /// Migrates onboarded and analyticsOptOut values from HKCU\Software\Clicky
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

            if (!settings.AnalyticsOptOut && ClickyAnalytics.IsOptedOut())
            {
                settings.AnalyticsOptOut = true;
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

        // 1. Stop timers and analytics first (no async work).
        _permissionPollTimer?.Stop();
        _permissionPollTimer = null;

        _autoUpdateService?.Dispose();
        _autoUpdateService = null;

        ClickyAnalytics.Shutdown();

        // 2. Async-dispose CompanionManager with 2s timeout for background tasks.
        if (_companionManager is not null)
        {
            _companionManager.OpenSettingsRequested -= OnPipelineKeyError;
            try { _companionManager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
            _companionManager = null;
        }

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
