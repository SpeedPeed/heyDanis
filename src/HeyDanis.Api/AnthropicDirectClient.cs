using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HeyDanis.Capture;

namespace HeyDanis.Api;

/// <summary>Token usage of one Claude request, used for the monthly cost display.</summary>
public sealed record LlmUsage(string Model, int InputTokens, int OutputTokens, int CacheReadInputTokens, int CacheCreationInputTokens);

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
    private readonly Action<LlmUsage>? _onUsageReported;

    /// <param name="onUsageReported">Called once per request with the token usage (for cost tracking).</param>
    public AnthropicDirectClient(
        string apiKey,
        string model = "claude-haiku-5-5",
        HttpClient? httpClient = null,
        Action<LlmUsage>? onUsageReported = null)
    {
        _model = model;
        _onUsageReported = onUsageReported;

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

        var json = JsonSerializer.Serialize(BuildRequestBody(_model, systemPrompt, messages));
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
        var usage = new UsageAccumulator();

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;

            // SSE lines: "data: {json}" or "data: [DONE]"
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line.Substring(6); // drop "data: "

            if (payload == "[DONE]") break;

            usage.Observe(payload);
            var textDelta = ExtractTextDelta(payload);
            if (textDelta is not null)
                yield return textDelta;
        }

        if (usage.HasData)
        {
            try { _onUsageReported?.Invoke(usage.ToUsage(_model)); }
            catch { /* cost tracking must never break a reply */ }
        }
    }

    /// <summary>
    /// Builds the request body. Voice answers are short, so we trade reasoning depth
    /// for speed and cost: Haiku runs without extended thinking (fastest, no thinking
    /// tokens billed); Sonnet 5.5 can't disable thinking, so it runs at low effort.
    /// </summary>
    internal static Dictionary<string, object> BuildRequestBody(string model, string systemPrompt, object messages)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = 1024,
            ["stream"] = true,
            ["system"] = systemPrompt,
            ["messages"] = messages,
        };

        if (model.StartsWith("claude-haiku", StringComparison.Ordinal))
        {
            body["thinking"] = new { type = "disabled" };
            body["output_config"] = new { effort = "low" };
        }
        else
        {
            body["output_config"] = new { effort = "low" };
        }

        return body;
    }

    /// <summary>
    /// Collects token counts from the SSE stream: input tokens arrive in
    /// <c>message_start</c>, the (cumulative) output count in <c>message_delta</c>.
    /// </summary>
    internal sealed class UsageAccumulator
    {
        private int _inputTokens;
        private int _outputTokens;
        private int _cacheReadInputTokens;
        private int _cacheCreationInputTokens;

        public bool HasData { get; private set; }

        public void Observe(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;

                JsonElement usageElement;
                switch (typeProp.GetString())
                {
                    case "message_start" when root.TryGetProperty("message", out var message)
                                              && message.TryGetProperty("usage", out usageElement):
                        Read(usageElement);
                        break;
                    case "message_delta" when root.TryGetProperty("usage", out usageElement):
                        Read(usageElement);
                        break;
                }
            }
            catch (JsonException)
            {
                // Ignore malformed events; text extraction handles its own errors.
            }
        }

        private void Read(JsonElement usage)
        {
            HasData = true;
            if (TryGetInt(usage, "input_tokens", out var input) && input > 0) _inputTokens = input;
            if (TryGetInt(usage, "output_tokens", out var output) && output > 0) _outputTokens = output;
            if (TryGetInt(usage, "cache_read_input_tokens", out var cacheRead) && cacheRead > 0) _cacheReadInputTokens = cacheRead;
            if (TryGetInt(usage, "cache_creation_input_tokens", out var cacheWrite) && cacheWrite > 0) _cacheCreationInputTokens = cacheWrite;
        }

        private static bool TryGetInt(JsonElement element, string name, out int value)
        {
            value = 0;
            return element.TryGetProperty(name, out var prop)
                   && prop.ValueKind == JsonValueKind.Number
                   && prop.TryGetInt32(out value);
        }

        public LlmUsage ToUsage(string model) =>
            new(model, _inputTokens, _outputTokens, _cacheReadInputTokens, _cacheCreationInputTokens);
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
