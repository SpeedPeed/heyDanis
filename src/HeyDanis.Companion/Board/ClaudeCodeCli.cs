using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HeyDanis.Companion.Board;

/// <summary>
/// Finds and runs the user's own Claude Code CLI in headless mode (<c>claude -p</c>). Runs
/// on the user's Claude subscription (Team plan) — no API costs. Shared by the board and,
/// later, the agent mode.
/// </summary>
public static class ClaudeCodeCli
{
    /// <summary>Where the official installers put the CLI on Windows, besides PATH.</summary>
    private static IEnumerable<string> CandidatePaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(directory.Trim(), "claude.exe");
            yield return Path.Combine(directory.Trim(), "claude.cmd");
        }
        yield return Path.Combine(home, ".local", "bin", "claude.exe");
        yield return Path.Combine(appData, "npm", "claude.cmd");
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "claude.exe");
        yield return Path.Combine(localAppData, "Programs", "claude", "claude.exe");
    }

    /// <summary>Full path of claude.exe / claude.cmd, or null if it isn't installed.</summary>
    public static string? FindExecutable()
    {
        foreach (var candidate in CandidatePaths())
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Invalid PATH entries — skip.
            }
        }
        return null;
    }

    public const string NotInstalledMessage =
        "Die Claude-Code-CLI ist nicht installiert. So geht's: PowerShell öffnen (Windows-Taste, „PowerShell“) und eingeben: " +
        "irm https://claude.ai/install.ps1 | iex – danach ein neues PowerShell-Fenster öffnen, „claude“ eingeben, " +
        "mit dem Team-Konto anmelden („Claude account with subscription“) und mit /exit beenden. Dann heyDanis neu starten.";

    public const string NotLoggedInMessage =
        "Die Claude-CLI ist nicht angemeldet. Öffne eine Eingabeaufforderung, gib „claude“ ein, " +
        "melde dich mit deinem Team-Konto an („Claude account with subscription“) und beende mit /exit.";

    /// <summary>
    /// Runs <c>claude -p</c> with <paramref name="prompt"/> on stdin and returns the final
    /// answer text. The subscription login is used: ANTHROPIC_API_KEY/AUTH_TOKEN are removed
    /// from the child environment, otherwise the CLI would bill the API key instead.
    /// </summary>
    public static async Task<string> RunPrintModeAsync(
        IReadOnlyList<string> arguments,
        string prompt,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var executable = FindExecutable() ?? throw new BoardGenerationException(NotInstalledMessage);
        Directory.CreateDirectory(workingDirectory);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // .cmd shims (npm installs) need cmd.exe; the native claude.exe runs directly.
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(executable);
        }
        else
        {
            startInfo.FileName = executable;
        }
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // Use the subscription, never an API key that happens to be set system-wide.
        startInfo.Environment.Remove("ANTHROPIC_API_KEY");
        startInfo.Environment.Remove("ANTHROPIC_AUTH_TOKEN");

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new BoardGenerationException("Claude hat zu lange gebraucht – probier es nochmal oder mit einer kürzeren Frage.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return InterpretJsonResult(stdout, stderr, process.ExitCode);
    }

    /// <summary>
    /// Reads the <c>--output-format json</c> result ({"type":"result","is_error":…,"result":"…"}),
    /// turning the typical failures into messages the user can act on.
    /// </summary>
    internal static string InterpretJsonResult(string stdout, string stderr, int exitCode)
    {
        // Only look for login problems in error output — a successful board about, say,
        // "authentication errors" must not be mistaken for one.
        if (LooksLikeLoginProblem(stderr))
            throw new BoardGenerationException(NotLoggedInMessage);

        var jsonStart = stdout.IndexOf('{');
        if (jsonStart >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout[jsonStart..]);
                var root = doc.RootElement;
                var isError = root.TryGetProperty("is_error", out var errorFlag) && errorFlag.ValueKind == JsonValueKind.True;
                var result = root.TryGetProperty("result", out var resultProp) ? resultProp.GetString() ?? "" : "";
                if (isError)
                {
                    if (LooksLikeLoginProblem(result))
                        throw new BoardGenerationException(NotLoggedInMessage);
                    if (result.Contains("limit", StringComparison.OrdinalIgnoreCase))
                        throw new BoardGenerationException($"Dein Claude-Abo-Limit ist gerade erreicht ({result}). Später nochmal probieren oder in den Einstellungen auf „Sonnet über API-Key“ umstellen.");
                    throw new BoardGenerationException($"Claude-CLI meldet einen Fehler: {Shorten(result)}");
                }
                return result;
            }
            catch (JsonException)
            {
                // Fall through to the generic error below.
            }
        }

        if (LooksLikeLoginProblem(stdout))
            throw new BoardGenerationException(NotLoggedInMessage);

        throw new BoardGenerationException(
            $"Die Claude-CLI hat nicht wie erwartet geantwortet (Exit-Code {exitCode}): {Shorten(stderr.Length > 0 ? stderr : stdout)}");
    }

    private static bool LooksLikeLoginProblem(string text) =>
        text.Contains("/login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)
        || text.Contains("authentication", StringComparison.OrdinalIgnoreCase) && text.Contains("error", StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + " …";
    }
}
