using NE.ChatAI.Storage;

namespace NE.ChatAI.Prompts;

/// <summary>The instruction over everything else in the prompt, a document of its own: one prompt preset serves several of them.</summary>
public sealed record SystemPromptPreset : NamedDocument
{
    /// <summary>The instruction itself, with macros (<c>{{char}}</c>, <c>{{user}}</c>); the story string puts it in as <c>{{system}}</c>.</summary>
    public string Content { get; init; } = string.Empty;
}
