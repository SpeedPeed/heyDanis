using HeyDanis.Api;
using HeyDanis.Capture;

namespace HeyDanis.Companion.Board;

/// <summary>What should go on the explanation board ("Erklär-Tafel").</summary>
/// <param name="Question">What the user said or typed.</param>
/// <param name="Topic">Claude's precise description of what to draw/explain.</param>
/// <param name="UpdateExisting">True when the user wants the current board changed, not a new one.</param>
/// <param name="PreviousHtml">The board currently shown (for updates), if any.</param>
/// <param name="History">Recent conversation, so "das" and "es" can be resolved.</param>
/// <param name="CursorScreen">Screenshot of the monitor the mouse is on (context), if any.</param>
public sealed record BoardRequest(
    string Question,
    string Topic,
    bool UpdateExisting,
    string? PreviousHtml,
    IReadOnlyList<Message> History,
    CapturedScreen? CursorScreen);

/// <summary>Creates the HTML for the explanation board.</summary>
public interface IBoardGenerator
{
    /// <summary>Shown while waiting, e.g. "über dein Claude-Abo".</summary>
    string WaitingHint { get; }

    Task<string> GenerateHtmlAsync(BoardRequest request, CancellationToken ct);
}

/// <summary>A problem the user can fix (message is German and shown as-is).</summary>
public sealed class BoardGenerationException : Exception
{
    public BoardGenerationException(string message, Exception? inner = null) : base(message, inner) { }
}
