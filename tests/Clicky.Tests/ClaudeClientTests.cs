using System.Net;
using System.Text;
using Clicky.Api;
using Clicky.Capture;
using System.Drawing;
using Xunit;

namespace Clicky.Tests;

public class ClaudeClientTests
{
    /// <summary>
    /// Verifies that the SSE parser extracts text deltas in order from a canned
    /// content_block_delta stream, matching the ClaudeAPI.swift behavior.
    /// </summary>
    [Fact]
    public async Task SendAsync_ParsesCannedSseStream_YieldsOrderedDeltas()
    {
        // Arrange: build a realistic SSE response stream
        var sseLines = new[]
        {
            "event: message_start",
            """data: {"type":"message_start","message":{"id":"msg_01","type":"message","role":"assistant","content":[],"model":"claude-sonnet-4-6","stop_reason":null}}""",
            "",
            "event: content_block_start",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            "",
            "event: content_block_delta",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}""",
            "",
            "event: content_block_delta",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}""",
            "",
            "event: content_block_delta",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"!"}}""",
            "",
            "event: content_block_stop",
            """data: {"type":"content_block_stop","index":0}""",
            "",
            "event: message_delta",
            """data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null}}""",
            "",
            "event: message_stop",
            """data: {"type":"message_stop"}""",
            "",
            "data: [DONE]",
            ""
        };

        var sseContent = string.Join("\n", sseLines);
        var handler = new FakeSseHandler(sseContent);

        // Use a custom HttpClient that returns our canned SSE stream.
        // We can't use the real ClaudeClient constructor's warmup, so we test
        // the parsing logic via the internal helpers + a direct HTTP roundtrip.
        var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/chat")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Act: parse the SSE stream the same way ClaudeClient does
        var deltas = new List<string>();
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line.Substring(6);
            if (payload == "[DONE]") break;

            var textDelta = AnthropicDirectClient.ExtractTextDelta(payload);
            if (textDelta is not null)
                deltas.Add(textDelta);
        }

        // Assert
        Assert.Equal(3, deltas.Count);
        Assert.Equal("Hello", deltas[0]);
        Assert.Equal(" world", deltas[1]);
        Assert.Equal("!", deltas[2]);
        Assert.Equal("Hello world!", string.Concat(deltas));
    }

    [Fact]
    public void ExtractTextDelta_ReturnsNull_ForNonDeltaEvents()
    {
        var messageStart = """{"type":"message_start","message":{"id":"msg_01"}}""";
        Assert.Null(AnthropicDirectClient.ExtractTextDelta(messageStart));

        var contentBlockStop = """{"type":"content_block_stop","index":0}""";
        Assert.Null(AnthropicDirectClient.ExtractTextDelta(contentBlockStop));
    }

    [Fact]
    public void ExtractTextDelta_ReturnsNull_ForMalformedJson()
    {
        Assert.Null(AnthropicDirectClient.ExtractTextDelta("not json at all"));
        Assert.Null(AnthropicDirectClient.ExtractTextDelta("{broken"));
    }

    [Fact]
    public void DetectImageMediaType_Png_ReturnsPng()
    {
        // PNG magic bytes: 89 50 4E 47
        byte[] pngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.Equal("image/png", AnthropicDirectClient.DetectImageMediaType(pngHeader));
    }

    [Fact]
    public void DetectImageMediaType_Jpeg_ReturnsJpeg()
    {
        // JPEG magic bytes: FF D8 FF
        byte[] jpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        Assert.Equal("image/jpeg", AnthropicDirectClient.DetectImageMediaType(jpegHeader));
    }

    [Fact]
    public void DetectImageMediaType_ShortData_DefaultsToJpeg()
    {
        byte[] twoBytes = [0x01, 0x02];
        Assert.Equal("image/jpeg", AnthropicDirectClient.DetectImageMediaType(twoBytes));
    }

    [Fact]
    public void DetectImageMediaType_EmptyData_DefaultsToJpeg()
    {
        Assert.Equal("image/jpeg", AnthropicDirectClient.DetectImageMediaType(ReadOnlySpan<byte>.Empty));
    }

    /// <summary>
    /// Fake HttpMessageHandler that returns a pre-built SSE stream.
    /// </summary>
    private sealed class FakeSseHandler(string sseContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new MemoryStream(Encoding.UTF8.GetBytes(sseContent)))
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
