using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Clicky.Capture;

namespace Clicky.Api;

/// <summary>
/// Streams OpenAI chat-completions responses token-by-token with optional
/// screenshot image inputs.
/// </summary>
public sealed class OpenAiDirectClient : ILlmClient
{
    private static readonly Uri ApiBaseUri = new("https://api.openai.com");

    private static readonly object TlsWarmupLock = new();
    private static bool _hasStartedTlsWarmup;

    private readonly HttpClient _http;
    private readonly string _model;

    public OpenAiDirectClient(string apiKey, string model = "gpt-5.2", HttpClient? httpClient = null)
    {
        _model = model;

        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        if (!_http.DefaultRequestHeaders.Contains("Authorization"))
        {
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }

        WarmUpTlsConnectionIfNeeded();
    }

    public async IAsyncEnumerable<string> SendAsync(
        IReadOnlyList<Message> history,
        IReadOnlyList<CapturedScreen> screens,
        string systemPrompt,
        string userText,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<object>
        {
            new { role = "developer", content = systemPrompt }
        };

        foreach (var turn in history)
        {
            messages.Add(new { role = "user", content = turn.UserText });
            messages.Add(new { role = "assistant", content = turn.AssistantText });
        }

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
            max_completion_tokens = 1024,
            stream = true,
            messages
        };

        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions")
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
                $"OpenAI API error ({(int)response.StatusCode}): {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

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

        if (lineBuilder.Length > 0)
        {
            var line = lineBuilder.ToString();
            var delta = ProcessSseLine(line);
            if (delta is not null && delta != DoneSentinel) yield return delta;
        }
    }

    private static readonly string DoneSentinel = "\x00DONE";

    internal static string? ProcessSseLine(string line)
    {
        if (!line.StartsWith("data: ", StringComparison.Ordinal)) return null;
        var payload = line.Substring(6);

        if (payload == "[DONE]") return DoneSentinel;

        return ExtractDeltaContent(payload);
    }

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
            System.Diagnostics.Debug.WriteLine($"[OpenAiDirectClient] Skipped malformed SSE line: {json}");
        }
        return null;
    }

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
                // Best-effort warmup only.
            }
        });
    }
}
