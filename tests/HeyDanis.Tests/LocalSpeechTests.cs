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

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        public int RequestCount { get; private set; }

        public CountingHandler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_payload) });
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
