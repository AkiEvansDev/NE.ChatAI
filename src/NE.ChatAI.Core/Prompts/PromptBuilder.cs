using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;

namespace NE.ChatAI.Prompts;

/// <summary>A message of the history as the prompt reads it: who wrote it, its words, the thought before them, and whether macros in its text expand.</summary>
public sealed record PromptMessage(MessageRole Role, string Text, string? Thought = null, bool ExpandMacros = false);

/// <summary>One logical turn of an assembled prompt: its role, the block it counts under, its words, and who speaks it.</summary>
public sealed record PromptTurn(PromptRole Role, string Block, string Text, string? Speaker = null);

/// <summary>Model-independent inputs for assembling a chat, impersonation or scenario prompt.</summary>
public sealed record PromptSpec
{
    /// <summary>The system prompt's words, <c>{{system}}</c> in the story string; a character's own wraps them as <c>{{original}}</c>.</summary>
    public string SystemPrompt { get; init; } = string.Empty;

    public PromptPreset Prompt { get; init; } = new();

    public Character? Character { get; init; }

    public Persona? Persona { get; init; }

    /// <summary>The character's name, <c>{{char}}</c>; falls back to <see cref="Character"/> when empty.</summary>
    public string CharacterName { get; init; } = string.Empty;

    /// <summary>The reader's name, <c>{{user}}</c>; falls back to <see cref="Persona"/> when empty.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>The articles of the world the chat is played in (<see cref="WorldInfo"/>).</summary>
    public IReadOnlyList<WorldArticle> Lore { get; init; } = [];

    /// <summary>The author's notes in order — the global one, the character's, the chat's — added together.</summary>
    public IReadOnlyList<string?> AuthorsNotes { get; init; } = [];

    /// <summary>Where the notes stand in the history: the global note's setting.</summary>
    public AuthorsNotePlacement NotePlacement { get; init; } = new();

    public IReadOnlyList<PromptMessage> History { get; init; } = [];

    /// <summary>An optional trailing turn placed right before the reply's opening — a scenario's instruction or an impersonation cue.</summary>
    public PromptTurn? Instruction { get; init; }

    /// <summary>Who speaks the reply's opening turn; <c>null</c> defaults to the character's name.</summary>
    public string? ReplySpeaker { get; init; }

    /// <summary>A reply being continued, its words so far: the reply's turn opens with them, and the model goes on from there.</summary>
    public string? Continuation { get; init; }
}

/// <summary>The names the prompt's blocks are counted under — what the charts show.</summary>
public static class PromptBlocks
{
    public const string System = "system";
    public const string Description = "description";
    public const string Personality = "personality";
    public const string Scenario = "scenario";
    public const string Persona = "persona";
    public const string Lore = "lore";
    public const string Examples = "examples";

    /// <summary>The story string's own words and the turn markers around it — what is left of the top once its macros are counted.</summary>
    public const string Story = "story";

    public const string AuthorsNote = "authorsNote";
    public const string History = "history";
    public const string PostHistory = "postHistory";
    public const string Instruction = "instruction";
    public const string Impersonate = "impersonate";

    /// <summary>The opening of the reply's turn.</summary>
    public const string Opening = "opening";
}

/// <summary>A prompt made: its rendered text (empty without a <see cref="ModelFormat"/>), its logical turns, its tokens by block and in all, and how much of the history it could hold.</summary>
public sealed record BuiltPrompt(string Text, IReadOnlyList<PromptTurn> Turns, IReadOnlyDictionary<string, int> Blocks, int Tokens, int MessagesSent, int MessagesDropped, TokenCounts Counts);

/// <summary>Builds a <see cref="PromptSpec"/> into counted blocks, logical turns and formatted text trimmed to fit the context window.</summary>
public static class PromptBuilder
{
    private const string ExampleStart = "<START>";

