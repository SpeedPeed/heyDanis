using System.Runtime.CompilerServices;
using Clicky.Capture;

namespace Clicky.Api;

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
