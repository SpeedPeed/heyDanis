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
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromMinutes(30) };

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

        Directory.CreateDirectory(ModelsDirectory);
        var temporaryPath = targetPath + ".download";
        await DownloadToFileAsync(downloadUri, temporaryPath, modelDisplayName, progress, ct).ConfigureAwait(false);
        File.Move(temporaryPath, targetPath, overwrite: true);
        return targetPath;
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

    private async Task DownloadToFileAsync(
        Uri downloadUri,
        string destinationPath,
        string modelDisplayName,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        using var response = await _httpClient
            .GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var networkStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long bytesReceived = 0;
            var lastReportedPercent = -1;
            int bytesRead;
            while ((bytesRead = await networkStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                bytesReceived += bytesRead;

                var report = new ModelDownloadProgress(modelDisplayName, bytesReceived, totalBytes);
                // Only report whole-percent changes so the UI isn't flooded.
                if (report.Percent != lastReportedPercent)
                {
                    lastReportedPercent = report.Percent ?? -1;
                    progress?.Report(report);
                }
            }
        }
    }
}
