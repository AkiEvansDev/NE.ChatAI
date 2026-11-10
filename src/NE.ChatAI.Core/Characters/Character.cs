using System.Collections.Generic;
using System.Text.Json.Serialization;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Characters;

/// <summary>What a character, a persona and a world share: a name, tags, the card's words, notes and the world it is played in.</summary>
public abstract record Card : NamedDocument
{
    public IReadOnlyList<string> Tags { get; init; } = [];

    public string Description { get; init; } = string.Empty;

    public string Personality { get; init; } = string.Empty;

    public string Scenario { get; init; } = string.Empty;

    public string CreatorNotes { get; init; } = string.Empty;

    /// <summary>An author's note of its own, added to the global one and the chat's.</summary>
    public string? AuthorsNote { get; init; }

    /// <summary>The world it is played in, by id: its articles go into the prompt; a character's wins over its persona's.</summary>
    public string? World { get; init; }
}

/// <summary>A character the model plays: a card, its first messages, examples and instructions of its own.</summary>
public sealed record Character : Card
{
    [JsonPropertyOrder(1)]
    public string ExampleMessages { get; init; } = string.Empty;

    /// <summary>The message a new chat opens with; more than one, and the reader swipes between them.</summary>
    [JsonPropertyOrder(1)]
    public IReadOnlyList<string> FirstMessages { get; init; } = [];

    /// <summary>The card's own system prompt, in place of the preset's; <c>{{original}}</c> in it is the preset's.</summary>
    [JsonPropertyOrder(1)]
    public string? SystemPrompt { get; init; }

    /// <summary>Instructions put after the history, just before the reply.</summary>
    [JsonPropertyOrder(1)]
    public string? PostHistoryInstructions { get; init; }
}

/// <summary>The reader's side of a chat: the same card, without what only a played character has.</summary>
public sealed record Persona : Card;

/// <summary>
/// A world, its world info: an overview (the card's description) and its articles — constant or woken by triggers in recent messages —
/// in a chat whose character, or else whose persona, names it.
/// </summary>
public sealed record World : Card
{
    public const int DefaultScanDepth = 4;

    /// <summary>How many recent messages of the chat are scanned for triggered articles.</summary>
    [JsonPropertyOrder(1)]
    public int ScanDepth { get; init; } = DefaultScanDepth;

    [JsonPropertyOrder(2)]
    public IReadOnlyList<WorldArticle> Articles { get; init; } = [];
}

/// <summary>One article of a world: a title for the reader, the words the prompt takes, where they go, and any triggers that wake it.</summary>
public sealed record WorldArticle
{
    public string Title { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    /// <summary>Keywords that include the article when matched in recent messages; empty, and the article is always on.</summary>
    public IReadOnlyList<string> Triggers { get; init; } = [];

    /// <summary>Before the card's description (<c>{{wiBefore}}</c>), after the scenario (<c>{{wiAfter}}</c>), or in the history at the note's depth.</summary>
    public LorePosition Position { get; init; } = LorePosition.AfterStory;

    public bool Enabled { get; init; } = true;
}

public enum LorePosition
{
    BeforeStory,
    AfterStory,
    InHistory
}
