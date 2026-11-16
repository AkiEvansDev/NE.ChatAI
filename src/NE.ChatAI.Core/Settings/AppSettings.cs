using System.Collections.Frozen;
using System.Collections.Generic;
using NE.ChatAI.Prompts;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Settings;

/// <summary>The application's own settings: one file, <c>settings.json</c>.</summary>
public sealed record AppSettings : Document
{
    /// <summary>How far a background picture is dimmed toward the theme's ground when the settings name no value yet.</summary>
    public const double DefaultBackgroundDim = 0.7;

    /// <summary>The connection a chat is generated with unless the chat says otherwise.</summary>
    public string? Connection { get; init; }

    public string? Persona { get; init; }

    /// <summary>The global author's note, added to the character's and the chat's.</summary>
    public string? AuthorsNote { get; init; }

    /// <summary>Where the author's notes, all three added together, stand in the prompt.</summary>
    public AuthorsNotePlacement AuthorsNotePlacement { get; init; } = new();

    /// <summary>The chat the home page opens when its address names none.</summary>
    public string? LastChat { get; init; }

    /// <summary>Whether each message in the chat stands on a subtle surface of its own over the background picture.</summary>
    public bool MessageBackground { get; init; }

    /// <summary>How far the picture behind the chat is dimmed toward the theme's ground, from 0 (none) to 1 (solid theme ground).</summary>
    public double BackgroundDim { get; init; } = DefaultBackgroundDim;

    /// <summary>The PIN's hash when one is set; none, and every browser on the network gets in.</summary>
    public string? PinHash { get; init; }

    /// <summary>The reader's quick phrases, each sent into the chat as it stands with one press.</summary>
    public IReadOnlyList<string> Phrases { get; init; } = [];

    /// <summary>The tags a character is given, each with the colour its badge is drawn in.</summary>
    public IReadOnlyList<TagStyle> Tags { get; init; } = [];

    /// <summary>The tags a persona is given, each with the colour its badge is drawn in.</summary>
    public IReadOnlyList<TagStyle> PersonaTags { get; init; } = [];

    /// <summary>The tags a world is given, each with the colour its badge is drawn in.</summary>
    public IReadOnlyList<TagStyle> WorldTags { get; init; } = [];

    /// <summary>Alias tags assigned to global gallery images (<c>imageId -&gt; tag</c>).</summary>
    public IReadOnlyDictionary<string, string> ImageTags { get; init; } = FrozenDictionary<string, string>.Empty;
}

/// <summary>A tag and its colour, written as the UI writes a colour; none, and the badge takes the theme's quiet one.</summary>
public sealed record TagStyle(string Name, string? Color);
