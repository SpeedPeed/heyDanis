using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HeyDanis.Capture;

namespace HeyDanis.Api;

/// <summary>
/// The model would have downsized one of our screenshots (it is on a lower image-resolution
/// tier than assumed). The caller resizes the screenshots and retries.
/// </summary>
public sealed class ImageTooLargeForModelException : Exception
{
    public ImageTooLargeForModelException(string message) : base(message) { }
}

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
    public IAsyncEnumerable<string> SendAsync(
        IReadOnlyList<Message> history,
        IReadOnlyList<CapturedScreen> screens,
        string systemPrompt,
        string userText,
        CancellationToken ct = default)
        => SendAsync(history, screens, systemPrompt, userText, responseJsonSchema: null, ct);

    public async IAsyncEnumerable<string> SendAsync(
        IReadOnlyList<Message> history,
        IReadOnlyList<CapturedScreen> screens,
        string systemPrompt,
        string userText,
        string? responseJsonSchema,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = BuildMessages(history, screens, userText, includeImageTransformations: true);

        var response = await SendMessagesRequestAsync(
            BuildRequestBody(_model, systemPrompt, messages, responseJsonSchema: responseJsonSchema), ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Safety net: if the API rejects our speed/cost tuning parameters (e.g. a model
            // that doesn't accept disabled thinking), retry once with a plain request.
            if ((int)response.StatusCode == 400 && MentionsTuningParameter(errorBody)
                && !errorBody.Contains("would be downsized", StringComparison.OrdinalIgnoreCase))
            {
                response.Dispose();
                response = await SendMessagesRequestAsync(
                    BuildRequestBody(
                        _model,
                        systemPrompt,
                        BuildMessages(history, screens, userText, includeImageTransformations: false),
                        includeTuning: false),
                    ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                response.Dispose();
                if (statusCode == 400 && errorBody.Contains("would be downsized", StringComparison.OrdinalIgnoreCase))
                    throw new ImageTooLargeForModelException(ExtractApiErrorMessage(errorBody) ?? errorBody);
                throw new HttpRequestException(
                    $"Claude API error ({statusCode}): {errorBody}");
            }
        }

        using var responseScope = response;

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
    internal static Dictionary<string, object> BuildRequestBody(
        string model,
        string systemPrompt,
        object messages,
        bool includeTuning = true,
        string? responseJsonSchema = null)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = 1024,
            ["stream"] = true,
            ["system"] = systemPrompt,
            ["messages"] = messages,
        };

        if (!includeTuning)
        {
            return body;
        }

        var outputConfig = new Dictionary<string, object> { ["effort"] = "low" };
        if (responseJsonSchema is not null)
        {
            // Structured outputs: the API guarantees JSON matching the schema, so the
            // pointing protocol can't silently degrade into plain prose.
            outputConfig["format"] = new Dictionary<string, object>
            {
                ["type"] = "json_schema",
                ["schema"] = System.Text.Json.Nodes.JsonNode.Parse(responseJsonSchema)!,
            };
        }
        body["output_config"] = outputConfig;

        if (model.StartsWith("claude-haiku", StringComparison.Ordinal))
        {
            body["thinking"] = new { type = "disabled" };
        }

        return body;
    }

    /// <summary>
    /// Conversation history as alternating user/assistant turns, then the current turn:
    /// each screenshot followed by its label, then the user's question.
    /// </summary>
    private static List<object> BuildMessages(
        IReadOnlyList<Message> history,
        IReadOnlyList<CapturedScreen> screens,
        string userText,
        bool includeImageTransformations)
    {
        var messages = new List<object>();
        foreach (var turn in history)
        {
            messages.Add(new { role = "user", content = turn.UserText });
            messages.Add(new { role = "assistant", content = turn.AssistantText });
        }

        var contentBlocks = new List<object>();
        foreach (var screen in screens)
        {
            var imageBlock = new Dictionary<string, object>
            {
                ["type"] = "image",
                ["source"] = new
                {
                    type = "base64",
                    media_type = DetectImageMediaType(screen.ImageBytes),
                    data = Convert.ToBase64String(screen.ImageBytes)
                },
            };
            if (includeImageTransformations)
            {
                // Never let Claude silently downsize a screenshot: its pointing coordinates
                // would no longer match our image. A 400 tells us to resize instead.
                imageBlock["transformations"] = new { oversized_image = "error" };
            }
            contentBlocks.Add(imageBlock);
            contentBlocks.Add(new { type = "text", text = screen.Label });
        }
        contentBlocks.Add(new { type = "text", text = userText });
        messages.Add(new { role = "user", content = contentBlocks });
        return messages;
    }

    private async Task<HttpResponseMessage> SendMessagesRequestAsync(Dictionary<string, object> body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    internal static bool MentionsTuningParameter(string errorBody) =>
        errorBody.Contains("thinking", StringComparison.OrdinalIgnoreCase)
        || errorBody.Contains("output_config", StringComparison.OrdinalIgnoreCase)
        || errorBody.Contains("effort", StringComparison.OrdinalIgnoreCase)
        || errorBody.Contains("json_schema", StringComparison.OrdinalIgnoreCase)
        || errorBody.Contains("transformations", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pulls the human-readable <c>error.message</c> out of an Anthropic error body,
    /// e.g. "invalid x-api-key" or "Your credit balance is too low …".
    /// </summary>
    public static string? ExtractApiErrorMessage(string text)
    {
        var jsonStart = text.IndexOf('{');
        if (jsonStart < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[jsonStart..]);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
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
