using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Clicky.Capture;

namespace Clicky.Api;

/// <summary>
/// Streams GLM responses token-by-token by calling api.z.ai's OpenAI-compatible
/// chat completions endpoint directly with the user's own API key.
/// </summary>
public sealed class ZaiDirectClient : ILlmClient
{
    private static readonly Uri ApiBaseUri = new("https://api.z.ai");

    private static readonly object TlsWarmupLock = new();
    private static bool _hasStartedTlsWarmup;

    private readonly HttpClient _http;
    private readonly string _model;

    public ZaiDirectClient(string apiKey, string model = "glm-4.6v", HttpClient? httpClient = null)
    {
        _model = model;

        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        if (httpClient is null)
        {
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }

        WarmUpTlsConnectionIfNeeded();
    }

    /// <summary>
    /// Sends a streaming request to GLM via api.z.ai and yields
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

        // System prompt as the first message (OpenAI convention)
        messages.Add(new { role = "system", content = systemPrompt });

        // Conversation history as alternating user/assistant turns
        foreach (var turn in history)
        {
            messages.Add(new { role = "user", content = turn.UserText });
            messages.Add(new { role = "assistant", content = turn.AssistantText });
        }

        // Current turn: images + labels + user text as a content array
        var contentParts = new List<object>();
        foreach (var screen in screens)
        {
            var mediaType = AnthropicDirectClient.DetectImageMediaType(screen.ImageBytes);
            var base64 = Convert.ToBase64String(screen.ImageBytes);
            contentParts.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:{mediaType};base64,{base64}" }
            });
            contentParts.Add(new { type = "text", text = screen.Label });
        }
        contentParts.Add(new { type = "text", text = userText });
        messages.Add(new { role = "user", content = contentParts });

        var body = new
        {
            model = _model,
            max_tokens = 1024,
            temperature = 0.7,
            stream = true,
            messages
        };

        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.z.ai/api/paas/v4/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Accept", "text/event-stream");

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"z.ai API error ({(int)response.StatusCode}): {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        // Use an incremental UTF-8 decoder so multi-byte characters split
        // across HTTP chunk boundaries don't corrupt the yielded text.
        var decoder = Encoding.UTF8.GetDecoder();
        var byteBuffer = new byte[8192];
        var charBuffer = new char[8192];
        var lineBuilder = new StringBuilder();

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(byteBuffer, 0, byteBuffer.Length, ct)
                   .ConfigureAwait(false)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            var charsDecoded = decoder.GetChars(byteBuffer, 0, bytesRead, charBuffer, 0, flush: false);

            for (var i = 0; i < charsDecoded; i++)
            {
                var c = charBuffer[i];
                if (c == '\n' || c == '\r')
                {
                    if (lineBuilder.Length > 0)
                    {
                        var line = lineBuilder.ToString();
                        lineBuilder.Clear();

                        var delta = ProcessSseLine(line);
                        if (delta == DoneSentinel) yield break;
                        if (delta is not null) yield return delta;
                    }
                }
                else
                {
                    lineBuilder.Append(c);
                }
            }
        }

        // Flush any remaining partial line
        if (lineBuilder.Length > 0)
        {
            var line = lineBuilder.ToString();
            var delta = ProcessSseLine(line);
            if (delta is not null && delta != DoneSentinel) yield return delta;
        }
    }

    /// <summary>Sentinel returned by <see cref="ProcessSseLine"/> to signal end of stream.</summary>
    private static readonly string DoneSentinel = "\x00DONE";

    /// <summary>
    /// Processes a single SSE line. Returns a text delta string, <see cref="DoneSentinel"/>
    /// to signal stream end, or <c>null</c> for lines we don't care about.
    /// </summary>
    internal static string? ProcessSseLine(string line)
    {
        if (!line.StartsWith("data: ", StringComparison.Ordinal)) return null;
        var payload = line.Substring(6);

        if (payload == "[DONE]") return DoneSentinel;

        return ExtractDeltaContent(payload);
    }

    /// <summary>
    /// Extracts <c>choices[0].delta.content</c> from an OpenAI chat-completions
    /// SSE chunk. Returns <c>null</c> for events without text content.
    /// Malformed JSON is logged and skipped — one bad line must not kill the stream.
    /// </summary>
    internal static string? ExtractDeltaContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — skip this event, don't kill the stream
            System.Diagnostics.Debug.WriteLine($"[ZaiDirectClient] Skipped malformed SSE line: {json}");
        }
        return null;
    }

    /// <summary>
    /// Fires a HEAD request to pre-establish TLS with api.z.ai.
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
