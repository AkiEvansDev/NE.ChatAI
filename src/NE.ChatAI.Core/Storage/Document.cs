using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NE.ChatAI.Storage;

/// <summary>What every kept document has: its format's number, and what a reader did not know, kept to be written back.</summary>
public abstract record Document
{
    /// <summary>The format this application writes; a file of a newer one is refused, not guessed at.</summary>
    public const int CurrentFormat = 1;

    [JsonPropertyOrder(-2)]
    public int Format { get; init; } = CurrentFormat;

    /// <summary>Fields this version does not know — a newer version's, SillyTavern's — read and written back as they were.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>A document with a name a person gives it, shown in lists and slugged into its id when it is made.</summary>
public abstract record NamedDocument : Document
{
    [JsonPropertyOrder(-1)]
    public string Name { get; init; } = string.Empty;
}
