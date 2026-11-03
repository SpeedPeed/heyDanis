namespace Clicky.Api;

/// <summary>
/// A single turn in the conversation history, mirroring the
/// <c>(userPlaceholder, assistantResponse)</c> tuples in ClaudeAPI.swift.
/// </summary>
public sealed record Message(string UserText, string AssistantText);
