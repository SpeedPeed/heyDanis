using System.Text.Json;
using Clicky.Api;
using Xunit;

namespace Clicky.Tests;

public class CodexAppServerClientTests
{
    [Fact]
    public void ExtractAgentMessageDelta_UsesDeltaInsteadOfFirstString()
    {
        using var doc = JsonDocument.Parse("""
        {
          "threadId": "thread-random-id",
          "turnId": "turn-random-id",
          "itemId": "item-random-id",
          "delta": "actual assistant text"
        }
        """);

        var delta = CodexAppServerClient.ExtractAgentMessageDelta(doc.RootElement);

        Assert.Equal("actual assistant text", delta);
    }

    [Fact]
    public void ExtractAgentMessageDelta_ReturnsNullWhenNoTextDeltaExists()
    {
        using var doc = JsonDocument.Parse("""
        {
          "threadId": "thread-random-id",
          "turnId": "turn-random-id",
          "itemId": "item-random-id"
        }
        """);

        var delta = CodexAppServerClient.ExtractAgentMessageDelta(doc.RootElement);

        Assert.Null(delta);
    }
}
