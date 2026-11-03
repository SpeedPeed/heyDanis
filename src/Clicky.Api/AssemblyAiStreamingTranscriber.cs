using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clicky.Api;

/// <summary>
/// Streams real-time speech-to-text via AssemblyAI's v3 WebSocket API,
/// mirroring <c>AssemblyAIStreamingTranscriptionProvider.swift</c> in the Mac reference.
/// </summary>
public sealed class AssemblyAiStreamingTranscriber
{
    private static readonly Uri AssemblyAiWsBase = new("wss://streaming.assemblyai.com/v3/ws");
    private static readonly Uri AssemblyAiTokenUri = new("https://streaming.assemblyai.com/v3/token?expires_in_seconds=60");

    /// <summary>
    /// Shared <see cref="HttpClient"/> reused across all sessions.
    /// Mac note: per-session URLSession corrupts the connection pool.
    /// </summary>
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public AssemblyAiStreamingTranscriber(string apiKey, HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Starts a new streaming transcription session.
    /// Fetches a short-lived token directly from AssemblyAI, then opens a WebSocket.
    /// </summary>
    public async Task<TranscriptionSession> StartSessionAsync(
        IReadOnlyList<string>? keyterms = null,
        CancellationToken ct = default)
    {
        var token = await FetchTokenAsync(ct).ConfigureAwait(false);
        var wsUri = BuildWebSocketUri(token, keyterms);
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(wsUri, ct).ConfigureAwait(false);

        // Wait for the "begin" message before returning the session
        await WaitForBeginMessageAsync(ws, ct).ConfigureAwait(false);

        return new TranscriptionSession(ws);
    }

    /// <summary>
    /// Fetches a single-use temporary token directly from AssemblyAI's token endpoint.
    /// Uses the raw API key in the Authorization header (not Bearer).
    /// Tokens must never be cached — each session needs its own.
    /// </summary>
    internal async Task<string> FetchTokenAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AssemblyAiTokenUri);
        request.Headers.TryAddWithoutValidation("Authorization", _apiKey);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Failed to fetch transcription token ({(int)response.StatusCode}): {errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("token", out var tokenProp))
        {
            return tokenProp.GetString()
                ?? throw new InvalidOperationException("Token response contained null token.");
        }

        throw new InvalidOperationException($"Token response missing 'token' field: {json}");
    }

    internal static Uri BuildWebSocketUri(string token, IReadOnlyList<string>? keyterms)
    {
        var builder = new UriBuilder(AssemblyAiWsBase);
        var query = new StringBuilder();
        query.Append("sample_rate=16000");
        query.Append("&encoding=pcm_s16le");
        query.Append("&format_turns=true");
        query.Append("&speech_model=u3-rt-pro");
        query.Append($"&token={Uri.EscapeDataString(token)}");

        if (keyterms is { Count: > 0 })
        {
            var keytermJson = JsonSerializer.Serialize(keyterms);
            query.Append($"&keyterms_prompt={Uri.EscapeDataString(keytermJson)}");
        }

        builder.Query = query.ToString();
        return builder.Uri;
    }

    private static async Task WaitForBeginMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

        if (result.MessageType == WebSocketMessageType.Close)
            throw new InvalidOperationException("WebSocket closed before begin message.");

        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        using var doc = JsonDocument.Parse(json);
        // AssemblyAI v3 sends type values capitalized ("Begin", "Turn", "Termination", "Error").
        // Normalize to lowercase before comparing — mirrors the Mac reference's
        // envelope.type.lowercased() in AssemblyAIStreamingTranscriptionProvider.swift.
        var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString()?.ToLowerInvariant() : null;

        if (type == "error")
        {
            var msg = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString()
                : doc.RootElement.TryGetProperty("message", out var m) ? m.GetString()
                : json;
            throw new InvalidOperationException($"AssemblyAI error on connect: {msg}");
        }

        if (type != "begin")
            throw new InvalidOperationException($"Expected 'begin' message, got: {json}");
    }
}

/// <summary>
/// Represents an active AssemblyAI streaming transcription session.
/// Audio frames are written in; transcript updates come out via events.
/// </summary>
public sealed class TranscriptionSession : IDisposable
{
    private const double ExplicitFinalGracePeriodSeconds = 1.4;

    private readonly ClientWebSocket _ws;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Turn tracking — mirrors Mac's storedTurnTranscriptsByOrder
    private readonly SortedDictionary<int, string> _storedTurns = new();
    private string _activeTurnText = "";
    private bool _isAwaitingExplicitFinal;
    private TaskCompletionSource<string>? _finalTranscriptTcs;
    private CancellationTokenSource? _graceTimerCts;
    private Task? _receiveLoopTask;

    /// <summary>
    /// Raised whenever a partial transcript update arrives.
    /// The string is the full composed transcript so far.
    /// </summary>
    public event Action<string>? PartialTranscriptUpdated;

    /// <summary>
    /// Raised when the final transcript is ready (used by CompanionManager in US-010).
    /// Suppress CS0067 — wired in the orchestration layer.
    /// </summary>
#pragma warning disable CS0067
    public event Action<string>? FinalTranscriptReady;
#pragma warning restore CS0067

    /// <summary>
    /// Raised on error.
    /// </summary>
    public event Action<Exception>? OnError;

