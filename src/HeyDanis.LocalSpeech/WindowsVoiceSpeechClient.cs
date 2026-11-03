using HeyDanis.Api;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace HeyDanis.LocalSpeech;

/// <summary>
/// Fallback text-to-speech with the voices built into Windows (for German usually
/// "Stefan" – male – and "Katja"/"Hedda"). Needs no download, sounds more robotic
/// than Piper. Used when the user picks it or while the Piper voice is still loading.
/// </summary>
public sealed class WindowsVoiceSpeechClient : ITextToSpeechClient
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly PcmAudioPlayer _player;
    private CancellationTokenSource? _currentSpeechCts;

    public WindowsVoiceSpeechClient(string? outputDeviceId, double speakingRate = 1.0, string preferredLanguage = "de-DE")
    {
        _player = new PcmAudioPlayer(outputDeviceId);
        _synthesizer.Options.SpeakingRate = Math.Clamp(speakingRate, 0.5, 3.0);

        var voice = PickVoice(preferredLanguage);
        if (voice is not null)
            _synthesizer.Voice = voice;

        SelectedVoiceName = _synthesizer.Voice?.DisplayName ?? "Standard";
    }

    /// <summary>Optional diagnostic log, forwarded to the audio player.</summary>
    public Action<string>? Log
    {
        get => _player.Log;
        set => _player.Log = value;
    }

    /// <summary>Name of the voice in use, shown in the settings window.</summary>
    public string SelectedVoiceName { get; }

    public bool IsPlaying => _player.IsPlaying;

    /// <summary>Prefers a male voice in the requested language, then any voice in it.</summary>
    internal static VoiceInformation? PickVoice(string preferredLanguage)
    {
        var voicesInLanguage = SpeechSynthesizer.AllVoices
            .Where(voice => voice.Language.Equals(preferredLanguage, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return voicesInLanguage.FirstOrDefault(voice => voice.Gender == VoiceGender.Male)
            ?? voicesInLanguage.FirstOrDefault();
    }

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        using var speechCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _currentSpeechCts = speechCts;
        try
        {
            using var stream = await _synthesizer.SynthesizeTextToStreamAsync(text).AsTask(speechCts.Token).ConfigureAwait(false);
            var wavBytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size).AsTask(speechCts.Token).ConfigureAwait(false);
                reader.ReadBytes(wavBytes);
            }

            // The WinRT synthesizer returns a WAV file; decode its header with NAudio.
            using var wavReader = new WaveFileReader(new MemoryStream(wavBytes));
            var pcmBytes = new byte[wavReader.Length];
            var bytesRead = wavReader.Read(pcmBytes, 0, pcmBytes.Length);
            Array.Resize(ref pcmBytes, bytesRead);
            await _player.PlayAsync(pcmBytes, wavReader.WaveFormat, speechCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _currentSpeechCts = null;
        }
    }

    public void StopPlayback()
    {
        try { _currentSpeechCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _player.Stop();
    }

    public void Dispose()
    {
        StopPlayback();
        _player.Dispose();
        _synthesizer.Dispose();
    }
}