    private static readonly Dictionary<string, string> BlockOfMacro = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system"] = PromptBlocks.System,
        ["description"] = PromptBlocks.Description,
        ["personality"] = PromptBlocks.Personality,
        ["scenario"] = PromptBlocks.Scenario,
        ["persona"] = PromptBlocks.Persona,
        ["wiBefore"] = PromptBlocks.Lore,
        ["wiAfter"] = PromptBlocks.Lore,
        ["loreBefore"] = PromptBlocks.Lore,
        ["loreAfter"] = PromptBlocks.Lore,
        ["mesExamples"] = PromptBlocks.Examples,
        ["mesExamplesRaw"] = PromptBlocks.Examples
    };

    /// <summary>Assembles <paramref name="spec"/> within <c>contextLength - replyLength</c> tokens and counts each block through <paramref name="counter"/>.</summary>
    public static async Task<BuiltPrompt> BuildAsync(PromptSpec spec, ModelFormat? format, int contextLength, int replyLength, ITokenCounter counter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(counter);

        var characterName = spec.CharacterName.Length > 0 ? spec.CharacterName : spec.Character?.Name ?? string.Empty;
        var userName = spec.UserName.Length > 0 ? spec.UserName : spec.Persona?.Name ?? string.Empty;
        var replySpeaker = spec.ReplySpeaker ?? characterName;
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase) { ["char"] = characterName, ["user"] = userName };
        Dictionary<string, string> values = Values(spec, names);
        Dictionary<string, StringBuilder> contributions = [with(StringComparer.Ordinal)];

        var story = Macros.Render(spec.Prompt.StoryString, values, (macro, value) => Contribute(contributions, macro, value));
        var examplesInStory = contributions.ContainsKey(PromptBlocks.Examples);
        var top = Top(spec, names, values, story, examplesInStory);

        if (!examplesInStory)
            Contribute(contributions, "mesExamples", values["mesExamples"]);

        var historyLore = LoreText(spec, LorePosition.InHistory, names);
        var notes = Notes(spec, names);
        var postText = spec.Character?.PostHistoryInstructions is { Length: > 0 } instructions ? Macros.Render(instructions, names).TrimEnd('\r', '\n') : string.Empty;
        PromptTurn? instruction = spec.Instruction is { Text.Length: > 0 } rawInstruction ? rawInstruction with { Text = Macros.Render(rawInstruction.Text, names).TrimEnd('\r', '\n') } : null;
        ModelFormat? effectiveFormat = format is not null && spec.Prompt.IncludeNames && format.NamesBehavior != NamesBehavior.Always
            ? format with { NamesBehavior = NamesBehavior.Always }
            : format;
        var includeNames = spec.Prompt.IncludeNames || effectiveFormat?.NamesBehavior == NamesBehavior.Always;
        PromptTurn? formattedInstruction = instruction is null ? null : instruction with { Text = TurnText(includeNames, instruction.Role, instruction.Text, instruction.Speaker) };

        var system = effectiveFormat is null ? top : effectiveFormat.SystemText(top);
        var topTurn = system.Length == 0 ? string.Empty : effectiveFormat is null ? system : effectiveFormat.Turn(PromptRole.System, system);
        var loreTurn = historyLore.Length == 0 ? string.Empty : effectiveFormat is null ? historyLore : effectiveFormat.Turn(PromptRole.System, historyLore);
        var noteTurn = notes.Length == 0 ? string.Empty : effectiveFormat is null ? notes : effectiveFormat.Turn(spec.NotePlacement.Role, notes);
        var postTurn = postText.Length == 0 ? string.Empty : effectiveFormat is null ? postText : effectiveFormat.Turn(PromptRole.System, postText);
        var instructionTurn = formattedInstruction is null ? string.Empty : effectiveFormat is null ? formattedInstruction.Text : effectiveFormat.Turn(formattedInstruction.Role, formattedInstruction.Text);

        var bareOpening = effectiveFormat is null ? string.Empty : effectiveFormat.Opening(replySpeaker);
        var opening = effectiveFormat is null
            ? spec.Continuation ?? string.Empty
            : spec.Continuation is string words ? effectiveFormat.Continuing(replySpeaker, words) : bareOpening;

        Dictionary<string, int> blocks = [with(StringComparer.Ordinal)];
        var topTokens = await counter.CountAsync(topTurn, cancellationToken).ConfigureAwait(false);
        var loreTokens = await counter.CountAsync(loreTurn, cancellationToken).ConfigureAwait(false);
        var noteTokens = await counter.CountAsync(noteTurn, cancellationToken).ConfigureAwait(false);
        var postTokens = await counter.CountAsync(postTurn, cancellationToken).ConfigureAwait(false);
        var instructionTokens = await counter.CountAsync(instructionTurn, cancellationToken).ConfigureAwait(false);
        var openingTokens = await counter.CountAsync(opening, cancellationToken).ConfigureAwait(false);
        var bareOpeningTokens = effectiveFormat is null ? 0 : spec.Continuation is null ? openingTokens : await counter.CountAsync(bareOpening, cancellationToken).ConfigureAwait(false);
        // A continuation puts the message's words so far into the opening turn, yet those words belong to history on the chart.
        var continuationTokens = Math.Max(0, openingTokens - bareOpeningTokens);
        var openingMarkerTokens = Math.Min(openingTokens, bareOpeningTokens);

        var room = contextLength - replyLength - topTokens - loreTokens - noteTokens - postTokens - instructionTokens - openingTokens;
        List<KeptMessage> kept = [];
        var historyTokens = 0;

        for (var i = spec.History.Count - 1; i >= 0; i--)
        {
            PromptMessage message = spec.History[i];
            PromptRole role = RoleOf(message);
            var speaker = SpeakerOf(message.Role, characterName, userName);
            var body = HistoryText(spec, effectiveFormat, message, names, includeNames ? speaker : null);
            var tokens = await counter.CountAsync(effectiveFormat is null ? body : effectiveFormat.Turn(role, body), cancellationToken).ConfigureAwait(false);

            if (historyTokens + tokens > room)
                break;

            kept.Insert(0, new KeptMessage(role, speaker, body));
            historyTokens += tokens;
        }

        var noteAt = NoteInsertionIndex(kept.Count, spec.NotePlacement.Depth);
        IReadOnlyList<PromptTurn> turns = AssembleTurns(top, kept, historyLore, notes, spec.NotePlacement.Role, noteAt, postText, formattedInstruction, spec.Continuation, replySpeaker);
        var text = effectiveFormat is null ? string.Empty : AssembleText(effectiveFormat, topTurn, kept, loreTurn, noteTurn, noteAt, postTurn, instructionTurn, opening);

        await CountContributionsAsync(blocks, contributions, counter, cancellationToken).ConfigureAwait(false);

        Add(blocks, PromptBlocks.Story, Math.Max(0, topTokens - blocks.Values.Sum()));
        Add(blocks, PromptBlocks.Lore, loreTokens);
        Add(blocks, PromptBlocks.AuthorsNote, noteTokens);
        Add(blocks, PromptBlocks.History, historyTokens + continuationTokens);
        Add(blocks, PromptBlocks.PostHistory, postTokens);

        if (formattedInstruction is not null)
            Add(blocks, formattedInstruction.Block, instructionTokens);

        Add(blocks, PromptBlocks.Opening, openingMarkerTokens);

        var total = format is null
            ? topTokens + historyTokens + loreTokens + noteTokens + postTokens + instructionTokens + openingTokens
            : await counter.CountAsync(text, cancellationToken).ConfigureAwait(false);

        return new BuiltPrompt(text, turns, blocks, total, kept.Count, spec.History.Count - kept.Count, counter.Kind);
    }

    private static Dictionary<string, string> Values(PromptSpec spec, Dictionary<string, string> names)
    {
        Character? character = spec.Character;
        var presetSystem = Macros.Render(spec.SystemPrompt, names).TrimEnd('\r', '\n');
        var system = (character?.SystemPrompt is { Length: > 0 } own ? Macros.Render(own, new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase) { ["original"] = presetSystem }) : presetSystem).TrimEnd('\r', '\n');
        var before = LoreText(spec, LorePosition.BeforeStory, names);
        var after = LoreText(spec, LorePosition.AfterStory, names);
        var examplesRaw = Macros.Render(character?.ExampleMessages ?? string.Empty, names).Trim();

        return new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase)
        {
            ["system"] = system,
            ["description"] = Macros.Render(character?.Description ?? string.Empty, names).TrimEnd('\r', '\n'),
            ["personality"] = Macros.Render(character?.Personality ?? string.Empty, names).TrimEnd('\r', '\n'),
            ["scenario"] = Macros.Render(character?.Scenario ?? string.Empty, names).TrimEnd('\r', '\n'),
            ["persona"] = Macros.Render(spec.Persona?.Description ?? string.Empty, names).TrimEnd('\r', '\n'),
            ["wiBefore"] = before,
            ["wiAfter"] = after,
            ["loreBefore"] = before,
            ["loreAfter"] = after,
            ["mesExamples"] = Examples(examplesRaw, Macros.Render(spec.Prompt.ExampleSeparator, names)),
            ["mesExamplesRaw"] = examplesRaw
        };
    }

    private static string LoreText(PromptSpec spec, LorePosition position, Dictionary<string, string> names)
        => string.Join('\n', spec.Lore.Where(entry => entry.Enabled && entry.Position == position && entry.Text.Length > 0).Select(entry => Macros.Render(entry.Text, names).Trim()));

    private static string Examples(string examples, string separator)
    {
        if (examples.Length == 0)
            return string.Empty;

        IEnumerable<string> dialogues = examples.Split(ExampleStart, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return separator.Length == 0
            ? string.Join("\n\n", dialogues)
            : string.Join('\n', dialogues.Select(dialogue => $"{separator}\n{dialogue}"));
    }

    private static void Contribute(Dictionary<string, StringBuilder> contributions, string macro, string value)
    {
        if (!BlockOfMacro.TryGetValue(macro, out var block) || value.Length == 0)
            return;

        if (!contributions.TryGetValue(block, out StringBuilder? text))
            contributions[block] = text = new StringBuilder();

        _ = text.Append(value);
    }

    private static string Top(PromptSpec spec, Dictionary<string, string> names, Dictionary<string, string> values, string story, bool examplesInStory)
    {
        var top = story.Trim('\r', '\n');

        if (!examplesInStory && values["mesExamples"].Length > 0)
            top = Join(top, values["mesExamples"]);

        return spec.Prompt.ChatStart.Length > 0 ? Join(top, Macros.Render(spec.Prompt.ChatStart, names)) : top;
    }

    private static string Join(string first, string second)
        => first.Length == 0 ? second : second.Length == 0 ? first : $"{first}\n{second}";

    private static string Notes(PromptSpec spec, Dictionary<string, string> names)
        => string.Join('\n', spec.AuthorsNotes.Where(static note => !string.IsNullOrWhiteSpace(note)).Select(note => Macros.Render(note!, names).Trim()));

    private static string TurnText(bool includeNames, PromptRole role, string text, string? speaker)
        => includeNames && role != PromptRole.System && !string.IsNullOrEmpty(speaker)
            ? $"{speaker}: {text}"
            : text;

    private static PromptRole RoleOf(PromptMessage message)
        => message.Role switch
        {
            MessageRole.User => PromptRole.User,
            MessageRole.Character => PromptRole.Model,
            _ => PromptRole.System
        };

    private static string? SpeakerOf(MessageRole role, string characterName, string userName)
    {
        var speaker = role switch
        {
            MessageRole.User => userName,
            MessageRole.Character => characterName,
            _ => string.Empty
        };

        return speaker.Length == 0 ? null : speaker;
    }

    private static string HistoryText(PromptSpec spec, ModelFormat? format, PromptMessage message, IReadOnlyDictionary<string, string> names, string? speaker)
    {
        var rawText = message.ExpandMacros ? Macros.Render(message.Text, names) : message.Text;
        var text = rawText.TrimEnd('\r', '\n');

        if (message.Role != MessageRole.System && !string.IsNullOrEmpty(speaker))
            text = $"{speaker}: {text}";

        if (message.Role != MessageRole.Character || !spec.Prompt.Thoughts.KeepInHistory || string.IsNullOrEmpty(message.Thought))
            return text;

        var thought = message.Thought.TrimEnd('\r', '\n');

        if (format is null)
            return $"<think>{thought}</think>{text}";

        return format.Thought.IsDefined ? $"{format.Thought.Prefix}{thought}{format.Thought.Suffix}{text}" : text;
    }

    private static int NoteInsertionIndex(int messageCount, int depth)
        => Math.Clamp(messageCount - Math.Max(0, depth), 0, messageCount);

    private static List<PromptTurn> AssembleTurns(string top, List<KeptMessage> kept, string historyLore, string notes, PromptRole noteRole, int noteAt, string postText, PromptTurn? instruction, string? continuation, string replySpeaker)
    {
        List<PromptTurn> turns = [];

        if (top.Length > 0)
            turns.Add(new PromptTurn(PromptRole.System, PromptBlocks.Story, top));

        for (var i = 0; i <= kept.Count; i++)
        {
            if (i == noteAt)
            {
                if (historyLore.Length > 0)
                    turns.Add(new PromptTurn(PromptRole.System, PromptBlocks.Lore, historyLore));

                if (notes.Length > 0)
                    turns.Add(new PromptTurn(noteRole, PromptBlocks.AuthorsNote, notes));
            }

            if (i < kept.Count)
            {
                KeptMessage message = kept[i];
                turns.Add(new PromptTurn(message.Role, PromptBlocks.History, message.Body, message.Speaker));
            }
        }

        if (postText.Length > 0)
            turns.Add(new PromptTurn(PromptRole.System, PromptBlocks.PostHistory, postText));

        if (instruction is not null)
            turns.Add(instruction);

        if (!string.IsNullOrEmpty(continuation))
            turns.Add(new PromptTurn(PromptRole.Model, PromptBlocks.History, continuation, replySpeaker.Length == 0 ? null : replySpeaker));

        return turns;
    }

    private static string AssembleText(ModelFormat format, string topTurn, List<KeptMessage> kept, string loreTurn, string noteTurn, int noteAt, string postTurn, string instructionTurn, string opening)
    {
        StringBuilder prompt = new(topTurn);
        var firstOfModel = true;

        for (var i = 0; i <= kept.Count; i++)
        {
            if (i == noteAt)
                _ = prompt.Append(loreTurn).Append(noteTurn);

            if (i < kept.Count)
            {
                KeptMessage message = kept[i];
                _ = prompt.Append(format.Turn(message.Role, message.Body, firstOfModel && message.Role == PromptRole.Model));
                firstOfModel &= message.Role != PromptRole.Model;
            }
        }

        return prompt.Append(postTurn).Append(instructionTurn).Append(opening).ToString();
    }

    private static async Task CountContributionsAsync(Dictionary<string, int> blocks, Dictionary<string, StringBuilder> contributions, ITokenCounter counter, CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<string, StringBuilder> contribution in contributions)
            Add(blocks, contribution.Key, await counter.CountAsync(contribution.Value.ToString(), cancellationToken).ConfigureAwait(false));
    }

    private static void Add(Dictionary<string, int> blocks, string block, int tokens)
    {
        if (tokens > 0)
            blocks[block] = blocks.GetValueOrDefault(block) + tokens;
    }

    private readonly record struct KeptMessage(PromptRole Role, string? Speaker, string Body);
}
