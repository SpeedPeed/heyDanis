using System.Threading.Channels;
using HeyDanis.Api;

namespace HeyDanis.Companion;

/// <summary>
/// Manages a queue of TTS sentences that play sequentially without gaps.
/// Sentences are enqueued as they arrive from the LLM stream (at sentence boundaries),
/// and each one is synthesized (Piper, Windows voice or ElevenLabs) and played in order. This overlaps TTS HTTP
/// requests with playback of prior sentences, cutting perceived latency.
/// </summary>
internal sealed class TtsPipeline : IAsyncDisposable
{
    private readonly ITextToSpeechClient _ttsClient;
    private readonly Channel<string> _sentenceChannel;
    private readonly CancellationTokenSource _pipelineCts;
    private readonly Task _consumerTask;
    private readonly Action<string>? _onSpeakStarted;
    private readonly Action<Exception>? _onSpeakFailed;

    /// <param name="onSpeakFailed">
    /// Called when a sentence can't be spoken (no audio device, voice broken …). The turn
    /// keeps going — the answer is still shown as text in the bubble.
    /// </param>
    public TtsPipeline(
        ITextToSpeechClient ttsClient,
        CancellationToken externalCt,
        Action<string>? onSpeakStarted = null,
        Action<Exception>? onSpeakFailed = null)
    {
        _ttsClient = ttsClient;
        _onSpeakStarted = onSpeakStarted;
        _onSpeakFailed = onSpeakFailed;
        _sentenceChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _consumerTask = ConsumeAsync(_pipelineCts.Token);
    }

    /// <summary>
    /// Enqueues a sentence to be spoken. Sentences play in the order they are enqueued.
    /// </summary>
    public void Enqueue(string sentence)
    {
        _sentenceChannel.Writer.TryWrite(sentence);
    }

    /// <summary>
    /// Signals that no more sentences will be enqueued. The pipeline will finish
    /// playing all queued sentences and then complete.
    /// </summary>
    public void Complete()
    {
        _sentenceChannel.Writer.TryComplete();
    }

    /// <summary>
    /// Waits for all queued sentences to finish playing.
    /// </summary>
    public async Task WaitForCompletionAsync()
    {
        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var sentence in _sentenceChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            _onSpeakStarted?.Invoke(sentence);
            try
            {
                await _ttsClient.SpeakAsync(sentence, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _onSpeakFailed?.Invoke(ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sentenceChannel.Writer.TryComplete();
        _pipelineCts.Cancel();
        try { await _consumerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
        _pipelineCts.Dispose();
    }
}
