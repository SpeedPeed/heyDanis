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
}
