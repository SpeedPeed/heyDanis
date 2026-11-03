using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeyDanis.Companion;

/// <summary>
/// Reads/writes non-secret user settings to %APPDATA%\HeyDanis\settings.json.
/// </summary>
public class SettingsStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private SettingsData _data;

    public SettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "heyDanis", "settings.json"))
    {
    }

    internal SettingsStore(string filePath)
    {
        _filePath = filePath;
        _data = Load();
    }

    public string LlmProvider
    {
        get { lock (_lock) return _data.LlmProvider; }
        set { lock (_lock) { _data.LlmProvider = value; Save(); } }
    }

    public string LlmModel
    {
        get { lock (_lock) return _data.LlmModel; }
        set { lock (_lock) { _data.LlmModel = value; Save(); } }
    }

    public string ElevenLabsVoiceId
    {
        get { lock (_lock) return _data.ElevenLabsVoiceId; }
        set { lock (_lock) { _data.ElevenLabsVoiceId = value; Save(); } }
    }

    /// <summary>
    /// Speech recognition: "whisper:base", "whisper:small", "whisper:large-turbo" (local, free)
    /// or "assemblyai" (cloud, needs a key).
    /// </summary>
    public string SpeechRecognitionChoice
    {
        get { lock (_lock) return _data.SpeechRecognitionChoice; }
        set { lock (_lock) { _data.SpeechRecognitionChoice = value; Save(); } }
    }

    /// <summary>
    /// Voice: "piper:thorsten-medium", "piper:thorsten-high" (local, free),
    /// "windows" (built-in Windows voice) or "elevenlabs" (cloud, needs a key).
    /// </summary>
    public string VoiceChoice
    {
        get { lock (_lock) return _data.VoiceChoice; }
        set { lock (_lock) { _data.VoiceChoice = value; Save(); } }
    }

    /// <summary>Speaking rate multiplier, 1.0 = normal.</summary>
    public double SpeakingRate
    {
        get { lock (_lock) return _data.SpeakingRate; }
        set { lock (_lock) { _data.SpeakingRate = value; Save(); } }
    }

    /// <summary>
    /// Zoom refinement for pointing: one extra small Claude request per point
    /// (~0.02 cent) for a much more precise pointer.
    /// </summary>
    public bool PrecisePointingEnabled
    {
        get { lock (_lock) return _data.PrecisePointingEnabled; }
        set { lock (_lock) { _data.PrecisePointingEnabled = value; Save(); } }
    }

    /// <summary>How the explanation board is generated: "abo" (Claude Code CLI, free) or "sonnet-api".</summary>
    public string BoardGeneratorChoice
    {
        get { lock (_lock) return _data.BoardGeneratorChoice; }
        set { lock (_lock) { _data.BoardGeneratorChoice = value; Save(); } }
    }

    public bool OnboardingComplete
    {
        get { lock (_lock) return _data.OnboardingComplete; }
        set { lock (_lock) { _data.OnboardingComplete = value; Save(); } }
    }

    /// <summary>
    /// Hides HeyDanis's idle cursor-following overlay while still allowing
    /// explicit AI pointing/drawing after a voice turn.
    /// </summary>
    public bool GamerModeEnabled
    {
        get { lock (_lock) return _data.GamerModeEnabled; }
        set { lock (_lock) { _data.GamerModeEnabled = value; Save(); } }
    }

    /// <summary>
    /// NAudio MMDevice ID of the user-selected input (microphone) device.
    /// Empty string means "use whatever Windows currently considers the default".
    /// </summary>
    public string MicrophoneDeviceId
    {
        get { lock (_lock) return _data.MicrophoneDeviceId; }
        set { lock (_lock) { _data.MicrophoneDeviceId = value ?? ""; Save(); } }
    }

    /// <summary>
    /// NAudio MMDevice ID of the user-selected output (speaker/headphone) device.
    /// Empty string means "use whatever Windows currently considers the default".
    /// </summary>
    public string SpeakerDeviceId
    {
        get { lock (_lock) return _data.SpeakerDeviceId; }
        set { lock (_lock) { _data.SpeakerDeviceId = value ?? ""; Save(); } }
    }

    private SettingsData Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new SettingsData();

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SettingsStore: failed to load settings: {ex.Message}");
            return new SettingsData();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_data, options);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SettingsStore: failed to save settings: {ex.Message}");
        }
    }

    internal class SettingsData
    {
        [JsonPropertyName("llmProvider")]
        public string LlmProvider { get; set; } = "anthropic";

        [JsonPropertyName("llmModel")]
        public string LlmModel { get; set; } = "claude-haiku-5-5";

        [JsonPropertyName("elevenLabsVoiceId")]
        // Default to Adam — one of ElevenLabs' built-in default voices that free
        // API tier users are allowed to synthesize. Library voices (like the
        // previous default) require a paid plan and 402 at runtime.
        public string ElevenLabsVoiceId { get; set; } = "pNInz6obpgDQGcFmaJgB";

        [JsonPropertyName("speechRecognitionChoice")]
        public string SpeechRecognitionChoice { get; set; } = "whisper:small";

        [JsonPropertyName("voiceChoice")]
        public string VoiceChoice { get; set; } = "piper:thorsten-medium";

        [JsonPropertyName("speakingRate")]
        public double SpeakingRate { get; set; } = 1.05;

        [JsonPropertyName("precisePointingEnabled")]
        public bool PrecisePointingEnabled { get; set; } = true;

        [JsonPropertyName("boardGeneratorChoice")]
        public string BoardGeneratorChoice { get; set; } = "abo";

        [JsonPropertyName("onboardingComplete")]
        public bool OnboardingComplete { get; set; }

        [JsonPropertyName("gamerModeEnabled")]
        public bool GamerModeEnabled { get; set; }

        [JsonPropertyName("microphoneDeviceId")]
        public string MicrophoneDeviceId { get; set; } = "";

        [JsonPropertyName("speakerDeviceId")]
        public string SpeakerDeviceId { get; set; } = "";
    }
}
