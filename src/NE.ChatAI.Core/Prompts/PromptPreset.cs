using System.Text.Json.Serialization;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Prompts;

/// <summary>Whose turn a text put into the history is written as.</summary>
public enum PromptRole
{
    System,
    User,

    [JsonStringEnumMemberName("character")]
    Model
}

/// <summary>
/// Where the author's notes — global, the character's and the chat's, added together — stand in the history: <see cref="Depth"/>
/// messages from its end, 0 after the last and 1 before it, written as <see cref="Role"/>'s turn.
/// </summary>
public sealed record AuthorsNotePlacement
{
    public int Depth { get; init; } = 4;

    public PromptRole Role { get; init; } = PromptRole.System;
}

/// <summary>How a reply's thought is shown on the page.</summary>
public enum ThoughtDisplay
{
    Folded,
    Hidden
}

/// <summary>What happens to replies' thoughts after they are written.</summary>
public sealed record ThoughtHandling
{
    public ThoughtDisplay Show { get; init; } = ThoughtDisplay.Folded;

    /// <summary>Whether earlier replies' thoughts go back into the prompt; they rarely help and always cost context.</summary>
    public bool KeepInHistory { get; init; }
}

/// <summary>
/// The words around a chat, whatever the model: the story string over the history, and what becomes of thoughts. The system prompt
/// is a document of its own (<see cref="SystemPromptPreset"/>), so one preset serves several; where author's notes go is the
/// global note's (<see cref="Settings.AppSettings.AuthorsNotePlacement"/>).
/// </summary>
public sealed record PromptPreset : NamedDocument
{
    /// <summary>The top of the prompt in SillyTavern's syntax, <c>{{macro}}</c> and <c>{{#if macro}}…{{/if}}</c>; each macro is a block.</summary>
    public string StoryString { get; init; } = DefaultStoryString;

    /// <summary>What stands between the example messages and before each of them.</summary>
    public string ExampleSeparator { get; init; } = "***";

    /// <summary>What stands between the story and the chat's history.</summary>
    public string ChatStart { get; init; } = "***";

    /// <summary>Whether each message in the history and the reply's opening are prefixed with <c>{{char}}:</c> or <c>{{user}}:</c>.</summary>
    public bool IncludeNames { get; init; }

    public ThoughtHandling Thoughts { get; init; } = new();

    /// <summary>SillyTavern's own default story string.</summary>
    public const string DefaultStoryString =
        "{{#if system}}{{system}}\n{{/if}}{{#if wiBefore}}{{wiBefore}}\n{{/if}}{{#if description}}{{description}}\n{{/if}}"
        + "{{#if personality}}{{char}}'s personality: {{personality}}\n{{/if}}{{#if scenario}}Scenario: {{scenario}}\n{{/if}}"
        + "{{#if wiAfter}}{{wiAfter}}\n{{/if}}{{#if persona}}{{persona}}\n{{/if}}";
}
