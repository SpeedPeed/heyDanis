using System.Text;
using HeyDanis.Api;

namespace HeyDanis.Companion.Board;

/// <summary>
/// Board via Claude Sonnet and the user's API key: fast (a few seconds), costs a few cents
/// per board. Optional alternative to the subscription route.
/// </summary>
public sealed class ApiBoardGenerator : IBoardGenerator
{
    private readonly ILlmClient _sonnetClient;

    public ApiBoardGenerator(string apiKey, Action<LlmUsage>? onUsageReported)
    {
        _sonnetClient = new AnthropicDirectClient(
            apiKey,
            model: "claude-sonnet-5-5",
            onUsageReported: onUsageReported,
            maxTokens: 16000,
            effort: "medium");
    }

    public string WaitingHint => "mit Claude Sonnet über deinen API-Key (ca. 2–5 Cent)";

    public async Task<string> GenerateHtmlAsync(BoardRequest request, CancellationToken ct)
    {
        var screens = request.CursorScreen is null
            ? Array.Empty<HeyDanis.Capture.CapturedScreen>()
            : new[] { request.CursorScreen };
        var screenshotNote = request.CursorScreen is null ? null : "Das Bild oben ist der aktuelle Bildschirm des Nutzers.";

        var output = new StringBuilder();
        await foreach (var delta in _sonnetClient.SendAsync(
            Array.Empty<Message>(),
            screens,
            BoardPrompt.SystemPrompt,
            BoardPrompt.BuildUserPrompt(request, screenshotNote),
            ct).ConfigureAwait(false))
        {
            output.Append(delta);
        }
        return BoardPrompt.ExtractHtml(output.ToString());
    }
}