    internal TranscriptionSession(ClientWebSocket ws)
    {
        _ws = ws;
        _receiveLoopTask = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>
    /// Sends a PCM16 audio frame to AssemblyAI.
    /// Frames should be 16 kHz mono PCM16 little-endian (the format from MicrophoneCapture).
    /// </summary>
    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcmFrame, CancellationToken ct = default)
    {
        if (_ws.State != WebSocketState.Open) return;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _sendLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(pcmFrame, WebSocketMessageType.Binary, true, linkedCts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Requests a final transcript by sending ForceEndpoint, then waits up to the grace period.
    /// Returns the best available transcript.
    /// </summary>
    public async Task<string> RequestFinalTranscriptAsync(CancellationToken ct = default)
    {
        _isAwaitingExplicitFinal = true;
        _finalTranscriptTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Send ForceEndpoint to tell AssemblyAI to finalize current turn
        await SendJsonAsync(new { type = "ForceEndpoint" }, ct).ConfigureAwait(false);

        // Start grace period timer
        _graceTimerCts = new CancellationTokenSource();
        var graceToken = _graceTimerCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ExplicitFinalGracePeriodSeconds), graceToken)
                    .ConfigureAwait(false);
                // Grace period expired — deliver what we have
                DeliverFinalTranscript();
            }
            catch (OperationCanceledException)
            {
                // Timer was cancelled because we got the final turn in time
            }
        });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linkedCts.Token.Register(() => _finalTranscriptTcs.TrySetCanceled());

        return await _finalTranscriptTcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the session by sending a Terminate message and closing the WebSocket.
    /// </summary>
    public async Task CancelAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                await SendJsonAsync(new { type = "Terminate" }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort
            }
        }

        _cts.Cancel();
        CloseWebSocket();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _graceTimerCts?.Cancel();
        CloseWebSocket();
        _sendLock.Dispose();
        _cts.Dispose();
        _graceTimerCts?.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        HandleSessionEnd();
                        return;
                    }
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                ProcessMessage(json);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (WebSocketException ex)
        {
            OnError?.Invoke(ex);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }
    }

    internal void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // AssemblyAI v3 capitalizes type values — normalize before matching
            // (matches the Mac reference's envelope.type.lowercased()).
            var type = root.TryGetProperty("type", out var t) ? t.GetString()?.ToLowerInvariant() : null;

            switch (type)
            {
                case "turn":
                    HandleTurnMessage(root);
                    break;

                case "termination":
                    HandleSessionEnd();
                    break;

                case "error":
                    var errorMsg = root.TryGetProperty("error", out var e) ? e.GetString()
                        : root.TryGetProperty("message", out var m) ? m.GetString()
                        : json;
                    OnError?.Invoke(new InvalidOperationException($"AssemblyAI error: {errorMsg}"));
                    break;

                // "begin" already handled during connection; ignore unknown types
            }
        }
        catch (JsonException ex)
        {
            OnError?.Invoke(ex);
        }
    }

    private void HandleTurnMessage(JsonElement root)
    {
        var transcript = root.TryGetProperty("transcript", out var tp) ? tp.GetString() ?? "" : "";
        var turnOrder = root.TryGetProperty("turn_order", out var to) ? to.GetInt32() : 0;
        var endOfTurn = root.TryGetProperty("end_of_turn", out var eot) && eot.GetBoolean();
        var turnIsFormatted = root.TryGetProperty("turn_is_formatted", out var tif) && tif.GetBoolean();

        if (endOfTurn || turnIsFormatted)
        {
            // Complete turn — store it
            _storedTurns[turnOrder] = transcript;
            _activeTurnText = "";

            if (_isAwaitingExplicitFinal)
            {
                // Cancel the grace timer — we got the final turn in time
                _graceTimerCts?.Cancel();
                DeliverFinalTranscript();
            }
        }
        else
        {
            // Partial turn
            _activeTurnText = transcript;
        }

        // Compose full transcript from all stored turns + active
        var composed = ComposeTranscript();
        PartialTranscriptUpdated?.Invoke(composed);
    }

    private void HandleSessionEnd()
    {
        if (_isAwaitingExplicitFinal)
        {
            _graceTimerCts?.Cancel();
            DeliverFinalTranscript();
        }
    }

    private void DeliverFinalTranscript()
    {
        if (_finalTranscriptTcs is null || _finalTranscriptTcs.Task.IsCompleted) return;

        var transcript = GetBestTranscript();
        _isAwaitingExplicitFinal = false;

        // Send Terminate after delivering final
        _ = Task.Run(async () =>
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await SendJsonAsync(new { type = "Terminate" }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        });

        _finalTranscriptTcs.TrySetResult(transcript);
    }

    internal string ComposeTranscript()
    {
        var sb = new StringBuilder();
        foreach (var kvp in _storedTurns)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(kvp.Value);
        }
        if (!string.IsNullOrEmpty(_activeTurnText))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(_activeTurnText);
        }
        return sb.ToString().Trim();
    }

    internal string GetBestTranscript()
    {
        var composed = ComposeTranscript();
        return string.IsNullOrWhiteSpace(composed) ? _activeTurnText.Trim() : composed;
    }

    private async Task SendJsonAsync(object message, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void CloseWebSocket()
    {
        try
        {
            if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
            {
                _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Best-effort close
        }
        finally
        {
            _ws.Dispose();
        }
    }
}
