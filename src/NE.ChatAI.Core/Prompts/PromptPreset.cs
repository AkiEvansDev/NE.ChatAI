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

/// <summary>Depth from the end of history (<c>0</c> after the last message) and turn role for injected author's notes.</summary>
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

    /// <summary>Whether earlier replies' thoughts are sent back in the assembled history.</summary>
    public bool KeepInHistory { get; init; }
}

/// <summary>Model-independent prompt template: story string, separators, speaker-name toggle and thought handling.</summary>
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
