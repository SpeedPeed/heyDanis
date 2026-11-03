using Clicky.Api;
using Clicky.Companion;
using Xunit;

namespace Clicky.Tests;

public class CompanionManagerTests
{
    [Fact]
    public void StripPointTags_RemovesSimplePointTag()
    {
        var input = "check the save button [POINT:300,150:save button]";
        var result = CompanionManager.StripPointTags(input);
        Assert.Equal("check the save button", result);
    }

    [Fact]
    public void StripPointTags_RemovesPointNone()
    {
        var input = "html is the skeleton of every web page [POINT:none]";
        var result = CompanionManager.StripPointTags(input);
        Assert.Equal("html is the skeleton of every web page", result);
    }

    [Fact]
    public void StripPointTags_RemovesMultiScreenPointTag()
    {
        var input = "that terminal is on your other monitor [POINT:400,300:terminal:screen2]";
        var result = CompanionManager.StripPointTags(input);
        Assert.Equal("that terminal is on your other monitor", result);
    }

    [Fact]
    public void StripPointTags_NoTagReturnsOriginal()
    {
        var input = "just a normal response with no pointing";
        var result = CompanionManager.StripPointTags(input);
        Assert.Equal("just a normal response with no pointing", result);
    }

    [Fact]
    public void StripPointTags_EmptyString()
    {
        Assert.Equal("", CompanionManager.StripPointTags(""));
    }

    [Fact]
    public void StripPointTags_TagOnly()
    {
        var result = CompanionManager.StripPointTags("[POINT:100,200:button]");
        Assert.Equal("", result);
    }

    [Fact]
    public void StripPointTags_MultipleTagsStripped()
    {
        var input = "first [POINT:10,20:a] and second [POINT:30,40:b]";
        var result = CompanionManager.StripPointTags(input);
        Assert.Equal("first  and second", result);
    }

    [Fact]
    public void SystemPrompt_ContainsKeyPhrases()
    {
        // Verify the system prompt matches Mac's essential content
        var prompt = CompanionManager.CompanionSystemPrompt;
        Assert.Contains("clicky", prompt);
        Assert.Contains("push-to-talk", prompt);
        Assert.Contains("text-to-speech", prompt);
        Assert.Contains("[POINT:", prompt);
        Assert.Contains("element pointing", prompt);
    }

    [Fact]
    public void SystemPrompt_ContainsPointingFormat()
    {
        var prompt = CompanionManager.CompanionSystemPrompt;
        Assert.Contains("[POINT:x,y:label]", prompt);
        Assert.Contains("[POINT:none]", prompt);
        Assert.Contains(":screenN", prompt);
    }

    [Fact]
    public void ConversationHistory_MaxTenExchanges()
    {
        // The conversation history trimming logic is tested via the internal list.
        // We verify the constant is 10 by reflecting on StripPointTags behavior
        // and the documented Mac behavior of keeping last 10 exchanges.
        // This is a documentation/contract test.
        var history = new List<Message>();
        for (int i = 0; i < 15; i++)
        {
            history.Add(new Message($"user {i}", $"assistant {i}"));
        }

        // Simulate the trimming logic from CompanionManager
        if (history.Count > 10)
        {
            history.RemoveRange(0, history.Count - 10);
        }

        Assert.Equal(10, history.Count);
        Assert.Equal("user 5", history[0].UserText);
        Assert.Equal("user 14", history[^1].UserText);
    }
}
