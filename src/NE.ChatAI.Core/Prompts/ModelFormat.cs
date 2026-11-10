using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Prompts;

/// <summary>How a model's turns carry the speaker's name, as SillyTavern's instruct templates say it.</summary>
public enum NamesBehavior
{
    /// <summary>No names: the turn markers say who speaks.</summary>
    None,

    /// <summary>The name opens a turn only where the speaker is not the obvious one (a group, an example).</summary>
    Force,

    /// <summary>Every turn opens with the speaker's name.</summary>
    Always
}

/// <summary>How a thinking model marks what it thinks before it answers.</summary>
public sealed record ThoughtFormat
{
    /// <summary>Off, the reply is opened with an empty thought, so the model answers at once and spends no tokens on it.</summary>
    public bool Enabled { get; init; } = true;

    public string Prefix { get; init; } = string.Empty;

    public string Suffix { get; init; } = string.Empty;

    /// <summary>What opens the system text while thinking is on: Gemma 4 thinks only when <c>&lt;|think|&gt;</c> starts it.</summary>
    public string SystemMark { get; init; } = string.Empty;

    /// <summary>
    /// On, a reply with thinking on is opened inside its thought — the prompt ends with <see cref="Prefix"/>, as Qwen's template does —
    /// so the model thinks every time rather than when it chooses to.
    /// </summary>
    public bool OpensReply { get; init; }

    /// <summary>Whether the reply starts inside the thought: the format marks one, thinking is on, and the prompt opens it.</summary>
    [JsonIgnore]
    public bool OpenedByPrompt => IsDefined && Enabled && OpensReply;

    /// <summary>Whether the format marks thoughts at all.</summary>
    [JsonIgnore]
    public bool IsDefined => Prefix.Length > 0 && Suffix.Length > 0;
}

/// <summary>A model's turn markers — what opens and closes each speaker's turn — its stop sequences, and its thought's markers.</summary>
public sealed record ModelFormat : NamedDocument
{
    public string SystemPrefix { get; init; } = string.Empty;

    public string SystemSuffix { get; init; } = string.Empty;

    public string UserPrefix { get; init; } = string.Empty;

    public string UserSuffix { get; init; } = string.Empty;

    public string ModelPrefix { get; init; } = string.Empty;

    public string ModelSuffix { get; init; } = string.Empty;

    /// <summary>What opens the first model turn in place of <see cref="ModelPrefix"/>, when it differs.</summary>
    public string? FirstModelPrefix { get; init; }

    /// <summary>What opens the model turn being generated in place of <see cref="ModelPrefix"/>, when it differs.</summary>
    public string? LastModelPrefix { get; init; }

    /// <summary>The system text goes in user turns' markers: a model with no system role of its own.</summary>
    public bool SystemSameAsUser { get; init; }

    /// <summary>Whether a line break follows each turn's opening marker.</summary>
    public bool Wrap { get; init; }

    public NamesBehavior NamesBehavior { get; init; } = NamesBehavior.None;

    public IReadOnlyList<string> StopSequences { get; init; } = [];

    /// <summary>Whether the turn markers stop generation as well as <see cref="StopSequences"/>.</summary>
    public bool SequencesAsStop { get; init; } = true;

    public ThoughtFormat Thought { get; init; } = new();

    /// <summary>Gemma 4's official turns, thinking off: the same as <c>default/instruct/gemma-4-d1000001.json</c>.</summary>
    public static ModelFormat Gemma4 { get; } = new()
    {
        Name = "Gemma 4",
        SystemPrefix = "<|turn>system\n",
        SystemSuffix = "<turn|>\n",
        UserPrefix = "<|turn>user\n",
        UserSuffix = "<turn|>\n",
        ModelPrefix = "<|turn>model\n",
        ModelSuffix = "<turn|>\n",
        NamesBehavior = NamesBehavior.Force,
        StopSequences = ["<turn|>", "<|turn>"],
        Thought = new ThoughtFormat { Enabled = false, Prefix = "<|channel>thought\n", Suffix = "<channel|>", SystemMark = "<|think|>\n" }
    };

