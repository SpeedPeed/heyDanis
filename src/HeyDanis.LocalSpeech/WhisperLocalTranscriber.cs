using System.Text;
using System.Text.RegularExpressions;
using HeyDanis.Api;
using Whisper.net;

namespace HeyDanis.LocalSpeech;

/// <summary>
/// Whisper model sizes the user can pick. Quantized ggml files from the whisper.cpp
/// project: smaller and faster on CPU with almost the same accuracy.
/// </summary>
public sealed record WhisperModelChoice(string Id, string DisplayName, string FileName, long ApproximateBytes)
{
    private const string DownloadBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static readonly WhisperModelChoice Base = new("base", "Whisper Base (schnell, ~60 MB)", "ggml-base-q5_1.bin", 60_000_000);
    public static readonly WhisperModelChoice Small = new("small", "Whisper Small (empfohlen, ~190 MB)", "ggml-small-q5_1.bin", 190_000_000);
    public static readonly WhisperModelChoice LargeTurbo = new("large-turbo", "Whisper Large Turbo (beste Qualität, ~570 MB, braucht starken PC)", "ggml-large-v3-turbo-q5_0.bin", 574_000_000);

    public static IReadOnlyList<WhisperModelChoice> All { get; } = new[] { Base, Small, LargeTurbo };

    public Uri DownloadUri => new(DownloadBaseUrl + FileName);

    public static WhisperModelChoice FromId(string? id) =>
        All.FirstOrDefault(choice => choice.Id == id) ?? Small;
}

/// <summary>
/// Thrown when the user talks before the local model finished downloading/loading.
/// The message is shown to the user as-is.
/// </summary>
public sealed class LocalModelNotReadyException : SpeechServiceNotReadyException
{
    public LocalModelNotReadyException(string message) : base(message) { }
}

/// <summary>
/// Offline speech-to-text with whisper.cpp (via Whisper.net). Push-to-talk audio is
/// buffered while the hotkey is held and transcribed in one go after release — for
/// a few seconds of speech this takes well under a second on a modern CPU.
/// </summary>
public sealed class WhisperLocalTranscriber : ISpeechTranscriber, IDisposable
{
    private readonly LocalModelStore _modelStore;
    private readonly WhisperModelChoice _modelChoice;
    private readonly string _language;
    private readonly SemaphoreSlim _processorLock = new(1, 1);
    private readonly object _loadLock = new();

    private Task? _loadTask;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _currentPrompt;
    private bool _disposed;

    public WhisperLocalTranscriber(LocalModelStore modelStore, WhisperModelChoice modelChoice, string language = "de")
    {
        _modelStore = modelStore;
        _modelChoice = modelChoice;
        _language = language;
    }

    public bool IsReady => _factory is not null;

    /// <summary>
    /// Downloads (first run only) and loads the model. Safe to call repeatedly;
    /// started in the background at app launch so the first question is fast.
    /// </summary>
    public Task EnsureLoadedAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        lock (_loadLock)
        {
            if (_loadTask is null || _loadTask.IsFaulted || _loadTask.IsCanceled)
                _loadTask = LoadAsync(progress, ct);
            return _loadTask;
        }
    }

    private async Task LoadAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        var modelPath = await _modelStore.EnsureFileAsync(
            _modelChoice.FileName, _modelChoice.DownloadUri, "Spracherkennung", progress, ct).ConfigureAwait(false);
        _factory = await Task.Run(() => WhisperFactory.FromPath(modelPath), ct).ConfigureAwait(false);
    }

    public Task<ISpeechTranscriptionSession> StartSessionAsync(
        IReadOnlyList<string>? keyterms = null,
        CancellationToken ct = default)
    {
        if (!IsReady)
        {
            // Kick off loading in case the background warm-up failed (e.g. no internet at launch).
            _ = EnsureLoadedAsync(ct: CancellationToken.None);
            throw new LocalModelNotReadyException(
                "Die Spracherkennung wird noch geladen – einen Moment, dann nochmal probieren.");
        }

        ISpeechTranscriptionSession session = new WhisperTranscriptionSession(this, BuildPrompt(keyterms));
        return Task.FromResult(session);
    }

    /// <summary>
    /// Whisper's "initial prompt" nudges spelling of names/jargon (e.g. "heyDanis").
    /// </summary>
    private static string BuildPrompt(IReadOnlyList<string>? keyterms)
    {
        var builder = new StringBuilder("Gespräch auf Deutsch mit Fachbegriffen aus Schule und Programmierung");
        if (keyterms is { Count: > 0 })
        {
            builder.Append(": ").Append(string.Join(", ", keyterms));
        }
        builder.Append('.');
        return builder.ToString();
    }

    internal async Task<string> TranscribeAsync(float[] samples, string prompt, CancellationToken ct)
    {
        await _processorLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var processor = GetOrCreateProcessor(prompt);
            var transcript = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                transcript.Append(segment.Text);
            }
            return WhisperTranscriptCleaner.Clean(transcript.ToString());
        }
        finally
        {
            _processorLock.Release();
        }
    }

    private WhisperProcessor GetOrCreateProcessor(string prompt)
    {
        if (_processor is not null && _currentPrompt == prompt)
            return _processor;

        _processor?.Dispose();
        _processor = _factory!.CreateBuilder()
            .WithLanguage(_language)
            .WithPrompt(prompt)
            .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
            .WithNoContext()
            .Build();
        _currentPrompt = prompt;
        return _processor;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _processor?.Dispose();
        _factory?.Dispose();
        _processorLock.Dispose();
    }
}

