using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.Compression;

namespace Clicky.Api;

/// <summary>
/// Streams TTS audio from ElevenLabs directly via the ElevenLabs API,
/// mirroring ElevenLabsTTSClient.swift.
/// </summary>
public class ElevenLabsTtsClient : IDisposable
{
    private static readonly Uri ApiBaseUri = new("https://api.elevenlabs.io");

    private static readonly object TlsWarmupLock = new();
    private static bool _hasStartedTlsWarmup;

    private readonly HttpClient _httpClient;
    private readonly string _ttsUrl;
    private readonly string _apiKey;
    private readonly string? _outputDeviceId;
    private readonly object _lock = new();
    private IWavePlayer? _wavePlayer;
    private MMDevice? _wasapiDevice;
    private Mp3FileReader? _mp3Reader;
    private CancellationTokenSource? _streamingCts;
    private IMp3FrameDecompressor? _decompressor;

    public ElevenLabsTtsClient(string apiKey, string voiceId, HttpClient? httpClient = null)
        : this(apiKey, voiceId, outputDeviceId: null, httpClient)
    {
    }

    /// <summary>
    /// Creates an ElevenLabs TTS client that plays audio on a specific output
    /// endpoint identified by NAudio MMDevice ID. Pass <c>null</c> or an empty
    /// string to use the Windows default playback device. If the saved device
    /// can't be resolved (e.g. headphones unplugged since settings were saved),
    /// playback silently falls back to the default device — we never want a
    /// missing speaker to block the TTS reply.
    /// </summary>
    public ElevenLabsTtsClient(string apiKey, string voiceId, string? outputDeviceId, HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _ttsUrl = $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}/stream";
        _outputDeviceId = outputDeviceId;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        WarmUpTlsConnectionIfNeeded();
    }

    /// <summary>
    /// Fires a HEAD request to pre-establish TLS, mirroring
    /// <see cref="AnthropicDirectClient.WarmUpTlsConnectionIfNeeded"/>.
    /// </summary>
    private void WarmUpTlsConnectionIfNeeded()
    {
        lock (TlsWarmupLock)
        {
            if (_hasStartedTlsWarmup) return;
            _hasStartedTlsWarmup = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, ApiBaseUri);
                req.Headers.ConnectionClose = false;
                await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Failure is fine — this is purely an optimization
            }
        });
    }

    /// <summary>
    /// Resets the warmup flag so it can fire again. Only used by tests.
    /// </summary>
    internal static void ResetTlsWarmupForTesting()
    {
        lock (TlsWarmupLock)
        {
            _hasStartedTlsWarmup = false;
        }
    }

    /// <summary>
    /// Whether audio is currently playing.
    /// </summary>
    public bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                return _wavePlayer?.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    /// <summary>
    /// Sends text to ElevenLabs TTS directly, downloads the full MP3 response,
    /// and plays it through the default output device. Returns when playback finishes or is cancelled.
    /// </summary>
    public virtual async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var body = new TtsRequest
        {
            Text = text,
            ModelId = "eleven_flash_v2_5",
            VoiceSettings = new VoiceSettings { Stability = 0.5, SimilarityBoost = 0.75 }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _ttsUrl)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("xi-api-key", _apiKey);
        request.Headers.Accept.ParseAdd("audio/mpeg");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"ElevenLabs TTS failed ({(int)response.StatusCode}): {errorBody}");
        }

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await PlayStreamingMp3Async(httpStream, ct);
    }

    /// <summary>
    /// Plays an MP3 stream in real-time by decoding MP3 frames as they arrive and
    /// feeding PCM samples into a <see cref="BufferedWaveProvider"/>. Playback starts
    /// after the first MP3 frame is decoded (~50-100 ms), while the HTTP download
    /// continues in parallel.
    /// </summary>
    internal async Task PlayStreamingMp3Async(Stream mp3Stream, CancellationToken ct)
    {
        StopPlayback();

        var streamingCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, streamingCts.Token);
        var linkedToken = linkedCts.Token;

        lock (_lock)
        {
            _streamingCts = streamingCts;
        }

        BufferedWaveProvider? bufferedProvider = null;
        IMp3FrameDecompressor? decompressor = null;
        var accumulator = new MemoryStream();
        var readBuffer = new byte[4096];
        var decodeBuffer = new byte[16384];

        try
        {
            while (true)
            {
                linkedToken.ThrowIfCancellationRequested();
                var bytesRead = await mp3Stream.ReadAsync(readBuffer, linkedToken).ConfigureAwait(false);
                if (bytesRead == 0) break;

                // Append new data to the end of the accumulator
                long parsePos = accumulator.Position;
                accumulator.Seek(0, SeekOrigin.End);
                accumulator.Write(readBuffer, 0, bytesRead);
                accumulator.Position = parsePos;

                // Decode as many complete MP3 frames as possible
                while (accumulator.Position < accumulator.Length)
                {
                    long frameStart = accumulator.Position;
                    Mp3Frame? frame;
                    try { frame = Mp3Frame.LoadFromStream(accumulator); }
                    catch { frame = null; }

                    if (frame == null)
                    {
                        accumulator.Position = frameStart;
                        break;
                    }

                    if (decompressor == null)
                    {
                        // First frame determines the audio format
                        var mp3Format = new Mp3WaveFormat(
                            frame.SampleRate,
                            frame.ChannelMode == ChannelMode.Mono ? 1 : 2,
                            frame.FrameLength,
                            frame.BitRate);
                        decompressor = new AcmMp3FrameDecompressor(mp3Format);
                        bufferedProvider = new BufferedWaveProvider(decompressor.OutputFormat)
                        {
                            BufferDuration = TimeSpan.FromSeconds(10),
                            ReadFully = true
                        };

                        lock (_lock)
                        {
                            _decompressor = decompressor;
                            var (player, mmDevice) = CreateWavePlayer(_outputDeviceId);
                            _wavePlayer = player;
                            _wasapiDevice = mmDevice;
                            _wavePlayer.Init(bufferedProvider);
                            _wavePlayer.Play();
                        }
                    }

                    int decoded = decompressor.DecompressFrame(frame, decodeBuffer, 0);
                    if (decoded > 0)
                    {
                        // Backpressure: wait if the buffer is nearly full to avoid
                        // BufferedWaveProvider throwing "Buffer full" when data
                        // arrives faster than playback can consume it.
                        while (bufferedProvider!.BufferedBytes + decoded > bufferedProvider.BufferLength - 4096)
                        {
                            linkedToken.ThrowIfCancellationRequested();
                            await Task.Delay(20, linkedToken).ConfigureAwait(false);
                        }
                        bufferedProvider.AddSamples(decodeBuffer, 0, decoded);
                    }
                }
            }

            if (bufferedProvider == null) return;

            // HTTP stream finished — wait for buffered audio to finish playing
            while (bufferedProvider.BufferedBytes > 0)
            {
                linkedToken.ThrowIfCancellationRequested();
                await Task.Delay(50, linkedToken).ConfigureAwait(false);
            }

            // Brief pause for the audio device to output the last samples
            try { await Task.Delay(100, linkedToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* acceptable during drain */ }
        }
        finally
        {
            accumulator.Dispose();
            lock (_lock)
            {
                _streamingCts?.Dispose();
                _streamingCts = null;
            }
            StopPlayback();
        }
    }

    /// <summary>
    /// Immediately stops any current playback so a new utterance can interrupt.
    /// </summary>
    public virtual void StopPlayback()
    {
        lock (_lock)
        {
            // Cancel any in-flight streaming download before disposing the player
            try { _streamingCts?.Cancel(); }
            catch (ObjectDisposedException) { }

            DisposePlaybackResources();
        }
    }

    /// <summary>
    /// Plays MP3 audio bytes through the default output device and awaits completion.
    /// </summary>
    internal async Task PlayMp3Async(byte[] mp3Data, CancellationToken ct)
    {
        // Stop any existing playback first
        StopPlayback();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            var stream = new MemoryStream(mp3Data);
            _mp3Reader = new Mp3FileReader(stream);
            var (player, mmDevice) = CreateWavePlayer(_outputDeviceId);
            _wavePlayer = player;
            _wasapiDevice = mmDevice;
            _wavePlayer.Init(_mp3Reader);

            _wavePlayer.PlaybackStopped += (_, _) => tcs.TrySetResult(true);
            _wavePlayer.Play();
        }

        // Wait for playback to finish or cancellation
        using var reg = ct.Register(() =>
        {
            StopPlayback();
            tcs.TrySetCanceled(ct);
        });

        await tcs.Task;
    }

    private void DisposePlaybackResources()
    {
        if (_wavePlayer is not null)
        {
            if (_wavePlayer.PlaybackState == PlaybackState.Playing)
                _wavePlayer.Stop();
            _wavePlayer.Dispose();
            _wavePlayer = null;
        }

        // MMDevice is only held when we took the WasapiOut path. NAudio's
        // WasapiOut does not dispose the MMDevice it was constructed with,
        // so we have to release it ourselves after the player is gone.
        if (_wasapiDevice is not null)
        {
            try { _wasapiDevice.Dispose(); } catch { /* best effort */ }
            _wasapiDevice = null;
        }

        if (_decompressor is not null)
        {
            _decompressor.Dispose();
            _decompressor = null;
        }

        if (_mp3Reader is not null)
        {
            _mp3Reader.Dispose();
            _mp3Reader = null;
        }
    }

    /// <summary>
    /// Builds an <see cref="IWavePlayer"/> for the requested output endpoint.
    /// When no device is specified we keep the old <see cref="WaveOutEvent"/>
    /// path (stable across many machines). When a specific MMDevice is
    /// requested we use <see cref="WasapiOut"/>, which is the only NAudio
    /// player that can target an endpoint by ID. Any failure to resolve the
    /// saved device falls back to the default WaveOutEvent rather than
    /// silently breaking TTS playback. Returns the player plus the MMDevice
    /// it holds (or <c>null</c>) so the caller can release the device when
    /// playback finishes — NAudio's WasapiOut does not own its MMDevice.
    /// </summary>
    private static (IWavePlayer player, MMDevice? device) CreateWavePlayer(string? outputDeviceId)
    {
        if (string.IsNullOrWhiteSpace(outputDeviceId))
        {
            return (new WaveOutEvent(), null);
        }

        MMDevice? mmDevice = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            try
            {
                mmDevice = enumerator.GetDevice(outputDeviceId);
                if (mmDevice.DataFlow != DataFlow.Render || mmDevice.State != DeviceState.Active)
                {
                    mmDevice.Dispose();
                    mmDevice = null;
                }
            }
            catch
            {
                mmDevice = null;
            }
        }
        catch
        {
            mmDevice = null;
        }

        if (mmDevice is null)
        {
            return (new WaveOutEvent(), null);
        }

        try
        {
            var player = new WasapiOut(mmDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
            return (player, mmDevice);
        }
        catch
        {
            mmDevice.Dispose();
            return (new WaveOutEvent(), null);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposePlaybackResources();
        }
        _httpClient.Dispose();
    }
}

internal sealed class TtsRequest
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("model_id")]
    public required string ModelId { get; init; }

    [JsonPropertyName("voice_settings")]
    public required VoiceSettings VoiceSettings { get; init; }
}

internal sealed class VoiceSettings
{
    [JsonPropertyName("stability")]
    public double Stability { get; init; }

    [JsonPropertyName("similarity_boost")]
    public double SimilarityBoost { get; init; }
}
