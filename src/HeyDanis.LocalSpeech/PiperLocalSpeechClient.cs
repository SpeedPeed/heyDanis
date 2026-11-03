using System.Text.RegularExpressions;
using System.Threading.Channels;
using HeyDanis.Api;
using SherpaOnnx;

namespace HeyDanis.LocalSpeech;

/// <summary>
/// Piper voices packaged for sherpa-onnx (https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models).
/// </summary>
public sealed record PiperVoiceChoice(string Id, string DisplayName, string ArchiveName, string OnnxFileName)
{
    private const string DownloadBaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/";

    public static readonly PiperVoiceChoice ThorstenMedium = new(
        "thorsten-medium", "Thorsten (männlich, schnell, ~70 MB)", "vits-piper-de_DE-thorsten-medium", "de_DE-thorsten-medium.onnx");

    public static readonly PiperVoiceChoice ThorstenHigh = new(
        "thorsten-high", "Thorsten HQ (männlich, natürlicher, ~115 MB)", "vits-piper-de_DE-thorsten-high", "de_DE-thorsten-high.onnx");

    public static IReadOnlyList<PiperVoiceChoice> All { get; } = new[] { ThorstenMedium, ThorstenHigh };

    public Uri DownloadUri => new(DownloadBaseUrl + ArchiveName + ".tar.bz2");

    public static PiperVoiceChoice FromId(string? id) =>
        All.FirstOrDefault(choice => choice.Id == id) ?? ThorstenMedium;
}

/// <summary>
/// Offline text-to-speech with a Piper voice running through sherpa-onnx.
/// Long answers are split into sentences: while sentence N plays, sentence N+1 is
/// already being synthesized, so speech starts quickly and has no gaps.
/// </summary>
public sealed class PiperLocalSpeechClient : ITextToSpeechClient
{
    private static readonly Regex SentenceSplitter = new(@"(?<=[.!?…:;])\s+", RegexOptions.Compiled);

    private readonly LocalModelStore _modelStore;
    private readonly PiperVoiceChoice _voiceChoice;
    private readonly float _speakingRate;
    private readonly PcmAudioPlayer _player;
    private readonly object _loadLock = new();
    private readonly SemaphoreSlim _synthesisLock = new(1, 1);

    private Task? _loadTask;
    private OfflineTts? _tts;
    private CancellationTokenSource? _currentSpeechCts;
    private bool _disposed;

    /// <param name="speakingRate">1.0 = normal; 1.15 = a bit faster (Piper's "length scale" inverse).</param>
    public PiperLocalSpeechClient(LocalModelStore modelStore, PiperVoiceChoice voiceChoice, float speakingRate, string? outputDeviceId)
    {
        _modelStore = modelStore;
        _voiceChoice = voiceChoice;
        _speakingRate = Math.Clamp(speakingRate, 0.6f, 1.8f);
        _player = new PcmAudioPlayer(outputDeviceId);
    }

    /// <summary>Optional diagnostic log, forwarded to the audio player.</summary>
    public Action<string>? Log
    {
        get => _player.Log;
        set => _player.Log = value;
    }

    public bool IsReady => _tts is not null;

    public bool IsPlaying => _player.IsPlaying;

    /// <summary>Downloads (first run only) and loads the voice in the background.</summary>
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
        var voiceDirectory = await _modelStore.EnsureTarBz2DirectoryAsync(
            _voiceChoice.ArchiveName, _voiceChoice.DownloadUri, "Stimme", progress, ct).ConfigureAwait(false);

        _tts = await Task.Run(() =>
        {
            var config = new OfflineTtsConfig();
            config.Model.Vits.Model = Path.Combine(voiceDirectory, _voiceChoice.OnnxFileName);
            config.Model.Vits.Tokens = Path.Combine(voiceDirectory, "tokens.txt");
            config.Model.Vits.DataDir = Path.Combine(voiceDirectory, "espeak-ng-data");
            config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            config.Model.Provider = "cpu";
            config.Model.Debug = 0;
            config.MaxNumSentences = 1;
            return new OfflineTts(config);
        }, ct).ConfigureAwait(false);
    }

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!IsReady)
        {
            _ = EnsureLoadedAsync(ct: CancellationToken.None);
            throw new LocalModelNotReadyException("Die Stimme wird noch geladen – die Antwort steht solange nur als Text da.");
        }

        using var speechCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _currentSpeechCts = speechCts;
        var speechToken = speechCts.Token;

        var sentences = SplitIntoSentences(text);
        var synthesizedAudio = Channel.CreateBounded<float[]>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
        var sampleRate = _tts!.SampleRate;

        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var sentence in sentences)
                {
                    speechToken.ThrowIfCancellationRequested();
                    var samples = await SynthesizeAsync(sentence, speechToken).ConfigureAwait(false);
                    await synthesizedAudio.Writer.WriteAsync(samples, speechToken).ConfigureAwait(false);
                }
                synthesizedAudio.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                synthesizedAudio.Writer.TryComplete(ex);
            }
        }, speechToken);

        try
        {
            await foreach (var samples in synthesizedAudio.Reader.ReadAllAsync(speechToken).ConfigureAwait(false))
            {
                await _player.PlayAsync(samples, sampleRate, speechToken).ConfigureAwait(false);
            }
            await producer.ConfigureAwait(false);
        }
        finally
        {
            _currentSpeechCts = null;
        }
    }

    private async Task<float[]> SynthesizeAsync(string sentence, CancellationToken ct)
    {
        await _synthesisLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _tts!.Generate(sentence, _speakingRate, 0).Samples, ct).ConfigureAwait(false);
        }
        finally
        {
            _synthesisLock.Release();
        }
    }

    internal static IReadOnlyList<string> SplitIntoSentences(string text) =>
        SentenceSplitter.Split(text.Trim())
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();

    public void StopPlayback()
    {
        try { _currentSpeechCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _player.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPlayback();
        _player.Dispose();
        _tts?.Dispose();
        _synthesisLock.Dispose();
    }
}
