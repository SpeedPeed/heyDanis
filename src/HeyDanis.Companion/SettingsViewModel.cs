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
using HeyDanis.Audio;

namespace HeyDanis.Companion;

/// <summary>
/// ViewModel backing the SettingsWindow. Handles field binding, Test button probes,
/// Save/Quit commands, and provider-dependent model list updates.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly SecretsStore _secretsStore;
    private readonly SettingsStore _settingsStore;
    private readonly HttpClient _httpClient;

    private string _selectedProvider = "anthropic";
    private string _selectedModel = DefaultModel;

    private string _anthropicApiKey = "";
    private string _assemblyAiApiKey = "";
    private string _elevenLabsApiKey = "";
    // Keep in sync with SettingsStore.SettingsData.ElevenLabsVoiceId — Adam,
    // a built-in default voice that ElevenLabs' free API tier can synthesize.
    private string _elevenLabsVoiceId = "pNInz6obpgDQGcFmaJgB";

    private string _selectedMicrophoneDeviceId = AudioDevices.DefaultDeviceId;
    private string _selectedSpeakerDeviceId = AudioDevices.DefaultDeviceId;
    private bool _gamerModeEnabled;
    private string _selectedSpeechRecognitionId = DefaultSpeechRecognitionId;
    private string _selectedVoiceId = DefaultVoiceId;
    private double _speakingRate = 1.05;

    // True when the field shows the saved placeholder instead of the real key.
    private bool _anthropicKeyIsSaved;
    private bool _assemblyAiKeyIsSaved;
    private bool _elevenLabsKeyIsSaved;

    private TestState _anthropicTestState = TestState.None;
    private TestState _assemblyAiTestState = TestState.None;
    private TestState _elevenLabsTestState = TestState.None;

    private string? _anthropicTestError;
    private string? _assemblyAiTestError;
    private string? _elevenLabsTestError;

    // Inline validation errors shown when a required key is missing (first-run / partial config).
    private string? _anthropicKeyError;
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
    /// When enabled, HeyDanis does not render the idle cursor next to the mouse.
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

    // -- Speech (recognition + voice) --

    public const string DefaultSpeechRecognitionId = "whisper:small";
    public const string DefaultVoiceId = "piper:thorsten-medium";
    public const string AssemblyAiSpeechRecognitionId = "assemblyai";
    public const string ElevenLabsVoiceChoiceId = "elevenlabs";

    public static IReadOnlyList<ModelChoice> SpeechRecognitionChoices { get; } = new[]
    {
        new ModelChoice("whisper:small", "Lokal – Whisper Small (gratis, empfohlen)"),
        new ModelChoice("whisper:base", "Lokal – Whisper Base (gratis, schneller, ungenauer)"),
        new ModelChoice("whisper:large-turbo", "Lokal – Whisper Large Turbo (gratis, sehr genau, braucht starken PC)"),
        new ModelChoice(AssemblyAiSpeechRecognitionId, "Cloud – AssemblyAI (ca. 0,15 $/Stunde, Key nötig)"),
    };

    public static IReadOnlyList<ModelChoice> VoiceChoices { get; } = new[]
    {
        new ModelChoice("piper:thorsten-medium", "Lokal – Thorsten (männlich, gratis, empfohlen)"),
        new ModelChoice("piper:thorsten-high", "Lokal – Thorsten HQ (männlich, gratis, natürlicher)"),
        new ModelChoice("windows", "Windows-Stimme (gratis, kein Download, robotischer)"),
        new ModelChoice(ElevenLabsVoiceChoiceId, "Cloud – ElevenLabs (Abo, Key nötig)"),
    };

    public string SelectedSpeechRecognitionId
    {
        get => _selectedSpeechRecognitionId;
        set
        {
            var normalized = NormalizeChoice(value, SpeechRecognitionChoices, DefaultSpeechRecognitionId);
            if (_selectedSpeechRecognitionId == normalized) return;
            _selectedSpeechRecognitionId = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowAssemblyAiKey));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public string SelectedVoiceId
    {
        get => _selectedVoiceId;
        set
        {
            var normalized = NormalizeChoice(value, VoiceChoices, DefaultVoiceId);
            if (_selectedVoiceId == normalized) return;
            _selectedVoiceId = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowElevenLabsKey));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>1.0 = normal speed.</summary>
    public double SpeakingRate
    {
        get => _speakingRate;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, 0.7, 1.6), 2);
            if (Math.Abs(_speakingRate - clamped) < 0.001) return;
            _speakingRate = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>The AssemblyAI key field is only shown (and required) when AssemblyAI is chosen.</summary>
    public bool ShowAssemblyAiKey => _selectedSpeechRecognitionId == AssemblyAiSpeechRecognitionId;

    /// <summary>The ElevenLabs fields are only shown (and required) when ElevenLabs is chosen.</summary>
    public bool ShowElevenLabsKey => _selectedVoiceId == ElevenLabsVoiceChoiceId;

    public static string NormalizeChoice(string? value, IReadOnlyList<ModelChoice> choices, string fallback)
    {
        foreach (var choice in choices)
        {
            if (choice.Value == value) return value!;
        }
        return fallback;
    }

    // -- Provider / Model --

    /// <summary>Günstigstes Claude-Modell mit Bildverständnis – Standard für alle Fragen.</summary>
    public const string DefaultModel = "claude-haiku-5-5";

    public static IReadOnlyList<ProviderChoice> Providers { get; } = new[]
    {
        new ProviderChoice("anthropic", "Anthropic Claude"),
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

    public IReadOnlyList<ModelChoice> AvailableModels => AnthropicModels;

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

    public static IReadOnlyList<ModelChoice> AnthropicModels { get; } = new[]
    {
        new ModelChoice("claude-haiku-5-5", "Claude Haiku 5.5 (günstig, Standard)"),
        new ModelChoice("claude-sonnet-5-5", "Claude Sonnet 5.5 (genauer, ca. 20x teurer)"),
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
        if (!HasKey(_anthropicApiKey, _anthropicKeyIsSaved))
            AnthropicKeyError = "Pflichtfeld";
        else
            AnthropicKeyError = null;



        if (ShowAssemblyAiKey && !HasKey(_assemblyAiApiKey, _assemblyAiKeyIsSaved))
            AssemblyAiKeyError = "Pflichtfeld, solange AssemblyAI ausgewählt ist";
        else
            AssemblyAiKeyError = null;

        if (ShowElevenLabsKey && !HasKey(_elevenLabsApiKey, _elevenLabsKeyIsSaved))
            ElevenLabsKeyError = "Pflichtfeld, solange ElevenLabs ausgewählt ist";
        else
            ElevenLabsKeyError = null;
    }

    // -- Save enablement --

    /// <summary>
    /// Save is enabled when the Anthropic key is entered (or saved), plus the key of
    /// any cloud speech service the user selected.
    /// </summary>
    public bool CanSave
    {
        get
        {
            bool hasLlmKey = HasKey(_anthropicApiKey, _anthropicKeyIsSaved);

            // Cloud speech keys are only needed when the user picked that cloud service.
            bool hasAudioKeys = (!ShowAssemblyAiKey || HasKey(_assemblyAiApiKey, _assemblyAiKeyIsSaved))
                             && (!ShowElevenLabsKey || HasKey(_elevenLabsApiKey, _elevenLabsKeyIsSaved));

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
        _settingsStore.SpeechRecognitionChoice = _selectedSpeechRecognitionId;
        _settingsStore.VoiceChoice = _selectedVoiceId;
        _settingsStore.SpeakingRate = _speakingRate;

        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    public void ClearAnthropicKey() { AnthropicApiKey = ""; AnthropicKeyIsSaved = false; }
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
                """{"model":"claude-haiku-5-5","max_tokens":1,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                AnthropicTestState = TestState.Success;
            }
            else
            {
                AnthropicTestError = FriendlyError("Anthropic", (int)response.StatusCode);
                AnthropicTestState = TestState.Failure;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnthropicTestError = FriendlyError("Anthropic", ex);
            AnthropicTestState = TestState.Failure;
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
            }
            else
            {
                AssemblyAiTestError = FriendlyError("AssemblyAI", (int)response.StatusCode);
                AssemblyAiTestState = TestState.Failure;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AssemblyAiTestError = FriendlyError("AssemblyAI", ex);
            AssemblyAiTestState = TestState.Failure;
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
            }
            else
            {
                ElevenLabsTestError = FriendlyError("ElevenLabs", (int)response.StatusCode);
                ElevenLabsTestState = TestState.Failure;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ElevenLabsTestError = FriendlyError("ElevenLabs", ex);
            ElevenLabsTestState = TestState.Failure;
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
        _selectedSpeechRecognitionId = NormalizeChoice(_settingsStore.SpeechRecognitionChoice, SpeechRecognitionChoices, DefaultSpeechRecognitionId);
        _selectedVoiceId = NormalizeChoice(_settingsStore.VoiceChoice, VoiceChoices, DefaultVoiceId);
        _speakingRate = Math.Clamp(_settingsStore.SpeakingRate, 0.7, 1.6);

        // Pre-populate saved-key placeholders.
        if (_secretsStore.Exists(SecretsStore.AnthropicApiKey))
            _anthropicKeyIsSaved = true;



        if (_secretsStore.Exists(SecretsStore.AssemblyAiApiKey))
            _assemblyAiKeyIsSaved = true;

        if (_secretsStore.Exists(SecretsStore.ElevenLabsApiKey))
            _elevenLabsKeyIsSaved = true;
    }

    private static string NormalizeProvider(string provider) => "anthropic";

    public static string NormalizeModel(string model)
    {
        foreach (var choice in AnthropicModels)
        {
            if (choice.Value == model) return model;
        }
        return DefaultModel;
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
        401 => $"Der {service}-Key stimmt nicht \u2014 prüf, ob du ihn vollständig eingefügt hast.",
        403 => $"Der {service}-Key hat nicht die nötigen Rechte.",
        429 => $"{service} bremst gerade (zu viele Anfragen) \u2014 kurz warten und nochmal probieren.",
        >= 500 => $"{service} hat gerade Probleme \u2014 in einer Minute nochmal probieren.",
        _ => $"{service} nicht erreichbar (HTTP {statusCode}) \u2014 Key und Internet prüfen.",
    };

    internal static string FriendlyError(string service, Exception ex) => ex switch
    {
        HttpRequestException => $"{service} nicht erreichbar \u2014 prüf deine Internetverbindung.",
        TaskCanceledException => $"{service} hat nicht rechtzeitig geantwortet \u2014 nochmal probieren.",
        _ => $"Beim Testen von {service} ist etwas schiefgelaufen \u2014 nochmal probieren.",
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
