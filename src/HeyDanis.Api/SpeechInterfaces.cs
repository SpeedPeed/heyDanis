namespace HeyDanis.Api;

/// <summary>
/// Turns push-to-talk audio into text. Implemented by the cloud AssemblyAI
/// streaming client and by the local Whisper transcriber, so the companion
/// pipeline does not care where speech recognition happens.
/// </summary>
public interface ISpeechTranscriber
{
    /// <summary>
    /// Opens a new session for one push-to-talk utterance.
    /// </summary>
    /// <param name="keyterms">Words the recognizer should favour (names, jargon).</param>
    Task<ISpeechTranscriptionSession> StartSessionAsync(
        IReadOnlyList<string>? keyterms = null,
        CancellationToken ct = default);
}

/// <summary>
/// One utterance: audio frames go in while the hotkey is held,
/// the final transcript comes out after release.
/// </summary>
public interface ISpeechTranscriptionSession : IDisposable
{
    /// <summary>Sends one frame of 16 kHz, 16-bit, mono PCM audio.</summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> pcmFrame, CancellationToken ct = default);

    /// <summary>Stops accepting audio and returns the full transcript.</summary>
    Task<string> RequestFinalTranscriptAsync(CancellationToken ct = default);

    /// <summary>Aborts the session without producing a transcript.</summary>
    Task CancelAsync();
}

/// <summary>
/// Speaks text out loud. Implemented by ElevenLabs (cloud), Piper (local)
/// and the built-in Windows voices.
/// </summary>
public interface ITextToSpeechClient : IDisposable
{
    /// <summary>Whether audio is currently playing.</summary>
    bool IsPlaying { get; }

    /// <summary>Synthesizes and plays <paramref name="text"/>; returns when playback ends or is cancelled.</summary>
    Task SpeakAsync(string text, CancellationToken ct = default);

    /// <summary>Stops any playback immediately (used when the user starts talking again).</summary>
    void StopPlayback();
}

/// <summary>
/// Thrown when a speech service can't be used yet — typically a local model that is
/// still downloading. The message is written for the user and shown as-is.
/// </summary>
public class SpeechServiceNotReadyException : Exception
{
    public SpeechServiceNotReadyException(string message) : base(message) { }
}
