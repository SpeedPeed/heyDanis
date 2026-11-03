using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HeyDanis.LocalSpeech;

/// <summary>
/// Plays synthesized speech (raw PCM) on the user's chosen output device and can
/// be stopped instantly when the user starts talking again.
/// </summary>
internal sealed class PcmAudioPlayer : IDisposable
{
    private readonly string? _outputDeviceId;
    private readonly object _lock = new();

    /// <summary>Optional diagnostic log (device, format, duration, playback errors).</summary>
    public Action<string>? Log { get; set; }
    private IWavePlayer? _wavePlayer;
    private MMDevice? _wasapiDevice;
    private TaskCompletionSource<bool>? _playbackCompletion;

    public PcmAudioPlayer(string? outputDeviceId)
    {
        _outputDeviceId = outputDeviceId;
    }

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

    /// <summary>Plays mono float samples and returns when playback has finished.</summary>
    public Task PlayAsync(float[] samples, int sampleRate, CancellationToken ct)
    {
        var pcmBytes = FloatToPcm16(samples);
        return PlayAsync(pcmBytes, new WaveFormat(sampleRate, 16, 1), ct);
    }

    /// <summary>Plays already encoded PCM bytes in <paramref name="format"/>.</summary>
    public async Task PlayAsync(byte[] pcmBytes, WaveFormat format, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Stop();

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            var provider = new RawSourceWaveStream(new MemoryStream(pcmBytes), format);
            var (player, device) = CreateWavePlayer(_outputDeviceId);
            _wavePlayer = player;
            _wasapiDevice = device;
            _playbackCompletion = completion;
            player.PlaybackStopped += (_, stoppedArgs) =>
            {
                if (stoppedArgs.Exception is not null)
                {
                    Log?.Invoke($"[AUDIO] Wiedergabe-Fehler: {stoppedArgs.Exception.Message}");
                    completion.TrySetException(stoppedArgs.Exception);
                    return;
                }
                completion.TrySetResult(true);
            };
            player.Init(provider);
            player.Play();
            Log?.Invoke($"[AUDIO] spiele {pcmBytes.Length / Math.Max(1, format.AverageBytesPerSecond):0.0}s " +
                        $"{format.SampleRate}Hz/{format.BitsPerSample}bit über {(device is null ? "Windows-Standard (WaveOut)" : device.FriendlyName)}");
        }

        using var registration = ct.Register(() =>
        {
            Stop();
            completion.TrySetCanceled(ct);
        });

        await completion.Task.ConfigureAwait(false);
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_wavePlayer is not null)
            {
                try
                {
                    if (_wavePlayer.PlaybackState == PlaybackState.Playing)
                        _wavePlayer.Stop();
                    _wavePlayer.Dispose();
                }
                catch
                {
                    // Device may have vanished (headset unplugged) — nothing left to stop.
                }
                _wavePlayer = null;
            }

            if (_wasapiDevice is not null)
            {
                try { _wasapiDevice.Dispose(); } catch { /* best effort */ }
                _wasapiDevice = null;
            }

            _playbackCompletion?.TrySetResult(false);
            _playbackCompletion = null;
        }
    }

    internal static byte[] FloatToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)(clamped * short.MaxValue);
            bytes[2 * i] = (byte)(value & 0xFF);
            bytes[2 * i + 1] = (byte)((value >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>
    /// Same strategy as the ElevenLabs client: WaveOutEvent for the default device,
    /// WasapiOut when a specific endpoint was chosen, falling back to the default
    /// device if the saved one is gone.
    /// </summary>
    private static (IWavePlayer Player, MMDevice? Device) CreateWavePlayer(string? outputDeviceId)
    {
        if (string.IsNullOrWhiteSpace(outputDeviceId))
            return (new WaveOutEvent(), null);

        MMDevice? device = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(outputDeviceId);
            if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
            {
                device.Dispose();
                device = null;
            }
        }
        catch
        {
            device = null;
        }

        if (device is null)
            return (new WaveOutEvent(), null);

        try
        {
            return (new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100), device);
        }
        catch
        {
            device.Dispose();
            return (new WaveOutEvent(), null);
        }
    }

    public void Dispose() => Stop();
}
