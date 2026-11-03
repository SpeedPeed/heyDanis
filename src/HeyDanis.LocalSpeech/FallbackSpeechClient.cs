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

    public FallbackSpeechClient(PiperLocalSpeechClient preferredClient, ITextToSpeechClient fallbackClient)
    {
        _preferredClient = preferredClient;
        _fallbackClient = fallbackClient;
    }

    public bool IsPlaying => _preferredClient.IsPlaying || _fallbackClient.IsPlaying;

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (_preferredClient.IsReady)
        {
            try
            {
                await _preferredClient.SpeakAsync(text, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Piper broke (e.g. corrupt model file) — fall through to the Windows voice.
            }
        }

        await _fallbackClient.SpeakAsync(text, ct).ConfigureAwait(false);
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
