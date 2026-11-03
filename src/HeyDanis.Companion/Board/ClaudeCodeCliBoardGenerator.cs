using System.IO;

namespace HeyDanis.Companion.Board;

/// <summary>
/// Board via the user's Claude Code CLI and Team subscription: no API costs, a bit slower
/// (the CLI starts up and Sonnet writes a whole page, typically 20–60 s).
/// </summary>
public sealed class ClaudeCodeCliBoardGenerator : IBoardGenerator
{
    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(4);

    private readonly string _workingDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "heyDanis", "board");

    public string WaitingHint => "über dein Claude-Abo (kostenlos, dauert ca. 20–60 Sekunden)";

    public async Task<string> GenerateHtmlAsync(BoardRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(_workingDirectory);
        var systemPromptPath = Path.Combine(_workingDirectory, "tafel-anweisungen.txt");
        await File.WriteAllTextAsync(systemPromptPath, BoardPrompt.SystemPrompt, ct).ConfigureAwait(false);

        // The CLI can't take image bytes on the command line; the screenshot is saved in the
        // working directory and Claude reads it with its Read tool (only tool allowed).
        string? screenshotNote = null;
        if (request.CursorScreen is { } screen)
        {
            var screenshotPath = Path.Combine(_workingDirectory, "bildschirm.jpg");
            await File.WriteAllBytesAsync(screenshotPath, screen.ImageBytes, ct).ConfigureAwait(false);
            screenshotNote = "Der aktuelle Bildschirm des Nutzers liegt als bildschirm.jpg im Arbeitsordner – " +
                             "lies ihn mit dem Read-Tool, wenn die Frage sich auf den Bildschirm bezieht.";
        }

        var arguments = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--model", "sonnet",
            "--tools", "Read",
            "--allowedTools", "Read",
            "--system-prompt-file", systemPromptPath,
            "--no-session-persistence",
            "--max-turns", "4",
        };

        var answer = await ClaudeCodeCli.RunPrintModeAsync(
            arguments,
            BoardPrompt.BuildUserPrompt(request, screenshotNote),
            _workingDirectory,
            GenerationTimeout,
            ct).ConfigureAwait(false);

        return BoardPrompt.ExtractHtml(answer);
    }
}
