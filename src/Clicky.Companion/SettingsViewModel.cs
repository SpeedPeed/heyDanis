using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clicky.Audio;

namespace Clicky.Companion;

/// <summary>
/// ViewModel backing the SettingsWindow. Handles field binding, Test button probes,
/// Save/Quit commands, and provider-dependent model list updates.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly SecretsStore _secretsStore;
    private readonly SettingsStore _settingsStore;
    private readonly HttpClient _httpClient;

    private string _selectedProvider = "codex";
    private string _selectedModel = "gpt-5.5";

    private string _anthropicApiKey = "";
    private string _openAiApiKey = "";
    private string _zaiApiKey = "";
    private string _assemblyAiApiKey = "";
    private string _elevenLabsApiKey = "";
    // Keep in sync with SettingsStore.SettingsData.ElevenLabsVoiceId — Adam,
    // a built-in default voice that ElevenLabs' free API tier can synthesize.
    private string _elevenLabsVoiceId = "pNInz6obpgDQGcFmaJgB";

    private string _selectedMicrophoneDeviceId = AudioDevices.DefaultDeviceId;
    private string _selectedSpeakerDeviceId = AudioDevices.DefaultDeviceId;
    private bool _gamerModeEnabled;

    // True when the field shows the saved placeholder instead of the real key.
    private bool _anthropicKeyIsSaved;
    private bool _openAiKeyIsSaved;
    private bool _zaiKeyIsSaved;
    private bool _assemblyAiKeyIsSaved;
    private bool _elevenLabsKeyIsSaved;

    private TestState _anthropicTestState = TestState.None;
    private TestState _openAiTestState = TestState.None;
    private TestState _zaiTestState = TestState.None;
    private TestState _assemblyAiTestState = TestState.None;
    private TestState _elevenLabsTestState = TestState.None;

    private string? _anthropicTestError;
    private string? _openAiTestError;
    private string? _zaiTestError;
    private string? _assemblyAiTestError;
    private string? _elevenLabsTestError;

    // Inline validation errors shown when a required key is missing (first-run / partial config).
    private string? _anthropicKeyError;
    private string? _openAiKeyError;
    private string? _zaiKeyError;
    private string? _assemblyAiKeyError;
    private string? _elevenLabsKeyError;

    /// <summary>Raised after a successful Save.</summary>
    public event EventHandler? SettingsSaved;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel(SecretsStore secretsStore, SettingsStore settingsStore, HttpClient? httpClient = null)
    {
        _secretsStore = secretsStore;
        _settingsStore = settingsStore;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Enumerate devices once on construction so the dropdowns have something
        // to show. If the user plugs in a new device while the window is open
        // they'll see it next time — mirroring Windows Sound Settings behavior.
        MicrophoneDevices = AudioDevices.EnumerateInputDevices();
        SpeakerDevices = AudioDevices.EnumerateOutputDevices();

        LoadFromStores();
    }

    // -- Audio device selection --

    /// <summary>
    /// Available input (microphone) devices, with "System default" pinned first.
    /// </summary>
    public IReadOnlyList<AudioDevices.DeviceInfo> MicrophoneDevices { get; }

    /// <summary>
    /// Available output (speaker/headphone) devices, with "System default" pinned first.
    /// </summary>
    public IReadOnlyList<AudioDevices.DeviceInfo> SpeakerDevices { get; }

    /// <summary>
    /// NAudio MMDevice ID of the user-selected microphone, or empty string for "System default".
    /// </summary>
    public string SelectedMicrophoneDeviceId
    {
        get => _selectedMicrophoneDeviceId;
        set
        {
            var normalized = value ?? AudioDevices.DefaultDeviceId;
            if (_selectedMicrophoneDeviceId == normalized) return;
            _selectedMicrophoneDeviceId = normalized;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// NAudio MMDevice ID of the user-selected speaker/headphone output, or empty string for "System default".
    /// </summary>
    public string SelectedSpeakerDeviceId
    {
        get => _selectedSpeakerDeviceId;
        set
        {
            var normalized = value ?? AudioDevices.DefaultDeviceId;
            if (_selectedSpeakerDeviceId == normalized) return;
            _selectedSpeakerDeviceId = normalized;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// When enabled, Clicky does not render the idle cursor next to the mouse.
    /// AI-triggered pointing and future drawing annotations still appear.
    /// </summary>
    public bool GamerModeEnabled
    {
        get => _gamerModeEnabled;
        set
        {
            if (_gamerModeEnabled == value) return;
            _gamerModeEnabled = value;
            OnPropertyChanged();
        }
    }

    // -- Provider / Model --

    public static IReadOnlyList<ProviderChoice> Providers { get; } = new[]
    {
        new ProviderChoice("codex", "OpenAI Codex OAuth"),
    };

    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            var normalized = NormalizeProvider(value);
            if (_selectedProvider == normalized) return;
            _selectedProvider = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(CanSave));

            // Auto-select first model for the new provider.
            SelectedModel = AvailableModels[0].Value;
        }
    }

    public IReadOnlyList<ModelChoice> AvailableModels => _selectedProvider switch
    {
        "codex" => CodexModels,
        _ => AnthropicModels,
    };

    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (_selectedModel == value) return;
            _selectedModel = value;
            OnPropertyChanged();
        }
    }

    public static IReadOnlyList<ModelChoice> CodexModels { get; } = new[]
    {
        new ModelChoice("gpt-5.5", "GPT-5.5 (Codex OAuth)"),
        new ModelChoice("gpt-5.4", "GPT-5.4 (Codex OAuth)"),
        new ModelChoice("gpt-5.4-mini", "GPT-5.4 Mini (Codex OAuth)"),
        new ModelChoice("gpt-5.3-codex", "GPT-5.3 Codex"),
        new ModelChoice("gpt-5.3-codex-spark", "GPT-5.3 Codex Spark"),
    };

    public static IReadOnlyList<ModelChoice> AnthropicModels { get; } = new[]
    {
        new ModelChoice("claude-sonnet-4-6", "Claude Sonnet 4.6"),
        new ModelChoice("claude-haiku-4-5", "Claude Haiku 4.5"),
        new ModelChoice("claude-opus-4-6", "Claude Opus 4.6"),
    };

    public static IReadOnlyList<ModelChoice> OpenAiModels { get; } = new[]
    {
        new ModelChoice("gpt-5.2", "GPT-5.2"),
    };

    public static IReadOnlyList<ModelChoice> ZaiModels { get; } = new[]
    {
        new ModelChoice("glm-4.6v", "GLM-4.6V"),
        new ModelChoice("glm-4.5v", "GLM-4.5V"),
    };

    // -- API Keys --

    public string AnthropicApiKey
    {
        get => _anthropicApiKey;
        set
        {
            if (_anthropicApiKey == value) return;
            _anthropicApiKey = value;
            _anthropicKeyIsSaved = false;
            _anthropicTestState = TestState.None;
            _anthropicKeyError = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AnthropicKeyIsSaved));
            OnPropertyChanged(nameof(AnthropicTestState));
            OnPropertyChanged(nameof(AnthropicKeyError));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool AnthropicKeyIsSaved
    {
        get => _anthropicKeyIsSaved;
        private set { _anthropicKeyIsSaved = value; OnPropertyChanged(); }
    }

    public string OpenAiApiKey
    {
        get => _openAiApiKey;
        set
        {
            if (_openAiApiKey == value) return;
            _openAiApiKey = value;
            _openAiKeyIsSaved = false;
            _openAiTestState = TestState.None;
            _openAiKeyError = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpenAiKeyIsSaved));
            OnPropertyChanged(nameof(OpenAiTestState));
            OnPropertyChanged(nameof(OpenAiKeyError));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool OpenAiKeyIsSaved
    {
        get => _openAiKeyIsSaved;
        private set { _openAiKeyIsSaved = value; OnPropertyChanged(); }
    }

    public string ZaiApiKey
    {
        get => _zaiApiKey;
        set
        {
            if (_zaiApiKey == value) return;
            _zaiApiKey = value;
            _zaiKeyIsSaved = false;
            _zaiTestState = TestState.None;
            _zaiKeyError = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZaiKeyIsSaved));
            OnPropertyChanged(nameof(ZaiTestState));
            OnPropertyChanged(nameof(ZaiKeyError));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool ZaiKeyIsSaved
    {
        get => _zaiKeyIsSaved;
        private set { _zaiKeyIsSaved = value; OnPropertyChanged(); }
    }

    public string AssemblyAiApiKey
    {
        get => _assemblyAiApiKey;
        set
        {
            if (_assemblyAiApiKey == value) return;
            _assemblyAiApiKey = value;
            _assemblyAiKeyIsSaved = false;
            _assemblyAiTestState = TestState.None;
            _assemblyAiKeyError = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AssemblyAiKeyIsSaved));
            OnPropertyChanged(nameof(AssemblyAiTestState));
            OnPropertyChanged(nameof(AssemblyAiKeyError));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool AssemblyAiKeyIsSaved
    {
        get => _assemblyAiKeyIsSaved;
        private set { _assemblyAiKeyIsSaved = value; OnPropertyChanged(); }
    }

    public string ElevenLabsApiKey
    {
        get => _elevenLabsApiKey;
        set
        {
            if (_elevenLabsApiKey == value) return;
            _elevenLabsApiKey = value;
            _elevenLabsKeyIsSaved = false;
            _elevenLabsTestState = TestState.None;
            _elevenLabsKeyError = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ElevenLabsKeyIsSaved));
            OnPropertyChanged(nameof(ElevenLabsTestState));
            OnPropertyChanged(nameof(ElevenLabsKeyError));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool ElevenLabsKeyIsSaved
    {
        get => _elevenLabsKeyIsSaved;
        private set { _elevenLabsKeyIsSaved = value; OnPropertyChanged(); }
    }

    public string ElevenLabsVoiceId
    {
        get => _elevenLabsVoiceId;
        set
        {
            if (_elevenLabsVoiceId == value) return;
            _elevenLabsVoiceId = value;
            OnPropertyChanged();
        }
    }

    // -- Test state --

    public TestState AnthropicTestState
    {
        get => _anthropicTestState;
        private set { _anthropicTestState = value; OnPropertyChanged(); OnPropertyChanged(nameof(AnthropicTestError)); }
    }

    public string? AnthropicTestError
    {
        get => _anthropicTestError;
        private set { _anthropicTestError = value; OnPropertyChanged(); }
    }

    public TestState OpenAiTestState
    {
        get => _openAiTestState;
        private set { _openAiTestState = value; OnPropertyChanged(); OnPropertyChanged(nameof(OpenAiTestError)); }
    }

    public string? OpenAiTestError
    {
        get => _openAiTestError;
        private set { _openAiTestError = value; OnPropertyChanged(); }
    }

    public TestState ZaiTestState
    {
        get => _zaiTestState;
        private set { _zaiTestState = value; OnPropertyChanged(); OnPropertyChanged(nameof(ZaiTestError)); }
    }

    public string? ZaiTestError
    {
        get => _zaiTestError;
        private set { _zaiTestError = value; OnPropertyChanged(); }
    }

    public TestState AssemblyAiTestState
    {
        get => _assemblyAiTestState;
        private set { _assemblyAiTestState = value; OnPropertyChanged(); OnPropertyChanged(nameof(AssemblyAiTestError)); }
    }

    public string? AssemblyAiTestError
    {
        get => _assemblyAiTestError;
        private set { _assemblyAiTestError = value; OnPropertyChanged(); }
    }

    public TestState ElevenLabsTestState
    {
        get => _elevenLabsTestState;
        private set { _elevenLabsTestState = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElevenLabsTestError)); }
    }

    public string? ElevenLabsTestError
    {
        get => _elevenLabsTestError;
        private set { _elevenLabsTestError = value; OnPropertyChanged(); }
    }

    // -- Field validation errors (for first-run / partial config highlighting) --

    public string? AnthropicKeyError
    {
        get => _anthropicKeyError;
        private set { _anthropicKeyError = value; OnPropertyChanged(); }
    }

    public string? OpenAiKeyError
    {
        get => _openAiKeyError;
        private set { _openAiKeyError = value; OnPropertyChanged(); }
    }

    public string? ZaiKeyError
    {
        get => _zaiKeyError;
        private set { _zaiKeyError = value; OnPropertyChanged(); }
    }

    public string? AssemblyAiKeyError
    {
        get => _assemblyAiKeyError;
        private set { _assemblyAiKeyError = value; OnPropertyChanged(); }
    }

    public string? ElevenLabsKeyError
    {
        get => _elevenLabsKeyError;
        private set { _elevenLabsKeyError = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Highlights missing required fields with inline error text.
    /// Called on first-run or when reopened due to partial config.
    /// </summary>
    public void ValidateRequiredFields()
    {
        if (_selectedProvider == "codex")
            AnthropicKeyError = null;
        else if (_selectedProvider == "anthropic" && !HasKey(_anthropicApiKey, _anthropicKeyIsSaved))
            AnthropicKeyError = "Required for your selected service";
        else
            AnthropicKeyError = null;

        if (_selectedProvider == "codex")
            OpenAiKeyError = null;
        else if (_selectedProvider == "openai" && !HasKey(_openAiApiKey, _openAiKeyIsSaved))
            OpenAiKeyError = "Required for your selected service";
        else
            OpenAiKeyError = null;

        if (_selectedProvider == "codex")
            ZaiKeyError = null;
        else if (_selectedProvider == "zai" && !HasKey(_zaiApiKey, _zaiKeyIsSaved))
            ZaiKeyError = "Required for your selected service";
        else
            ZaiKeyError = null;

        if (!HasKey(_assemblyAiApiKey, _assemblyAiKeyIsSaved))
            AssemblyAiKeyError = "Required";
        else
            AssemblyAiKeyError = null;

        if (!HasKey(_elevenLabsApiKey, _elevenLabsKeyIsSaved))
            ElevenLabsKeyError = "Required";
        else
            ElevenLabsKeyError = null;
    }

    // -- Save enablement --

    /// <summary>
    /// Save is enabled when the required keys for the selected provider are entered
    /// (or were previously saved), plus both audio keys are required.
    /// </summary>
    public bool CanSave
    {
        get
        {
            bool hasLlmKey = _selectedProvider switch
            {
                "zai" => HasKey(_zaiApiKey, _zaiKeyIsSaved),
                "openai" => HasKey(_openAiApiKey, _openAiKeyIsSaved),
                "codex" => true,
                _ => HasKey(_anthropicApiKey, _anthropicKeyIsSaved),
            };

            bool hasAudioKeys = HasKey(_assemblyAiApiKey, _assemblyAiKeyIsSaved)
                             && HasKey(_elevenLabsApiKey, _elevenLabsKeyIsSaved);

            return hasLlmKey && hasAudioKeys;
        }
    }

    private static bool HasKey(string key, bool isSaved) =>
        isSaved || !string.IsNullOrWhiteSpace(key);

    // -- Commands --

    public void Save()
    {
        // Write keys (only write if the user entered a new value, not the placeholder).
        if (!_anthropicKeyIsSaved && !string.IsNullOrWhiteSpace(_anthropicApiKey))
            _secretsStore.Write(SecretsStore.AnthropicApiKey, _anthropicApiKey);

        if (!_openAiKeyIsSaved && !string.IsNullOrWhiteSpace(_openAiApiKey))
            _secretsStore.Write(SecretsStore.OpenAiApiKey, _openAiApiKey);

        if (!_zaiKeyIsSaved && !string.IsNullOrWhiteSpace(_zaiApiKey))
            _secretsStore.Write(SecretsStore.ZaiApiKey, _zaiApiKey);

        if (!_assemblyAiKeyIsSaved && !string.IsNullOrWhiteSpace(_assemblyAiApiKey))
            _secretsStore.Write(SecretsStore.AssemblyAiApiKey, _assemblyAiApiKey);

        if (!_elevenLabsKeyIsSaved && !string.IsNullOrWhiteSpace(_elevenLabsApiKey))
            _secretsStore.Write(SecretsStore.ElevenLabsApiKey, _elevenLabsApiKey);

        // Write non-secret settings.
        _settingsStore.LlmProvider = _selectedProvider;
        _settingsStore.LlmModel = _selectedModel;
        _settingsStore.ElevenLabsVoiceId = _elevenLabsVoiceId;
        _settingsStore.MicrophoneDeviceId = _selectedMicrophoneDeviceId;
        _settingsStore.SpeakerDeviceId = _selectedSpeakerDeviceId;
        _settingsStore.GamerModeEnabled = _gamerModeEnabled;

        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    public void ClearAnthropicKey() { AnthropicApiKey = ""; AnthropicKeyIsSaved = false; }
    public void ClearOpenAiKey() { OpenAiApiKey = ""; OpenAiKeyIsSaved = false; }
    public void ClearZaiKey() { ZaiApiKey = ""; ZaiKeyIsSaved = false; }
    public void ClearAssemblyAiKey() { AssemblyAiApiKey = ""; AssemblyAiKeyIsSaved = false; }
    public void ClearElevenLabsKey() { ElevenLabsApiKey = ""; ElevenLabsKeyIsSaved = false; }

    // -- Test probes --

    public async Task TestAnthropicAsync(CancellationToken ct = default)
    {
        var key = _anthropicKeyIsSaved
            ? _secretsStore.Read(SecretsStore.AnthropicApiKey) ?? ""
            : _anthropicApiKey;

        AnthropicTestState = TestState.Testing;
        AnthropicTestError = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(
                """{"model":"claude-haiku-4-5","max_tokens":1,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                AnthropicTestState = TestState.Success;
                ClickyAnalytics.TrackProviderTestRun("anthropic", success: true);
            }
            else
            {
                AnthropicTestError = FriendlyError("Anthropic", (int)response.StatusCode);
                AnthropicTestState = TestState.Failure;
                ClickyAnalytics.TrackProviderTestRun("anthropic", success: false, ErrorCategory((int)response.StatusCode));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnthropicTestError = FriendlyError("Anthropic", ex);
            AnthropicTestState = TestState.Failure;
            ClickyAnalytics.TrackProviderTestRun("anthropic", success: false, ErrorCategory(ex));
        }
    }

    public async Task TestZaiAsync(CancellationToken ct = default)
    {
        var key = _zaiKeyIsSaved
            ? _secretsStore.Read(SecretsStore.ZaiApiKey) ?? ""
            : _zaiApiKey;

        ZaiTestState = TestState.Testing;
        ZaiTestError = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.z.ai/api/paas/v4/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {key}");
            request.Content = new StringContent(
                """{"model":"glm-4.5v","max_tokens":1,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                ZaiTestState = TestState.Success;
                ClickyAnalytics.TrackProviderTestRun("zai", success: true);
            }
            else
            {
                ZaiTestError = FriendlyError("z.ai", (int)response.StatusCode);
                ZaiTestState = TestState.Failure;
                ClickyAnalytics.TrackProviderTestRun("zai", success: false, ErrorCategory((int)response.StatusCode));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ZaiTestError = FriendlyError("z.ai", ex);
            ZaiTestState = TestState.Failure;
            ClickyAnalytics.TrackProviderTestRun("zai", success: false, ErrorCategory(ex));
        }
    }

    public async Task TestOpenAiAsync(CancellationToken ct = default)
    {
        var key = _openAiKeyIsSaved
            ? _secretsStore.Read(SecretsStore.OpenAiApiKey) ?? ""
            : _openAiApiKey;

        OpenAiTestState = TestState.Testing;
        OpenAiTestError = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {key}");
            request.Content = new StringContent(
                """{"model":"gpt-5.2","max_completion_tokens":1,"messages":[{"role":"developer","content":"Reply with ok."},{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                OpenAiTestState = TestState.Success;
                ClickyAnalytics.TrackProviderTestRun("openai", success: true);
            }
            else
            {
                OpenAiTestError = FriendlyError("OpenAI", (int)response.StatusCode);
                OpenAiTestState = TestState.Failure;
                ClickyAnalytics.TrackProviderTestRun("openai", success: false, ErrorCategory((int)response.StatusCode));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OpenAiTestError = FriendlyError("OpenAI", ex);
            OpenAiTestState = TestState.Failure;
            ClickyAnalytics.TrackProviderTestRun("openai", success: false, ErrorCategory(ex));
        }
    }

    public async Task TestAssemblyAiAsync(CancellationToken ct = default)
    {
        var key = _assemblyAiKeyIsSaved
            ? _secretsStore.Read(SecretsStore.AssemblyAiApiKey) ?? ""
            : _assemblyAiApiKey;

        AssemblyAiTestState = TestState.Testing;
        AssemblyAiTestError = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://streaming.assemblyai.com/v3/token?expires_in_seconds=60");
            request.Headers.Add("Authorization", key); // Raw key, not Bearer.

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                AssemblyAiTestState = TestState.Success;
                ClickyAnalytics.TrackProviderTestRun("assemblyai", success: true);
            }
            else
            {
                AssemblyAiTestError = FriendlyError("AssemblyAI", (int)response.StatusCode);
                AssemblyAiTestState = TestState.Failure;
                ClickyAnalytics.TrackProviderTestRun("assemblyai", success: false, ErrorCategory((int)response.StatusCode));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AssemblyAiTestError = FriendlyError("AssemblyAI", ex);
            AssemblyAiTestState = TestState.Failure;
            ClickyAnalytics.TrackProviderTestRun("assemblyai", success: false, ErrorCategory(ex));
        }
    }

    public async Task TestElevenLabsAsync(CancellationToken ct = default)
    {
        var key = _elevenLabsKeyIsSaved
            ? _secretsStore.Read(SecretsStore.ElevenLabsApiKey) ?? ""
            : _elevenLabsApiKey;

        ElevenLabsTestState = TestState.Testing;
        ElevenLabsTestError = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.elevenlabs.io/v1/user/subscription");
            request.Headers.Add("xi-api-key", key);

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                ElevenLabsTestState = TestState.Success;
                ClickyAnalytics.TrackProviderTestRun("elevenlabs", success: true);
            }
            else
            {
                ElevenLabsTestError = FriendlyError("ElevenLabs", (int)response.StatusCode);
                ElevenLabsTestState = TestState.Failure;
                ClickyAnalytics.TrackProviderTestRun("elevenlabs", success: false, ErrorCategory((int)response.StatusCode));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ElevenLabsTestError = FriendlyError("ElevenLabs", ex);
            ElevenLabsTestState = TestState.Failure;
            ClickyAnalytics.TrackProviderTestRun("elevenlabs", success: false, ErrorCategory(ex));
        }
    }

    // -- Helpers --

    private void LoadFromStores()
    {
        _selectedProvider = NormalizeProvider(_settingsStore.LlmProvider);
        _selectedModel = NormalizeModel(_settingsStore.LlmModel);
        _elevenLabsVoiceId = _settingsStore.ElevenLabsVoiceId;

        // Seed device selection from the store. If the saved device ID is no
        // longer in the enumerated list (e.g. USB headset unplugged), silently
        // reset to "System default" so the ComboBox isn't empty and the user
        // still gets working audio.
        _selectedMicrophoneDeviceId = ResolveSavedDeviceId(_settingsStore.MicrophoneDeviceId, MicrophoneDevices);
        _selectedSpeakerDeviceId = ResolveSavedDeviceId(_settingsStore.SpeakerDeviceId, SpeakerDevices);
        _gamerModeEnabled = _settingsStore.GamerModeEnabled;

        // Pre-populate saved-key placeholders.
        if (_secretsStore.Exists(SecretsStore.AnthropicApiKey))
            _anthropicKeyIsSaved = true;

        if (_secretsStore.Exists(SecretsStore.OpenAiApiKey))
            _openAiKeyIsSaved = true;

        if (_secretsStore.Exists(SecretsStore.ZaiApiKey))
            _zaiKeyIsSaved = true;

        if (_secretsStore.Exists(SecretsStore.AssemblyAiApiKey))
            _assemblyAiKeyIsSaved = true;

        if (_secretsStore.Exists(SecretsStore.ElevenLabsApiKey))
            _elevenLabsKeyIsSaved = true;
    }

    private static string NormalizeProvider(string provider) =>
        provider == "codex" ? "codex" : "codex";

    private static string NormalizeModel(string model)
    {
        foreach (var choice in CodexModels)
        {
            if (choice.Value == model) return model;
        }
        return CodexModels[0].Value;
    }

    private static string ResolveSavedDeviceId(string saved, IReadOnlyList<AudioDevices.DeviceInfo> available)
    {
        if (string.IsNullOrEmpty(saved)) return AudioDevices.DefaultDeviceId;
        foreach (var d in available)
        {
            if (d.Id == saved) return saved;
        }
        return AudioDevices.DefaultDeviceId;
    }

    internal static string FriendlyError(string service, int statusCode) => statusCode switch
    {
        401 => $"Couldn't verify your {service} key \u2014 check that you pasted it correctly.",
        403 => $"Your {service} key doesn't have the required permissions.",
        429 => $"{service} is rate-limiting this key \u2014 wait a moment and try again.",
        >= 500 => $"{service} seems to be having issues right now \u2014 try again in a minute.",
        _ => $"Couldn't reach {service} (HTTP {statusCode}) \u2014 check your key and internet connection.",
    };

    internal static string FriendlyError(string service, Exception ex) => ex switch
    {
        HttpRequestException => $"Couldn't reach {service} \u2014 check your internet connection.",
        TaskCanceledException => $"{service} didn't respond in time \u2014 try again.",
        _ => $"Something went wrong testing {service} \u2014 please try again or check your setup.",
    };

    internal static string ErrorCategory(int statusCode) => statusCode switch
    {
        401 => "auth_failed",
        403 => "forbidden",
        429 => "rate_limited",
        >= 500 => "server_error",
        _ => $"http_{statusCode}",
    };

    internal static string ErrorCategory(Exception ex) => ex switch
    {
        HttpRequestException => "network_error",
        TaskCanceledException => "timeout",
        _ => "unknown",
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum TestState { None, Testing, Success, Failure }

public record ProviderChoice(string Value, string Display);
public record ModelChoice(string Value, string Display);
