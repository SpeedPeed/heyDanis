using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Clicky.Capture;

namespace Clicky.Api;

/// <summary>
/// Streams Claude responses token-by-token by calling api.anthropic.com directly
/// with the user's own API key. No proxy or worker involved.
/// </summary>
public sealed class AnthropicDirectClient : ILlmClient
{
    private static readonly Uri ApiBaseUri = new("https://api.anthropic.com");

    private static readonly object TlsWarmupLock = new();
    private static bool _hasStartedTlsWarmup;

    private readonly HttpClient _http;
    private readonly string _model;

    public AnthropicDirectClient(string apiKey, string model = "claude-sonnet-4-6", HttpClient? httpClient = null)
    {
        _model = model;

        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        // Set default headers for all requests through this client.
        if (httpClient is null)
        {
            _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }

        WarmUpTlsConnectionIfNeeded();
    }

    /// <summary>
    /// Sends a streaming request to Claude via api.anthropic.com and yields
    /// incremental text deltas as they arrive over the SSE stream.
    /// </summary>
    public async IAsyncEnumerable<string> SendAsync(
        IReadOnlyList<Message> history,
        IReadOnlyList<CapturedScreen> screens,
        string systemPrompt,
        string userText,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<object>();

        // Conversation history as alternating user/assistant turns
        foreach (var turn in history)
        {
            messages.Add(new { role = "user", content = turn.UserText });
            messages.Add(new { role = "assistant", content = turn.AssistantText });
        }

        // Current turn: interleaved image+label content blocks, then user text
        var contentBlocks = new List<object>();
        foreach (var screen in screens)
        {
            contentBlocks.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = DetectImageMediaType(screen.ImageBytes),
                    data = Convert.ToBase64String(screen.ImageBytes)
                }
            });
            contentBlocks.Add(new { type = "text", text = screen.Label });
        }
        contentBlocks.Add(new { type = "text", text = userText });
        messages.Add(new { role = "user", content = contentBlocks });

        var body = new
        {
            model = _model,
            max_tokens = 1024,
            stream = true,
            system = systemPrompt,
            messages
        };

        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        // Set auth headers per-request so tests with custom HttpClient work.
        if (!request.Headers.Contains("x-api-key"))
        {
            // Headers are on the default client; no per-request override needed.
        }

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Claude API error ({(int)response.StatusCode}): {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;

            // SSE lines: "data: {json}" or "data: [DONE]"
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line.Substring(6); // drop "data: "

            if (payload == "[DONE]") break;

            var textDelta = ExtractTextDelta(payload);
            if (textDelta is not null)
                yield return textDelta;
        }
    }

    /// <summary>
    /// Detects image media type by inspecting magic bytes.
    /// PNG signature: 89 50 4E 47; default JPEG.
    /// </summary>
    internal static string DetectImageMediaType(ReadOnlySpan<byte> imageData)
    {
        if (imageData.Length >= 4 &&
            imageData[0] == 0x89 &&
            imageData[1] == 0x50 &&
            imageData[2] == 0x4E &&
            imageData[3] == 0x47)
        {
            return "image/png";
        }
        return "image/jpeg";
    }

    /// <summary>
    /// Extracts the text delta from a <c>content_block_delta</c> SSE event payload.
    /// Returns <c>null</c> for events we don't care about.
    /// </summary>
    internal static string? ExtractTextDelta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "content_block_delta" &&
                root.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("type", out var deltaType) &&
                deltaType.GetString() == "text_delta" &&
                delta.TryGetProperty("text", out var text))
            {
                return text.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — skip this event
        }
        return null;
    }

    /// <summary>
    /// Fires a HEAD request to pre-establish TLS, mirroring
    /// <c>warmUpTLSConnectionIfNeeded</c> in ClaudeAPI.swift.
    /// </summary>
    private void WarmUpTlsConnectionIfNeeded()
    {
        lock (TlsWarmupLock)
        {
            if (_hasStartedTlsWarmup) return;
            _hasStartedTlsWarmup = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, ApiBaseUri);
                req.Headers.ConnectionClose = false;
                await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Failure is fine — this is purely an optimization
            }
        });
    }
}
