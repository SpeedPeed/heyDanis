using System.Net;
using HeyDanis.LocalSpeech;
using Xunit;

namespace HeyDanis.Tests;

public class LocalSpeechTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "HeyDanisLocalSpeechTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Pcm16ToFloat_ConvertsLittleEndianSamples()
    {
        // 0x4000 = 16384 → 0.5 ; 0xC000 = -16384 → -0.5
        var samples = PcmConversion.Pcm16ToFloat(new byte[] { 0x00, 0x40, 0x00, 0xC0 });

        Assert.Equal(2, samples.Length);
        Assert.Equal(0.5f, samples[0], 3);
        Assert.Equal(-0.5f, samples[1], 3);
    }

    [Fact]
    public void FloatToPcm16_ClampsOutOfRangeSamples()
    {
        var bytes = PcmAudioPlayer.FloatToPcm16(new[] { 2f, -2f });
        var first = BitConverter.ToInt16(bytes, 0);
        var second = BitConverter.ToInt16(bytes, 2);

        Assert.Equal(short.MaxValue, first);
        Assert.Equal(-short.MaxValue, second);
    }

    [Theory]
    [InlineData("Untertitel im Auftrag des ZDF, 2021", "")]
    [InlineData(" Vielen Dank fürs Zuschauen! ", "")]
    [InlineData("[Musik]", "")]
    [InlineData("Wie mache ich eine for-Schleife in Python?", "Wie mache ich eine for-Schleife in Python?")]
    [InlineData("Erklär mir das  (unverständlich)  bitte", "Erklär mir das bitte")]
    public void TranscriptCleaner_RemovesKnownHallucinations(string raw, string expected)
    {
        Assert.Equal(expected, WhisperTranscriptCleaner.Clean(raw));
    }

    [Fact]
    public void SplitIntoSentences_SplitsOnPunctuation()
    {
        var sentences = PiperLocalSpeechClient.SplitIntoSentences("Klick oben auf Datei. Dann auf Speichern! Fertig?");

        Assert.Equal(new[] { "Klick oben auf Datei.", "Dann auf Speichern!", "Fertig?" }, sentences);
    }

    [Fact]
    public void ModelChoices_FallBackToDefaults()
    {
        Assert.Equal(WhisperModelChoice.Small, WhisperModelChoice.FromId("unknown"));
        Assert.Equal(WhisperModelChoice.Base, WhisperModelChoice.FromId("base"));
        Assert.Equal(PiperVoiceChoice.ThorstenMedium, PiperVoiceChoice.FromId(null));
    }

    [Fact]
    public void DownloadProgress_ComputesPercent()
    {
        Assert.Equal(50, new ModelDownloadProgress("x", 50, 100).Percent);
        Assert.Null(new ModelDownloadProgress("x", 50, null).Percent);
    }

    [Fact]
    public async Task EnsureFileAsync_DownloadsOnceAndReportsProgress()
    {
        var handler = new CountingHandler(new byte[] { 1, 2, 3, 4 });
        var store = new LocalModelStore(_tempDir, new HttpClient(handler));
        var reports = new List<ModelDownloadProgress>();
        var progress = new SynchronousProgress(reports);

        var firstPath = await store.EnsureFileAsync("model.bin", new Uri("https://example.test/model.bin"), "Test", progress, CancellationToken.None);
        var secondPath = await store.EnsureFileAsync("model.bin", new Uri("https://example.test/model.bin"), "Test", progress, CancellationToken.None);

        Assert.Equal(firstPath, secondPath);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(firstPath));
        Assert.Equal(1, handler.RequestCount);
        Assert.False(File.Exists(firstPath + ".download"));
        Assert.Contains(reports, report => report.Percent == 100);
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameModel_DownloadOnlyOnce()
    {
        var handler = new CountingHandler(new byte[] { 9, 8, 7 }, delay: TimeSpan.FromMilliseconds(200));
        var store = new LocalModelStore(_tempDir, new HttpClient(handler));
        var uri = new Uri("https://example.test/shared.bin");

        var first = store.EnsureFileAsync("shared.bin", uri, "Test", null, CancellationToken.None);
        var second = store.EnsureFileAsync("shared.bin", uri, "Test", null, CancellationToken.None);
        var paths = await Task.WhenAll(first, second);

        Assert.Equal(paths[0], paths[1]);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PartialDownload_IsResumedWithRangeRequest()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "resume.bin.download"), new byte[] { 1, 2 });
        var handler = new RangeHandler(new byte[] { 1, 2, 3, 4, 5 });
        var store = new LocalModelStore(_tempDir, new HttpClient(handler));

        var path = await store.EnsureFileAsync("resume.bin", new Uri("https://example.test/resume.bin"), "Test", null, CancellationToken.None);

        Assert.Equal(2, handler.RequestedRangeStart);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(path));
    }

    private sealed class RangeHandler : HttpMessageHandler
    {
        private readonly byte[] _fullPayload;
        public long? RequestedRangeStart { get; private set; }

        public RangeHandler(byte[] fullPayload) => _fullPayload = fullPayload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedRangeStart = request.Headers.Range?.Ranges.First().From;
            if (RequestedRangeStart is { } start)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(_fullPayload[(int)start..]),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_fullPayload) });
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        private readonly TimeSpan _delay;
        private int _requestCount;
        public int RequestCount => _requestCount;

        public CountingHandler(byte[] payload, TimeSpan delay = default)
        {
            _payload = payload;
            _delay = delay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_payload) };
        }
    }

    /// <summary>Progress&lt;T&gt; posts to a sync context; tests need the reports immediately.</summary>
    private sealed class SynchronousProgress : IProgress<ModelDownloadProgress>
    {
        private readonly List<ModelDownloadProgress> _reports;
        public SynchronousProgress(List<ModelDownloadProgress> reports) => _reports = reports;
        public void Report(ModelDownloadProgress value) => _reports.Add(value);
    }
}
