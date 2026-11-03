using System.Net;
using Clicky.Api;
using Xunit;

namespace Clicky.Tests;

public class AssemblyAiTranscriberTests
{
    [Fact]
    public async Task FetchTokenAsync_UsesDirectEndpointWithRawApiKey()
    {
        var handler = new CapturingHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"token":"temp-session-token-123"}""")
        });
        var httpClient = new HttpClient(handler);
        var transcriber = new AssemblyAiStreamingTranscriber("my-assemblyai-key", httpClient);

        var token = await transcriber.FetchTokenAsync(CancellationToken.None);

        Assert.Equal("temp-session-token-123", token);
        Assert.NotNull(handler.CapturedRequest);
        Assert.Equal(HttpMethod.Get, handler.CapturedRequest!.Method);
        Assert.Equal(
            "https://streaming.assemblyai.com/v3/token?expires_in_seconds=60",
            handler.CapturedRequest.RequestUri!.ToString());
        // AssemblyAI uses raw key in Authorization header, NOT Bearer
        Assert.True(handler.CapturedRequest.Headers.TryGetValues("Authorization", out var authValues));
        Assert.Equal("my-assemblyai-key", authValues!.First());
    }

    [Fact]
    public async Task FetchTokenAsync_ThrowsOnBadResponse()
    {
        var handler = new CapturingHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Unauthorized")
        });
        var httpClient = new HttpClient(handler);
        var transcriber = new AssemblyAiStreamingTranscriber("bad-key", httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => transcriber.FetchTokenAsync(CancellationToken.None));
    }

    [Fact]
    public void BuildWebSocketUri_IncludesRequiredQueryParams()
    {
        var uri = AssemblyAiStreamingTranscriber.BuildWebSocketUri("test-token-123", null);

        var query = uri.Query;
        Assert.Contains("sample_rate=16000", query);
        Assert.Contains("encoding=pcm_s16le", query);
        Assert.Contains("format_turns=true", query);
        Assert.Contains("speech_model=u3-rt-pro", query);
        Assert.Contains("token=test-token-123", query);
        Assert.Equal("streaming.assemblyai.com", uri.Host);
        Assert.Equal("/v3/ws", uri.AbsolutePath);
    }

    [Fact]
    public void BuildWebSocketUri_IncludesKeytermsWhenProvided()
    {
        var keyterms = new List<string> { "Clicky", "Claude" };
        var uri = AssemblyAiStreamingTranscriber.BuildWebSocketUri("tok", keyterms);

        var query = Uri.UnescapeDataString(uri.Query);
        Assert.Contains("keyterms_prompt=", query);
        Assert.Contains("[\"Clicky\",\"Claude\"]", query);
    }

    [Fact]
    public void BuildWebSocketUri_OmitsKeytermsWhenEmpty()
    {
        var uri = AssemblyAiStreamingTranscriber.BuildWebSocketUri("tok", new List<string>());
        Assert.DoesNotContain("keyterms_prompt", uri.Query);
    }

    [Fact]
    public void ProcessMessage_CapitalizedType_IsRecognized()
    {
        // Regression: AssemblyAI v3 sends capitalized type values ("Begin",
        // "Turn", "Termination", "Error"). Our port originally compared
        // case-sensitively against lowercase literals, so a real "Turn"
        // message was dropped on the floor and the user saw no transcript.
        // The Mac reference normalizes via envelope.type.lowercased().
        var capitalizedTurn = """
            {"type":"Turn","transcript":"capitalized works","turn_order":0,"end_of_turn":true,"turn_is_formatted":true}
            """;

        var session = CreateTestSession();
        string? received = null;
        session.PartialTranscriptUpdated += t => received = t;

        session.ProcessMessage(capitalizedTurn);

        Assert.Equal("capitalized works", received);

        var capitalizedError = """
            {"type":"Error","error":"boom"}
            """;
        Exception? receivedError = null;
        session.OnError += ex => receivedError = ex;
        session.ProcessMessage(capitalizedError);
        Assert.NotNull(receivedError);
        Assert.Contains("boom", receivedError!.Message);
    }

    [Fact]
    public void ProcessMessage_PartialTurn_RaisesPartialEvent()
    {
        // We can't easily construct a TranscriptionSession without a real WebSocket,
        // so we test the internal ProcessMessage + ComposeTranscript via a helper approach.
        // Use reflection or test the message parsing logic directly.
        var partialJson = """
            {"type":"turn","transcript":"hello world","turn_order":0,"end_of_turn":false,"turn_is_formatted":false}
            """;

        var session = CreateTestSession();
        string? received = null;
        session.PartialTranscriptUpdated += t => received = t;

        session.ProcessMessage(partialJson);

        Assert.Equal("hello world", received);
    }

    [Fact]
    public void ProcessMessage_CompleteTurn_StoresTurnAndRaisesPartial()
    {
        var completeJson = """
            {"type":"turn","transcript":"hello world","turn_order":0,"end_of_turn":true,"turn_is_formatted":true}
            """;

        var session = CreateTestSession();
        string? received = null;
        session.PartialTranscriptUpdated += t => received = t;

        session.ProcessMessage(completeJson);

        Assert.Equal("hello world", received);
        Assert.Equal("hello world", session.ComposeTranscript());
    }

    [Fact]
    public void ProcessMessage_MultipleTurns_ComposesInOrder()
    {
        var session = CreateTestSession();
        string? lastReceived = null;
        session.PartialTranscriptUpdated += t => lastReceived = t;

        session.ProcessMessage("""
            {"type":"turn","transcript":"first turn","turn_order":0,"end_of_turn":true,"turn_is_formatted":true}
            """);
        session.ProcessMessage("""
            {"type":"turn","transcript":"second part","turn_order":1,"end_of_turn":false,"turn_is_formatted":false}
            """);

        Assert.Equal("first turn second part", lastReceived);
        Assert.Equal("first turn second part", session.ComposeTranscript());
    }

    [Fact]
    public void ProcessMessage_ErrorMessage_RaisesOnError()
    {
        var errorJson = """
            {"type":"error","error":"Invalid token","message":"Token has expired"}
            """;

        var session = CreateTestSession();
        Exception? receivedError = null;
        session.OnError += ex => receivedError = ex;

        session.ProcessMessage(errorJson);

        Assert.NotNull(receivedError);
        Assert.Contains("Invalid token", receivedError!.Message);
    }

    [Fact]
    public void ProcessMessage_MalformedJson_RaisesOnError()
    {
        var session = CreateTestSession();
        Exception? receivedError = null;
        session.OnError += ex => receivedError = ex;

        session.ProcessMessage("not valid json {{{");

        Assert.NotNull(receivedError);
    }

    [Fact]
    public void ComposeTranscript_EmptySession_ReturnsEmpty()
    {
        var session = CreateTestSession();
        Assert.Equal("", session.ComposeTranscript());
    }

    [Fact]
    public void GetBestTranscript_PrefersComposedOverActive()
    {
        var session = CreateTestSession();

        // Add a complete turn then a partial
        session.ProcessMessage("""
            {"type":"turn","transcript":"stored turn","turn_order":0,"end_of_turn":true,"turn_is_formatted":true}
            """);
        session.ProcessMessage("""
            {"type":"turn","transcript":"partial","turn_order":1,"end_of_turn":false,"turn_is_formatted":false}
            """);

        var best = session.GetBestTranscript();
        Assert.Equal("stored turn partial", best);
    }

    /// <summary>
    /// Creates a TranscriptionSession with a disposed WebSocket for testing message processing.
    /// The session's receive loop will fail immediately, but ProcessMessage/ComposeTranscript
    /// can still be tested directly.
    /// </summary>
    private static TranscriptionSession CreateTestSession()
    {
        // Use a ClientWebSocket that's never connected — the receive loop will exit
        // with an error, but we can still test message parsing via the internal methods.
        var ws = new System.Net.WebSockets.ClientWebSocket();
        var session = new TranscriptionSession(ws);
        // Give the receive loop a moment to fail (it will hit an exception and exit)
        Thread.Sleep(50);
        return session;
    }

    private sealed class CapturingHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(response);
        }
    }
}
