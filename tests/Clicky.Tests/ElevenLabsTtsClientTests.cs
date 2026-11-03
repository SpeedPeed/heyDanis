using System.Net;
using System.Text;
using System.Text.Json;
using Clicky.Api;
using Xunit;

namespace Clicky.Tests;

public class ElevenLabsTtsClientTests
{
    [Fact]
    public async Task Constructor_FiresHeadRequestToElevenLabsForTlsWarmup()
    {
        ElevenLabsTtsClient.ResetTlsWarmupForTesting();

        var handler = new RecordingHttpMessageHandler();
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var client = new ElevenLabsTtsClient("test-key", "test-voice-id", httpClient);

        // Wait up to 1 second for the background warmup HEAD request
        for (int i = 0; i < 20 && handler.Requests.Count == 0; i++)
            await Task.Delay(50);

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Head, req.Method);
        Assert.Equal("https://api.elevenlabs.io/", req.RequestUri!.ToString());
    }

    [Fact]
    public void IsPlaying_WhenNoPlayback_ReturnsFalse()
    {
        using var client = new ElevenLabsTtsClient("test-key", "test-voice-id");
        Assert.False(client.IsPlaying);
    }

    [Fact]
    public void StopPlayback_WhenNotPlaying_DoesNotThrow()
    {
        using var client = new ElevenLabsTtsClient("test-key", "test-voice-id");
        client.StopPlayback(); // Should be a no-op
    }

    [Fact]
    public void Dispose_WhenNotPlaying_DoesNotThrow()
    {
        var client = new ElevenLabsTtsClient("test-key", "test-voice-id");
        client.Dispose(); // Should clean up without error
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var client = new ElevenLabsTtsClient("test-key", "test-voice-id");
        client.Dispose();
        client.Dispose(); // Second dispose is a no-op
    }

    [Fact]
    public void TtsRequest_SerializesCorrectJsonShape()
    {
        var request = new TtsRequest
        {
            Text = "hello from clicky",
            ModelId = "eleven_flash_v2_5",
            VoiceSettings = new VoiceSettings { Stability = 0.5, SimilarityBoost = 0.75 }
        };

        var json = JsonSerializer.Serialize(request);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("hello from clicky", root.GetProperty("text").GetString());
        Assert.Equal("eleven_flash_v2_5", root.GetProperty("model_id").GetString());
        Assert.Equal(0.5, root.GetProperty("voice_settings").GetProperty("stability").GetDouble());
        Assert.Equal(0.75, root.GetProperty("voice_settings").GetProperty("similarity_boost").GetDouble());
    }

    [Fact]
    public async Task SpeakAsync_SendsDirectPostWithCorrectUrlAndHeaders()
    {
        var handler = new CapturingHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0xFF, 0xFB, 0x90, 0x00 })
        });
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var client = new ElevenLabsTtsClient("my-eleven-key", "pNInz6obpgDQGcFmaJgB", httpClient);

        try
        {
            await client.SpeakAsync("hello", CancellationToken.None);
        }
        catch
        {
            // MP3 playback will fail with fake data — that's fine, we're testing the HTTP request
        }

        Assert.NotNull(handler.CapturedRequest);
        Assert.Equal(HttpMethod.Post, handler.CapturedRequest!.Method);
        Assert.Equal(
            "https://api.elevenlabs.io/v1/text-to-speech/pNInz6obpgDQGcFmaJgB/stream",
            handler.CapturedRequest.RequestUri!.ToString());
        Assert.True(handler.CapturedRequest.Headers.TryGetValues("xi-api-key", out var apiKeyValues));
        Assert.Equal("my-eleven-key", apiKeyValues!.First());
        Assert.Contains("audio/mpeg", handler.CapturedRequest.Headers.Accept.ToString());
    }

    [Fact]
    public async Task SpeakAsync_DifferentVoiceId_UsesCorrectUrl()
    {
        var handler = new CapturingHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0xFF, 0xFB, 0x90, 0x00 })
        });
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var client = new ElevenLabsTtsClient("key", "my-custom-voice", httpClient);

        try { await client.SpeakAsync("hi", CancellationToken.None); } catch { }

        Assert.NotNull(handler.CapturedRequest);
        Assert.Contains("/my-custom-voice/stream", handler.CapturedRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SpeakAsync_ThrowsOnCancellation()
    {
        using var client = new ElevenLabsTtsClient("test-key", "test-voice-id");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.SpeakAsync("test", cts.Token));
    }

    [Fact]
    public async Task PlayMp3Async_WithCancellation_StopsPlayback()
    {
        using var client = new ElevenLabsTtsClient("test-key", "test-voice-id");
        var cts = new CancellationTokenSource();

        // Generate a minimal valid MP3 frame (silence)
        // A real MP3 would be needed for actual playback, but we can test that
        // cancellation interrupts the wait correctly.
        // Use a tiny valid MP3 — NAudio will throw if it can't parse the header,
        // so we test that the cancellation path works with invalid data too.
        var invalidMp3 = new byte[] { 0xFF, 0xFB, 0x90, 0x00 }; // MP3 sync word + header

        // This will either fail to parse the MP3 (expected) or get cancelled
        // Either way, it should not hang
        cts.CancelAfter(100);
        try
        {
            await client.PlayMp3Async(invalidMp3, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or InvalidDataException or EndOfStreamException)
        {
            // Expected — invalid MP3 data or cancellation
        }

        Assert.False(client.IsPlaying);
    }

    [Fact]
    public async Task PlayStreamingMp3Async_StartsPlaybackBeforeFullDownload()
    {
        var mp3Data = GenerateTestMp3(durationSeconds: 2);
        using var client = new ElevenLabsTtsClient("test-key", "test-voice");

        var drip = new DripFeedStream(mp3Data, chunkSize: 1024, delayMs: 50);

        var playTask = client.PlayStreamingMp3Async(drip, CancellationToken.None);

        // Wait long enough for first frames to decode and playback to begin,
        // but not long enough for all chunks to arrive.
        // Total delivery time ≈ (mp3Data.Length / 1024) * 50ms. For ~16KB of MP3 ≈ 800ms.
        // Playback should start within ~200ms (first few frames + decode).
        await Task.Delay(500);

        Assert.True(client.IsPlaying, "Expected playback to start before all chunks delivered");
        Assert.False(drip.FullyConsumed, "Expected HTTP stream to still be delivering data");

        client.StopPlayback();
        try { await playTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PlayStreamingMp3Async_Cancellation_DisposesWithinOneSecond()
    {
        var mp3Data = GenerateTestMp3(durationSeconds: 3);
        using var client = new ElevenLabsTtsClient("test-key", "test-voice");

        var drip = new DripFeedStream(mp3Data, chunkSize: 1024, delayMs: 50);
        using var cts = new CancellationTokenSource(200);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await client.PlayStreamingMp3Async(drip, cts.Token);
        }
        catch (OperationCanceledException) { }
        sw.Stop();

        Assert.False(client.IsPlaying, "Playback should be stopped after cancellation");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Cleanup took {sw.ElapsedMilliseconds}ms, expected < 1000ms");

        // Dispose should succeed without issues (no resource leaks)
        client.Dispose();
    }

    /// <summary>
    /// Builds valid MPEG-1 Layer 3 frames (128 kbps, 44100 Hz, stereo).
    /// The frame bodies are zeroed (decoded as silence) but structurally valid
    /// enough for NAudio's Mp3Frame parser and the Windows ACM decoder.
    /// Each frame is 417 bytes and represents ~26 ms of audio.
    /// </summary>
    private static byte[] GenerateTestMp3(int durationSeconds)
    {
        // MPEG-1, Layer III, 128 kbps, 44100 Hz, stereo, no padding, no CRC
        // Frame size = floor(144 * 128000 / 44100) = 417 bytes
        const int frameSize = 417;
        const double frameDurationMs = 1152.0 / 44100 * 1000; // ~26.1 ms per frame
        int frameCount = (int)Math.Ceiling(durationSeconds * 1000.0 / frameDurationMs);

        var mp3Data = new byte[frameCount * frameSize];
        // Header bytes: FF FB 90 00
        // FF FB = sync word + MPEG1 + Layer3 + no CRC
        // 90 = bitrate index 9 (128kbps) + sample rate index 0 (44100) + no padding
        // 00 = stereo + no mode ext + no copyright + not original + no emphasis
        byte[] header = { 0xFF, 0xFB, 0x90, 0x00 };

        for (int i = 0; i < frameCount; i++)
        {
            Array.Copy(header, 0, mp3Data, i * frameSize, header.Length);
        }

        return mp3Data;
    }

    /// <summary>
    /// Stream that feeds data in fixed-size chunks with configurable delays,
    /// simulating a slow HTTP response.
    /// </summary>
    private sealed class DripFeedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly int _delayMs;
        private int _position;

        public bool FullyConsumed => _position >= _data.Length;

        public DripFeedStream(byte[] data, int chunkSize, int delayMs)
        {
            _data = data;
            _chunkSize = chunkSize;
            _delayMs = delayMs;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position >= _data.Length) return 0;
            await Task.Delay(_delayMs, ct);
            int toRead = Math.Min(Math.Min(buffer.Length, _chunkSize), _data.Length - _position);
            _data.AsSpan(_position, toRead).CopyTo(buffer.Span);
            _position += toRead;
            return toRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length) return 0;
            int toRead = Math.Min(Math.Min(count, _chunkSize), _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CapturingHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
