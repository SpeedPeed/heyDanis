using System.Drawing;
using System.Net;
using System.Text;
using System.Text.Json;
using Clicky.Api;
using Clicky.Capture;
using Xunit;

namespace Clicky.Tests;

public class ZaiDirectClientTests
{
    // ──────────────────────────────────────────────────────────
    // Request translation tests
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_TextOnly_BuildsCorrectOpenAiRequest()
    {
        // Arrange
        string? capturedBody = null;
        var handler = new CaptureHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return MakeSseResponse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n");
        });
        var client = new ZaiDirectClient("test-key", "glm-4.6v", new HttpClient(handler));

        // Act
        await foreach (var _ in client.SendAsync(
            Array.Empty<Message>(),
            Array.Empty<CapturedScreen>(),
            "You are helpful.",
            "Hello"))
        {
        }

        // Assert: verify request body structure
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;

        Assert.Equal("glm-4.6v", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(1024, root.GetProperty("max_tokens").GetInt32());

        var messages = root.GetProperty("messages");
        // First message is the system prompt
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are helpful.", messages[0].GetProperty("content").GetString());

        // Last message is user with content array
        var lastMsg = messages[1];
        Assert.Equal("user", lastMsg.GetProperty("role").GetString());
        var content = lastMsg.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Hello", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendAsync_SingleImage_BuildsImageUrlBlock()
    {
        // Arrange: PNG magic bytes
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 };
        var screen = new CapturedScreen
        {
            ImageBytes = pngBytes,
            Label = "Screen 1 (cursor is here)",
            IsCursorScreen = true,
            DisplayBounds = new Rectangle(0, 0, 1920, 1080),
            ScreenshotPixelWidth = 1920,
            ScreenshotPixelHeight = 1080
        };

        string? capturedBody = null;
        var handler = new CaptureHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return MakeSseResponse("data: {\"choices\":[{\"delta\":{\"content\":\"seen\"}}]}\n\ndata: [DONE]\n");
        });
        var client = new ZaiDirectClient("test-key", "glm-4.6v", new HttpClient(handler));

        // Act
        await foreach (var _ in client.SendAsync(
            Array.Empty<Message>(),
            new[] { screen },
            "system",
            "What do you see?"))
        {
        }

        // Assert
        using var doc = JsonDocument.Parse(capturedBody!);
        var userMsg = doc.RootElement.GetProperty("messages")[1]; // index 0=system, 1=user
        var content = userMsg.GetProperty("content");

        // image_url, label text, user text = 3 blocks
        Assert.Equal(3, content.GetArrayLength());

        var imageBlock = content[0];
        Assert.Equal("image_url", imageBlock.GetProperty("type").GetString());
        var url = imageBlock.GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url);

        var labelBlock = content[1];
        Assert.Equal("text", labelBlock.GetProperty("type").GetString());
        Assert.Equal("Screen 1 (cursor is here)", labelBlock.GetProperty("text").GetString());

        var textBlock = content[2];
        Assert.Equal("text", textBlock.GetProperty("type").GetString());
        Assert.Equal("What do you see?", textBlock.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendAsync_MultipleScreens_BuildsMultiImageRequest()
    {
        // Arrange: one PNG, one JPEG
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        var screens = new[]
        {
            new CapturedScreen
            {
                ImageBytes = pngBytes, Label = "screen 1", IsCursorScreen = true,
                DisplayBounds = new Rectangle(0, 0, 1920, 1080),
                ScreenshotPixelWidth = 1920, ScreenshotPixelHeight = 1080
            },
            new CapturedScreen
            {
                ImageBytes = jpegBytes, Label = "screen 2", IsCursorScreen = false,
                DisplayBounds = new Rectangle(1920, 0, 1920, 1080),
                ScreenshotPixelWidth = 1920, ScreenshotPixelHeight = 1080
            }
        };

        string? capturedBody = null;
        var handler = new CaptureHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return MakeSseResponse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n");
        });
        var client = new ZaiDirectClient("test-key", "glm-4.6v", new HttpClient(handler));

        // Act
        await foreach (var _ in client.SendAsync(
            Array.Empty<Message>(),
            screens,
            "system",
            "Compare"))
        {
        }

        // Assert
        using var doc = JsonDocument.Parse(capturedBody!);
        var userMsg = doc.RootElement.GetProperty("messages")[1];
        var content = userMsg.GetProperty("content");

        // 2 screens × (image + label) + 1 user text = 5 blocks
        Assert.Equal(5, content.GetArrayLength());

        // First image is PNG
        var url1 = content[0].GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url1);

        // Second image is JPEG
        var url2 = content[2].GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/jpeg;base64,", url2);
    }

    [Fact]
    public async Task SendAsync_WithHistory_PreservesHistoryTurns()
    {
        // Arrange
        var history = new[]
        {
            new Message("What color is the sky?", "The sky is blue."),
            new Message("And grass?", "Grass is green.")
        };

        string? capturedBody = null;
        var handler = new CaptureHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return MakeSseResponse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n");
        });
        var client = new ZaiDirectClient("test-key", "glm-4.6v", new HttpClient(handler));

        // Act
        await foreach (var _ in client.SendAsync(
            history,
            Array.Empty<CapturedScreen>(),
            "system",
            "And water?"))
        {
        }

        // Assert: system + 4 history + 1 current = 6 messages
        using var doc = JsonDocument.Parse(capturedBody!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(6, messages.GetArrayLength());

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("What color is the sky?", messages[1].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("The sky is blue.", messages[2].GetProperty("content").GetString());
    }

    // ──────────────────────────────────────────────────────────
    // SSE parsing tests
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ExtractDeltaContent_ValidChunk_ReturnsDelta()
    {
        var json = """{"choices":[{"delta":{"content":"Hello"}}]}""";
        Assert.Equal("Hello", ZaiDirectClient.ExtractDeltaContent(json));
    }

    [Fact]
    public void ExtractDeltaContent_EmptyDelta_ReturnsNull()
    {
        // OpenAI sends chunks with empty delta at the start (role-only)
        var json = """{"choices":[{"delta":{"role":"assistant"}}]}""";
        Assert.Null(ZaiDirectClient.ExtractDeltaContent(json));
    }

    [Fact]
    public void ExtractDeltaContent_NullContent_ReturnsNull()
    {
        var json = """{"choices":[{"delta":{"content":null}}]}""";
        Assert.Null(ZaiDirectClient.ExtractDeltaContent(json));
    }

    [Fact]
    public void ExtractDeltaContent_MalformedJson_ReturnsNull()
    {
        Assert.Null(ZaiDirectClient.ExtractDeltaContent("not json"));
        Assert.Null(ZaiDirectClient.ExtractDeltaContent("{broken"));
        Assert.Null(ZaiDirectClient.ExtractDeltaContent(""));
    }

    [Fact]
    public void ExtractDeltaContent_EmptyChoices_ReturnsNull()
    {
        var json = """{"choices":[]}""";
        Assert.Null(ZaiDirectClient.ExtractDeltaContent(json));
    }

    [Fact]
    public void ProcessSseLine_DataLine_ExtractsDelta()
    {
        var line = """data: {"choices":[{"delta":{"content":"Hi"}}]}""";
        Assert.Equal("Hi", ZaiDirectClient.ProcessSseLine(line));
    }

    [Fact]
    public void ProcessSseLine_DoneSentinel_ReturnsDoneSentinel()
    {
        // ProcessSseLine returns a special sentinel for [DONE] — it's not null
        // and it's not a normal delta. We test that it's non-null and different from real deltas.
        var result = ZaiDirectClient.ProcessSseLine("data: [DONE]");
        Assert.NotNull(result);
        // The sentinel is an internal detail; just verify it's not empty text
        Assert.NotEqual("", result);
    }

    [Fact]
    public void ProcessSseLine_NonDataLine_ReturnsNull()
    {
        Assert.Null(ZaiDirectClient.ProcessSseLine("event: message"));
        Assert.Null(ZaiDirectClient.ProcessSseLine(": comment"));
        Assert.Null(ZaiDirectClient.ProcessSseLine(""));
    }

    [Fact]
    public async Task SendAsync_RealisticGlmStream_YieldsCorrectDeltas()
    {
        // A realistic GLM-4.6V SSE response that includes a [POINT:...] tag mid-stream
        var sse = string.Join("\n", new[]
        {
            """data: {"id":"chat-1","choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}""",
            "",
            """data: {"id":"chat-1","choices":[{"index":0,"delta":{"content":"I can see "}}]}""",
            "",
            """data: {"id":"chat-1","choices":[{"index":0,"delta":{"content":"a button "}}]}""",
            "",
            """data: {"id":"chat-1","choices":[{"index":0,"delta":{"content":"[POINT:150,300:Submit:screen1]"}}]}""",
            "",
            """data: {"id":"chat-1","choices":[{"index":0,"delta":{"content":" here."}}]}""",
            "",
            """data: {"id":"chat-1","choices":[{"index":0,"finish_reason":"stop","delta":{}}]}""",
            "",
            "data: [DONE]",
            ""
        });

        var handler = new FakeSseHandler(sse);
        var client = new ZaiDirectClient("key", "glm-4.6v", new HttpClient(handler));

        var deltas = new List<string>();
        await foreach (var d in client.SendAsync(
            Array.Empty<Message>(),
            Array.Empty<CapturedScreen>(),
            "system",
            "click"))
        {
            deltas.Add(d);
        }

        // The empty content "" at the start is a valid string, so it's yielded
        Assert.Equal(5, deltas.Count);
        Assert.Equal("", deltas[0]);
        Assert.Equal("I can see ", deltas[1]);
        Assert.Equal("a button ", deltas[2]);
        Assert.Equal("[POINT:150,300:Submit:screen1]", deltas[3]);
        Assert.Equal(" here.", deltas[4]);
        Assert.Equal("I can see a button [POINT:150,300:Submit:screen1] here.", string.Concat(deltas));
    }

    [Fact]
    public async Task SendAsync_Utf8ChunkBoundary_DoesNotCorrupt()
    {
        // "日本語" = E6 97 A5 E6 9C AC E8 AA 9E
        // We split a multi-byte char across two HTTP chunks to test the incremental decoder.
        var fullJson = """{"choices":[{"delta":{"content":"日本語"}}]}""";
        var fullSse = $"data: {fullJson}\n\ndata: [DONE]\n";
        var sseBytes = Encoding.UTF8.GetBytes(fullSse);

        // Find a split point inside a multi-byte character.
        // "日" is E6 97 A5 — split after E6 97 (before A5).
        var dataPrefix = "data: {\"choices\":[{\"delta\":{\"content\":\"";
        var prefixBytes = Encoding.UTF8.GetBytes(dataPrefix);
        // The first byte of 日 starts at prefixBytes.Length. Split 2 bytes into the char.
        var splitPoint = prefixBytes.Length + 2;

        var chunk1 = sseBytes[..splitPoint];
        var chunk2 = sseBytes[splitPoint..];

        var handler = new ChunkedSseHandler(chunk1, chunk2);
        var client = new ZaiDirectClient("key", "glm-4.6v", new HttpClient(handler));

        var deltas = new List<string>();
        await foreach (var d in client.SendAsync(
            Array.Empty<Message>(),
            Array.Empty<CapturedScreen>(),
            "sys",
            "hi"))
        {
            deltas.Add(d);
        }

        Assert.Single(deltas);
        Assert.Equal("日本語", deltas[0]);
    }

    [Fact]
    public async Task SendAsync_MalformedLineSkipped_StreamContinues()
    {
        var sse = string.Join("\n", new[]
        {
            """data: {"choices":[{"delta":{"content":"before"}}]}""",
            "",
            "data: {this is broken json!!!",
            "",
            """data: {"choices":[{"delta":{"content":"after"}}]}""",
            "",
            "data: [DONE]",
            ""
        });

        var handler = new FakeSseHandler(sse);
        var client = new ZaiDirectClient("key", "glm-4.6v", new HttpClient(handler));

        var deltas = new List<string>();
        await foreach (var d in client.SendAsync(
            Array.Empty<Message>(),
            Array.Empty<CapturedScreen>(),
            "sys",
            "hi"))
        {
            deltas.Add(d);
        }

        Assert.Equal(2, deltas.Count);
        Assert.Equal("before", deltas[0]);
        Assert.Equal("after", deltas[1]);
    }

    [Fact]
    public async Task SendAsync_HttpError_ThrowsHttpRequestException()
    {
        var handler = new CaptureHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid api key\"}")
            });
        var client = new ZaiDirectClient("bad-key", "glm-4.6v", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.SendAsync(
                Array.Empty<Message>(),
                Array.Empty<CapturedScreen>(),
                "sys",
                "hi"))
            {
            }
        });

        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid api key", ex.Message);
    }

    [Fact]
    public async Task SendAsync_EndToEnd_FakeHttpHandler_YieldsExpectedDeltas()
    {
        // End-to-end: canned OpenAI SSE → expected string deltas through ILlmClient.SendAsync
        var sse = string.Join("\n", new[]
        {
            """data: {"id":"x","choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}""",
            "",
            """data: {"id":"x","choices":[{"index":0,"delta":{"content":"The "}}]}""",
            "",
            """data: {"id":"x","choices":[{"index":0,"delta":{"content":"answer"}}]}""",
            "",
            """data: {"id":"x","choices":[{"index":0,"delta":{"content":" is 42."}}]}""",
            "",
            """data: {"id":"x","choices":[{"index":0,"finish_reason":"stop","delta":{}}]}""",
            "",
            "data: [DONE]",
            ""
        });

        var handler = new FakeSseHandler(sse);
        ILlmClient client = new ZaiDirectClient("key", "glm-4.6v", new HttpClient(handler));

        var result = new StringBuilder();
        await foreach (var delta in client.SendAsync(
            Array.Empty<Message>(),
            Array.Empty<CapturedScreen>(),
            "system prompt",
            "What is the answer?"))
        {
            result.Append(delta);
        }

        Assert.Equal("The answer is 42.", result.ToString());
    }

    [Fact]
    public async Task SendAsync_RequestUrl_IsCorrect()
    {
        Uri? capturedUri = null;
        var handler = new CaptureHandler(req =>
        {
            capturedUri = req.RequestUri;
            return MakeSseResponse("data: [DONE]\n");
        });
        var client = new ZaiDirectClient("key", "glm-4.6v", new HttpClient(handler));

        await foreach (var _ in client.SendAsync(
            Array.Empty<Message>(),
            Array.Empty<CapturedScreen>(),
            "sys",
            "hi"))
        {
        }

        Assert.Equal("https://api.z.ai/api/paas/v4/chat/completions", capturedUri?.ToString());
    }

    [Fact]
    public async Task SendAsync_RequestHeaders_IncludeAcceptSseAndContentType()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new CaptureHandler(req =>
        {
            capturedRequest = req;
            return MakeSseResponse("data: [DONE]\n");
        });
        var client = new ZaiDirectClient("key", "glm-4.6v", new HttpClient(handler));

        await foreach (var _ in client.SendAsync(
            Array.Empty<Message>(),
            Array.Empty<CapturedScreen>(),
            "sys",
            "hi"))
        {
        }

        Assert.NotNull(capturedRequest);
        Assert.Contains("text/event-stream", capturedRequest!.Headers.GetValues("Accept"));
        Assert.Equal("application/json", capturedRequest.Content!.Headers.ContentType!.MediaType);
    }

    // ──────────────────────────────────────────────────────────
    // Test helpers
    // ──────────────────────────────────────────────────────────

    private static HttpResponseMessage MakeSseResponse(string sseContent)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(
                new MemoryStream(Encoding.UTF8.GetBytes(sseContent)))
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    /// <summary>
    /// Fake HttpMessageHandler that returns a pre-built SSE stream.
    /// </summary>
    private sealed class FakeSseHandler(string sseContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Skip TLS warmup HEAD requests
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            return Task.FromResult(MakeSseResponse(sseContent));
        }
    }

    /// <summary>
    /// Handler that captures the request and lets the test provide a custom response.
    /// </summary>
    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            return Task.FromResult(respond(request));
        }
    }

    /// <summary>
    /// Handler that delivers SSE content in two separate byte chunks to test
    /// UTF-8 chunk-boundary handling.
    /// </summary>
    private sealed class ChunkedSseHandler(byte[] chunk1, byte[] chunk2) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var stream = new ChunkedMemoryStream(chunk1, chunk2);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// A MemoryStream that returns data in two separate Read calls,
    /// simulating HTTP chunk boundaries.
    /// </summary>
    private sealed class ChunkedMemoryStream : Stream
    {
        private readonly byte[][] _chunks;
        private int _chunkIndex;
        private int _posInChunk;

        public ChunkedMemoryStream(params byte[][] chunks)
        {
            _chunks = chunks;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunkIndex >= _chunks.Length) return 0;

            var chunk = _chunks[_chunkIndex];
            var remaining = chunk.Length - _posInChunk;
            var toRead = Math.Min(count, remaining);
            Array.Copy(chunk, _posInChunk, buffer, offset, toRead);
            _posInChunk += toRead;

            if (_posInChunk >= chunk.Length)
            {
                _chunkIndex++;
                _posInChunk = 0;
            }

            return toRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _chunks.Sum(c => c.Length);
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