    /// <summary>Every sequence generation stops at: the stop sequences, and the turn markers where they count as stops.</summary>
    public IReadOnlyList<string> AllStopSequences()
    {
        List<string> stops = [.. StopSequences];

        if (SequencesAsStop)
        {
            foreach (var marker in (ReadOnlySpan<string?>)[UserPrefix, ModelPrefix, SystemPrefix, FirstModelPrefix, LastModelPrefix, ModelSuffix, UserSuffix, SystemSuffix])
            {
                if (marker?.Trim() is { Length: > 0 } trimmed && !stops.Contains(trimmed))
                    stops.Add(trimmed);
            }
        }

        return stops;
    }

    /// <summary>
    /// The whole prompt of a system text and turns, in this format: what the prompt builder does with its blocks, for a format
    /// looked at on its own.
    /// </summary>
    public string Render(string system, IEnumerable<(bool FromUser, string Text)> turns)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(turns);

        StringBuilder prompt = new();
        var opening = SystemText(system);

        if (opening.Length > 0)
            _ = prompt.Append(Turn(PromptRole.System, opening));

        var first = true;

        foreach ((var fromUser, var text) in turns)
        {
            _ = prompt.Append(Turn(fromUser ? PromptRole.User : PromptRole.Model, text, first && !fromUser));
            first &= fromUser;
        }

        return prompt.Append(Opening(null)).ToString();
    }

    /// <summary>The system text the prompt opens with: after <see cref="ThoughtFormat.SystemMark"/> while thinking is on.</summary>
    public string SystemText(string system)
        => Thought.Enabled && Thought.SystemMark.Length > 0 ? Thought.SystemMark + system : system;

    /// <summary>
    /// One turn between its role's markers — a system turn in a user's where the model has no system role, the model's first turn
    /// in <see cref="FirstModelPrefix"/> when there is one; under <see cref="Wrap"/> the opening marker stands on a line of its own.
    /// </summary>
    /// <remarks>SillyTavern's wrap puts the break after the opening marker only: the suffix carries its own line breaks.</remarks>
    public string Turn(PromptRole role, string text, bool firstOfModel = false)
    {
        ArgumentNullException.ThrowIfNull(text);

        (var prefix, var suffix) = role switch
        {
            PromptRole.User => (UserPrefix, UserSuffix),
            PromptRole.Model => (firstOfModel && FirstModelPrefix is not null ? FirstModelPrefix : ModelPrefix, ModelSuffix),
            _ => SystemSameAsUser ? (UserPrefix, UserSuffix) : (SystemPrefix, SystemSuffix)
        };

        return string.Concat(prefix, Wrap && prefix.Length > 0 ? "\n" : string.Empty, text, suffix);
    }

    /// <summary>
    /// The opening of the model's turn the reply continues: its marker, an empty thought after it when thinking is off, so the model
    /// answers at once, or the thought's opening when the format opens it; then the speaker's name where the format writes names —
    /// unless the thought is open, where the name would be part of it.
    /// </summary>
    public string Opening(string? name)
    {
        StringBuilder opening = OpenTurn();

        if (Thought.IsDefined && !Thought.Enabled)
            _ = opening.Append(Thought.Prefix).Append(Thought.Suffix);
        else if (Thought.OpenedByPrompt)
            _ = opening.Append(Thought.Prefix);

        if (NamesBehavior == NamesBehavior.Always && !string.IsNullOrEmpty(name) && (!Thought.IsDefined || !Thought.Enabled))
            _ = opening.Append(name).Append(':');

        return opening.ToString();
    }

    /// <summary>The model's turn opened for the reply: its marker, and the break after it where the format wraps.</summary>
    private StringBuilder OpenTurn()
    {
        var marker = LastModelPrefix ?? ModelPrefix;
        StringBuilder opening = new(marker);

        return Wrap && marker.Length > 0 ? opening.Append('\n') : opening;
    }

    /// <summary>
    /// The opening of a reply being continued: the turn's marker, an empty thought where the format marks thoughts — the model
    /// thought already, and goes on writing — the name where the format writes names, and <paramref name="words"/>, the reply so far.
    /// </summary>
    public string Continuing(string? name, string words)
    {
        ArgumentNullException.ThrowIfNull(words);

        StringBuilder opening = OpenTurn();

        if (Thought.IsDefined)
            _ = opening.Append(Thought.Prefix).Append(Thought.Suffix);

        if (NamesBehavior == NamesBehavior.Always && !string.IsNullOrEmpty(name))
            _ = opening.Append(name).Append(": ");

        return opening.Append(words).ToString();
    }
}
