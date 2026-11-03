using System.Drawing;
using System.Net;
using System.Text;
using System.Text.Json;
using Clicky.Api;
using Clicky.Capture;
using Xunit;

namespace Clicky.Tests;

public class OpenAiDirectClientTests
{
    [Fact]
    public async Task SendAsync_TextOnly_BuildsOpenAiChatRequest()
    {
        string? capturedBody = null;
        var handler = new CaptureHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return MakeSseResponse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n");
        });
        var client = new OpenAiDirectClient("test-key", "gpt-5.2", new HttpClient(handler));

        await foreach (var _ in client.SendAsync(
            Array.Empty<Message>(),
            Array.Empty<CapturedScreen>(),
            "You are helpful.",
            "Hello"))
        {
        }

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;

        Assert.Equal("gpt-5.2", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(1024, root.GetProperty("max_completion_tokens").GetInt32());

        var messages = root.GetProperty("messages");
        Assert.Equal("developer", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are helpful.", messages[0].GetProperty("content").GetString());

        var content = messages[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Single(content.EnumerateArray());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Hello", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendAsync_SingleImage_BuildsImageUrlBlock()
    {
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
        var client = new OpenAiDirectClient("test-key", "gpt-5.2", new HttpClient(handler));

        await foreach (var _ in client.SendAsync(
            Array.Empty<Message>(),
            new[] { screen },
            "system",
            "What do you see?"))
        {
        }

        using var doc = JsonDocument.Parse(capturedBody!);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content");

        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("image_url", content[0].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", content[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("Screen 1 (cursor is here)", content[1].GetProperty("text").GetString());
        Assert.Equal("What do you see?", content[2].GetProperty("text").GetString());
    }

    [Fact]
    public void ExtractDeltaContent_ValidChunk_ReturnsDelta()
    {
        var json = """{"choices":[{"delta":{"content":"Hello"}}]}""";
        Assert.Equal("Hello", OpenAiDirectClient.ExtractDeltaContent(json));
    }

    [Fact]
    public void ExtractDeltaContent_MalformedJson_ReturnsNull()
    {
        Assert.Null(OpenAiDirectClient.ExtractDeltaContent("not json"));
        Assert.Null(OpenAiDirectClient.ExtractDeltaContent("{broken"));
    }

    [Fact]
    public async Task SendAsync_RequestUrlAndHeaders_AreCorrect()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new CaptureHandler(req =>
        {
            capturedRequest = req;
            return MakeSseResponse("data: [DONE]\n");
        });
        var http = new HttpClient(handler);
        var client = new OpenAiDirectClient("key", "gpt-5.2", http);

        await foreach (var _ in client.SendAsync(
            Array.Empty<Message>(),
            Array.Empty<CapturedScreen>(),
            "sys",
            "hi"))
        {
        }

        Assert.Equal("https://api.openai.com/v1/chat/completions", capturedRequest?.RequestUri?.ToString());
        Assert.NotNull(capturedRequest);
        Assert.Contains("text/event-stream", capturedRequest!.Headers.GetValues("Accept"));
        Assert.Equal("application/json", capturedRequest.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("Bearer key", http.DefaultRequestHeaders.GetValues("Authorization"));
    }

    [Fact]
    public async Task SendAsync_HttpError_ThrowsHttpRequestException()
    {
        var handler = new CaptureHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid api key\"}")
            });
        var client = new OpenAiDirectClient("bad-key", "gpt-5.2", new HttpClient(handler));

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

    private static HttpResponseMessage MakeSseResponse(string sseContent)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sseContent)))
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }

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
}
