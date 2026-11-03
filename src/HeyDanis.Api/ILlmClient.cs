using System.Runtime.CompilerServices;
using HeyDanis.Capture;

namespace HeyDanis.Api;

/// <summary>
/// Common interface for streaming LLM responses. Implementations talk directly
/// to a provider's API (Anthropic, z.ai, etc.) using the caller's own key.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Sends a streaming request and yields incremental text deltas as they arrive.
    /// </summary>
    IAsyncEnumerable<string> SendAsync(
        IReadOnlyList<Message> history,
        IReadOnlyList<CapturedScreen> screens,
        string systemPrompt,
        string userText,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="SendAsync(IReadOnlyList{Message}, IReadOnlyList{CapturedScreen}, string, string, CancellationToken)"/>,
    /// but asks the provider to return JSON matching <paramref name="responseJsonSchema"/>
    /// (structured outputs). Providers without that feature just ignore the schema.
    /// </summary>
    IAsyncEnumerable<string> SendAsync(
        IReadOnlyList<Message> history,
        IReadOnlyList<CapturedScreen> screens,
        string systemPrompt,
        string userText,
        string? responseJsonSchema,
        CancellationToken ct = default)
        => SendAsync(history, screens, systemPrompt, userText, ct);
}
