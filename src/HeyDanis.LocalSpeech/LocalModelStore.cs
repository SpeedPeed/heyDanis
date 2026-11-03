using System.Formats.Tar;
using ICSharpCode.SharpZipLib.BZip2;

namespace HeyDanis.LocalSpeech;

/// <summary>
/// Progress report for a model download, shown in the panel ("Stimme wird geladen … 42 %").
/// </summary>
public readonly record struct ModelDownloadProgress(string ModelDisplayName, long BytesReceived, long? TotalBytes)
{
    /// <summary>0–100, or null when the server didn't send a size.</summary>
    public int? Percent => TotalBytes is > 0 ? (int)(BytesReceived * 100 / TotalBytes.Value) : null;
}

/// <summary>
/// Downloads and caches the local speech models under <c>%APPDATA%\heyDanis\models</c>.
/// Models are big (60–600 MB), so they are fetched once on first use, written to a
/// temporary file first and only moved into place when complete — an interrupted
/// download never leaves a half-written model behind.
/// </summary>
public sealed class LocalModelStore
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromHours(3) };

    // Downloads currently running, keyed by target path. Every settings save creates new
    // speech clients; without this they would all start downloading into the same
    // temporary file at once, block each other, and the model would never finish.
    private static readonly object InFlightLock = new();
    private static readonly Dictionary<string, InFlightDownload> InFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

    private sealed class InFlightDownload
    {
        public required Task<string> Task { get; init; }
        public List<IProgress<ModelDownloadProgress>> Subscribers { get; } = new();
        public ModelDownloadProgress? LastProgress { get; set; }
    }

    /// <summary>Latest progress of a running download for <paramref name="targetPath"/>, if any.</summary>
    public static ModelDownloadProgress? GetRunningDownloadProgress(string targetPath)
    {
        lock (InFlightLock)
        {
            return InFlightDownloads.TryGetValue(targetPath, out var download) ? download.LastProgress : null;
        }
    }

    private readonly HttpClient _httpClient;

    public LocalModelStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "heyDanis", "models"))
    {
    }

    public LocalModelStore(string modelsDirectory, HttpClient? httpClient = null)
    {
        ModelsDirectory = modelsDirectory;
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public string ModelsDirectory { get; }

    /// <summary>
    /// Returns the local path of a single-file model, downloading it first if needed.
    /// </summary>
    public async Task<string> EnsureFileAsync(
        string fileName,
        Uri downloadUri,
        string modelDisplayName,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        var targetPath = Path.Combine(ModelsDirectory, fileName);
        if (File.Exists(targetPath))
            return targetPath;

        return await RunOnceAsync(targetPath, progress, async sharedProgress =>
        {
            Directory.CreateDirectory(ModelsDirectory);
            var temporaryPath = targetPath + ".download";
            await DownloadToFileAsync(downloadUri, temporaryPath, modelDisplayName, sharedProgress, CancellationToken.None).ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
            return targetPath;
        }).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="work"/> once per target; concurrent callers share the same task
    /// and all receive its progress reports.
    /// </summary>
    private static Task<string> RunOnceAsync(
        string targetKey,
        IProgress<ModelDownloadProgress>? progress,
        Func<IProgress<ModelDownloadProgress>, Task<string>> work)
    {
        lock (InFlightLock)
        {
            if (InFlightDownloads.TryGetValue(targetKey, out var running))
            {
                if (progress is not null)
                {
                    running.Subscribers.Add(progress);
                    if (running.LastProgress is { } last)
                        progress.Report(last);
                }
                return running.Task;
            }

            InFlightDownload? download = null;
            var broadcast = new BroadcastProgress(report =>
            {
                List<IProgress<ModelDownloadProgress>> subscribers;
                lock (InFlightLock)
                {
                    download!.LastProgress = report;
                    subscribers = download.Subscribers.ToList();
                }
                foreach (var subscriber in subscribers)
                    subscriber.Report(report);
            });

            var task = Task.Run(async () =>
            {
                try
                {
                    return await work(broadcast).ConfigureAwait(false);
                }
                finally
                {
                    lock (InFlightLock)
                    {
                        InFlightDownloads.Remove(targetKey);
                    }
                }
            });
            download = new InFlightDownload { Task = task };
            if (progress is not null)
                download.Subscribers.Add(progress);
            InFlightDownloads[targetKey] = download;
            return task;
        }
    }

    private sealed class BroadcastProgress : IProgress<ModelDownloadProgress>
    {
        private readonly Action<ModelDownloadProgress> _onReport;
        public BroadcastProgress(Action<ModelDownloadProgress> onReport) => _onReport = onReport;
        public void Report(ModelDownloadProgress value) => _onReport(value);
    }

    /// <summary>
    /// Returns the local directory of a model shipped as <c>.tar.bz2</c> archive
    /// (the sherpa-onnx Piper voices), downloading and extracting it first if needed.
    /// </summary>
    /// <param name="directoryName">Top-level folder name inside the archive.</param>
    public async Task<string> EnsureTarBz2DirectoryAsync(
        string directoryName,
        Uri downloadUri,
        string modelDisplayName,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        var targetDirectory = Path.Combine(ModelsDirectory, directoryName);
        var completeMarkerPath = Path.Combine(targetDirectory, ".complete");
        if (File.Exists(completeMarkerPath))
            return targetDirectory;

        return await RunOnceAsync(targetDirectory, progress, sharedProgress =>
            DownloadAndExtractTarBz2Async(directoryName, downloadUri, modelDisplayName, targetDirectory, completeMarkerPath, sharedProgress))
            .WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> DownloadAndExtractTarBz2Async(
        string directoryName,
        Uri downloadUri,
        string modelDisplayName,
        string targetDirectory,
        string completeMarkerPath,
        IProgress<ModelDownloadProgress> progress)
    {
        var ct = CancellationToken.None;
        Directory.CreateDirectory(ModelsDirectory);
        var archivePath = Path.Combine(ModelsDirectory, directoryName + ".tar.bz2.download");
        await DownloadToFileAsync(downloadUri, archivePath, modelDisplayName, progress, ct).ConfigureAwait(false);

        // Extract into a scratch folder first, then swap it in, so a crash mid-extract
        // is retried cleanly on the next start.
        var extractDirectory = Path.Combine(ModelsDirectory, directoryName + ".extracting");
        if (Directory.Exists(extractDirectory))
            Directory.Delete(extractDirectory, recursive: true);
        Directory.CreateDirectory(extractDirectory);

        await Task.Run(() =>
        {
            using var archiveStream = File.OpenRead(archivePath);
            using var bzip2Stream = new BZip2InputStream(archiveStream);
            TarFile.ExtractToDirectory(bzip2Stream, extractDirectory, overwriteFiles: true);
        }, ct).ConfigureAwait(false);

        var extractedModelDirectory = Path.Combine(extractDirectory, directoryName);
        if (!Directory.Exists(extractedModelDirectory))
            throw new InvalidDataException($"Das Archiv für {modelDisplayName} hat einen unerwarteten Aufbau.");

        if (Directory.Exists(targetDirectory))
            Directory.Delete(targetDirectory, recursive: true);
        Directory.Move(extractedModelDirectory, targetDirectory);
        Directory.Delete(extractDirectory, recursive: true);
        File.Delete(archivePath);
        await File.WriteAllTextAsync(completeMarkerPath, DateTime.UtcNow.ToString("O"), ct).ConfigureAwait(false);
        return targetDirectory;
    }

    /// <summary>
    /// Downloads to <paramref name="destinationPath"/>, resuming a partial file from an
    /// earlier attempt (HTTP Range) — on slow internet, closing the app mid-download
    /// shouldn't mean starting over.
    /// </summary>
    private async Task DownloadToFileAsync(
        Uri downloadUri,
        string destinationPath,
        string modelDisplayName,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        var alreadyDownloadedBytes = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        if (alreadyDownloadedBytes > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(alreadyDownloadedBytes, null);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file is already complete (or bogus) — start over cleanly.
            File.Delete(destinationPath);
            await DownloadToFileAsync(downloadUri, destinationPath, modelDisplayName, progress, ct).ConfigureAwait(false);
            return;
        }
        response.EnsureSuccessStatusCode();

        var isResuming = response.StatusCode == System.Net.HttpStatusCode.PartialContent && alreadyDownloadedBytes > 0;
        if (!isResuming)
            alreadyDownloadedBytes = 0;

        long? totalBytes = response.Content.Headers.ContentLength is { } remainingBytes
            ? remainingBytes + alreadyDownloadedBytes
            : null;

        await using var networkStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var fileStream = new FileStream(
            destinationPath,
            isResuming ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1 << 16,
            useAsync: true))
        {
            var buffer = new byte[1 << 16];
            var bytesReceived = alreadyDownloadedBytes;
            int? lastReportedPercent = null;
            int bytesRead;
            while ((bytesRead = await networkStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                bytesReceived += bytesRead;

                var report = new ModelDownloadProgress(modelDisplayName, bytesReceived, totalBytes);
                // Only report whole-percent changes so the UI isn't flooded.
                if (report.Percent is null || report.Percent != lastReportedPercent)
                {
                    lastReportedPercent = report.Percent;
                    progress?.Report(report);
                }
            }
        }

        if (totalBytes is { } expected && new FileInfo(destinationPath).Length != expected)
            throw new IOException($"Download von {modelDisplayName} unvollständig – wird beim nächsten Start fortgesetzt.");
    }
}
