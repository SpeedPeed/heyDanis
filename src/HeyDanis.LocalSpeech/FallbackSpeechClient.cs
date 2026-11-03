using HeyDanis.Api;

namespace HeyDanis.LocalSpeech;

/// <summary>
/// Uses the Piper voice once it is downloaded and loaded, and the built-in Windows
/// voice until then (or if Piper fails). This way heyDanis can talk right after the
/// very first start, while the ~70 MB voice is still downloading in the background.
/// </summary>
public sealed class FallbackSpeechClient : ITextToSpeechClient
{
    private readonly PiperLocalSpeechClient _preferredClient;
    private readonly ITextToSpeechClient _fallbackClient;
    private readonly Action<string>? _log;

    public FallbackSpeechClient(PiperLocalSpeechClient preferredClient, ITextToSpeechClient fallbackClient, Action<string>? log = null)
    {
        _preferredClient = preferredClient;
        _fallbackClient = fallbackClient;
        _log = log;
    }

    public bool IsPlaying => _preferredClient.IsPlaying || _fallbackClient.IsPlaying;

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (_preferredClient.IsReady)
        {
            try
            {
                _log?.Invoke($"[TTS] Piper spricht {text.Length} Zeichen");
                await _preferredClient.SpeakAsync(text, ct).ConfigureAwait(false);
                _log?.Invoke("[TTS] Piper fertig");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Piper broke (e.g. corrupt model file) — fall through to the Windows voice.
                _log?.Invoke($"[TTS] Piper-Fehler, nehme Windows-Stimme: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            _log?.Invoke("[TTS] Piper noch nicht bereit, nehme Windows-Stimme");
        }

        await _fallbackClient.SpeakAsync(text, ct).ConfigureAwait(false);
        _log?.Invoke("[TTS] Windows-Stimme fertig");
    }

    public void StopPlayback()
    {
        _preferredClient.StopPlayback();
        _fallbackClient.StopPlayback();
    }

    public void Dispose()
    {
        _preferredClient.Dispose();
        _fallbackClient.Dispose();
    }
}