/// <summary>
/// Buffers the PCM audio of one push-to-talk press until release.
/// </summary>
internal sealed class WhisperTranscriptionSession : ISpeechTranscriptionSession
{
    private const int SampleRate = 16_000;

    /// <summary>Below this, it was an accidental tap — don't bother Whisper.</summary>
    private static readonly TimeSpan MinimumSpeechDuration = TimeSpan.FromMilliseconds(300);

    private readonly WhisperLocalTranscriber _transcriber;
    private readonly string _prompt;
    private readonly MemoryStream _pcmBuffer = new();
    private bool _isClosed;

    public WhisperTranscriptionSession(WhisperLocalTranscriber transcriber, string prompt)
    {
        _transcriber = transcriber;
        _prompt = prompt;
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcmFrame, CancellationToken ct = default)
    {
        lock (_pcmBuffer)
        {
            if (!_isClosed)
                _pcmBuffer.Write(pcmFrame.Span);
        }
        return Task.CompletedTask;
    }

    public async Task<string> RequestFinalTranscriptAsync(CancellationToken ct = default)
    {
        byte[] pcmBytes;
        lock (_pcmBuffer)
        {
            _isClosed = true;
            pcmBytes = _pcmBuffer.ToArray();
        }

        var samples = PcmConversion.Pcm16ToFloat(pcmBytes);
        if (samples.Length < SampleRate * MinimumSpeechDuration.TotalSeconds)
            return "";

        // Whisper works on 30-second windows and tends to hallucinate on very short
        // clips; padding to one second with silence makes short answers ("ja") reliable.
        if (samples.Length < SampleRate)
            Array.Resize(ref samples, SampleRate);

        return await _transcriber.TranscribeAsync(samples, _prompt, ct).ConfigureAwait(false);
    }

    public Task CancelAsync()
    {
        lock (_pcmBuffer)
        {
            _isClosed = true;
        }
        return Task.CompletedTask;
    }

    public void Dispose() => _pcmBuffer.Dispose();
}

internal static class PcmConversion
{
    /// <summary>Converts little-endian 16-bit PCM to floats in [-1, 1].</summary>
    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcmBytes)
    {
        var samples = new float[pcmBytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = (short)(pcmBytes[2 * i] | (pcmBytes[2 * i + 1] << 8));
            samples[i] = sample / 32768f;
        }
        return samples;
    }
}

/// <summary>
/// Removes Whisper's well-known hallucinations. On silence or noise, Whisper models
/// trained on German TV subtitles like to "hear" phrases such as
/// "Untertitel im Auftrag des ZDF" — those must never be sent to Claude.
/// </summary>
internal static class WhisperTranscriptCleaner
{
    private static readonly Regex BracketedNoise = new(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", RegexOptions.Compiled);

    private static readonly string[] HallucinationPhrases =
    {
        "untertitel im auftrag des zdf",
        "untertitel von stephanie geiges",
        "untertitelung des zdf",
        "untertitel der amara.org-community",
        "vielen dank fürs zuschauen",
        "vielen dank für's zuschauen",
        "danke fürs zuschauen",
        "bis zum nächsten mal",
        "copyright wdr",
        "swr 2020",
        "thanks for watching",
    };

    public static string Clean(string rawTranscript)
    {
        var text = BracketedNoise.Replace(rawTranscript, " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        var normalized = text.ToLowerInvariant().Trim(' ', '.', '!', '?', ',');
        foreach (var phrase in HallucinationPhrases)
        {
            if (normalized == phrase || normalized.StartsWith(phrase, StringComparison.Ordinal) && normalized.Length < phrase.Length + 25)
                return "";
        }
        return text;
    }
}
