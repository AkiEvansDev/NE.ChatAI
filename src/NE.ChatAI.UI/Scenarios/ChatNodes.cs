using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Backends.KoboldCpp;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Scenarios;

/// <summary>The node catalogue shared by the <c>/scenarios</c> canvas and headless scenario runs from a chat.</summary>
internal static class ScenarioCatalog
{
    public const string ChatCategory = "Chat";
    public const string InputColor = "var(--ui-color-primary)";
    public const string PromptColor = "var(--ui-color-accent)";
    public const string WriteColor = "var(--ui-color-series-2)";
    public const string CueColor = "var(--ui-color-series-3)";

    public static IReadOnlyList<Type> BuiltInTypes { get; } =
    [
        .. UINodeKinds.Common,
        typeof(PromptNode),
        typeof(PersonaNode),
        typeof(CharacterNode),
        typeof(WorldNode),
        typeof(MessagesNode),
        typeof(RecordNode),
        typeof(ReadDraftNode),
        typeof(GalleryNode),
        typeof(ContextNode),
        typeof(TriggerMatchNode),
        typeof(CueNode),
        typeof(CueMenuNode),
        typeof(CuePickNode),
        typeof(CollectorNode),
        typeof(ModelCallNode),
        typeof(ReplaceRecordNode),
        typeof(InsertRecordNode),
        typeof(WriteDraftNode),
        typeof(SetVisualNode)
    ];

    public static UINodeCatalog BuiltIn { get; } = UINodeCatalog.FromTypes([.. BuiltInTypes]);

    public static FrozenSet<string> BuiltInKeys { get; } = BuildBuiltInKeys();

    public static UINodeCatalog Instance => BuiltIn;

    public static UINodeCatalog Resolve(IServiceProvider? services)
        => services?.GetService(typeof(Plugins.PluginManager)) is Plugins.PluginManager plugins ? plugins.Catalog : BuiltIn;

    private static FrozenSet<string> BuildBuiltInKeys()
    {
        HashSet<string> keys = [with(StringComparer.Ordinal)];

        for (var i = 0; i < BuiltIn.Types.Count; i++)
            _ = keys.Add(BuiltIn.Types[i].Key);

        return keys.ToFrozenSet(StringComparer.Ordinal);
    }
}

/// <summary>Reads the system prompt of the chat's active connection.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Prompt", Description = "The system prompt of the chat's connection.", Icon = UIGlyphs.TextFields, Color = ScenarioCatalog.InputColor, AlwaysRuns = true)]
internal sealed class PromptNode : IGraphNode
{
    public const string NodeKey = "chat.prompt";

    [GraphOutput(Title = "Prompt")]
    public string Prompt { get; set; } = string.Empty;

    public void Execute(UINodeRunContext context)
        => Prompt = ScenarioRunScope.From(context).ActiveProfile?.SystemPrompt.Content ?? string.Empty;
}

/// <summary>Reads the chat's persona: its structured card, name, text, and tags.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Persona", Description = "The chat's persona: its name, description, and tags.", Icon = AppIcons.Persona, Color = ScenarioCatalog.InputColor, AlwaysRuns = true)]
internal sealed class PersonaNode : IGraphNode
{
    public const string NodeKey = "chat.persona";

    [GraphOutput(Title = "Persona")]
    public PersonaCard? Persona { get; set; }

    [GraphOutput(Title = "Name")]
    public string Name { get; set; } = string.Empty;

    [GraphOutput(Title = "Text")]
    public string Text { get; set; } = string.Empty;

    [GraphOutput(Title = "Tags")]
    public string[] Tags { get; set; } = [];

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);
        Name = scope.UserName;
        Text = scope.Persona?.Description ?? string.Empty;
        Tags = scope.Persona is { Tags.Count: > 0 } persona ? [.. persona.Tags] : [];
        Persona = scope.Persona is null ? null : new PersonaCard(scope.Persona, scope.UserName);
    }
}

/// <summary>Reads the chat's character: its card, name, author's note, and tags.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Character", Description = "The chat's character: its name, card, author's note, and tags.", Icon = AppIcons.Chats, Color = ScenarioCatalog.InputColor, AlwaysRuns = true)]
internal sealed class CharacterNode : IGraphNode
{
    public const string NodeKey = "chat.character";

    [GraphOutput(Title = "Character")]
    public CharacterCard? Character { get; set; }

    [GraphOutput(Title = "Name")]
    public string Name { get; set; } = string.Empty;

    [GraphOutput(Title = "Card")]
    public string Card { get; set; } = string.Empty;

    [GraphOutput(Title = "Author's note")]
    public string AuthorsNote { get; set; } = string.Empty;

    [GraphOutput(Title = "Tags")]
    public string[] Tags { get; set; } = [];

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);
        CharacterCard? card = scope.Character is null ? null : new CharacterCard(scope.Character, scope.CharacterName);
        Character = card;
        Name = scope.CharacterName;
        Card = card?.CardText ?? string.Empty;
        AuthorsNote = scope.Character?.AuthorsNote ?? string.Empty;
        Tags = scope.Character is { Tags.Count: > 0 } character ? [.. character.Tags] : [];
    }
}

/// <summary>Reads the world the chat is played in: its overview, enabled articles, and tags.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "World", Description = "The chat's world: its overview, enabled articles, and tags.", Icon = UIGlyphs.Folder, Color = ScenarioCatalog.InputColor, AlwaysRuns = true)]
internal sealed class WorldNode : IGraphNode
{
    public const string NodeKey = "chat.world";

    [GraphOutput(Title = "World")]
    public WorldLore? World { get; set; }

    [GraphOutput(Title = "Overview")]
    public string Overview { get; set; } = string.Empty;

    [GraphOutput(Title = "Articles")]
    public string[] Articles { get; set; } = [];

    [GraphOutput(Title = "Tags")]
    public string[] Tags { get; set; } = [];

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);

        if (scope.World is not World world)
        {
            World = null;
            Overview = string.Empty;
            Articles = [];
            Tags = [];
            return;
        }

        IReadOnlyList<PromptMessage> history = ChatRecords.ProjectHistory(scope.Chat.Messages, scope.Chat.Records);
        IReadOnlyList<WorldArticle> articles = WorldInfo.ArticlesOf(world, history, scope.PendingOutgoingDraft);
        var skipOverview = !string.IsNullOrWhiteSpace(world.Description) ? 1 : 0;
        var enabledTexts = new string[Math.Max(0, articles.Count - skipOverview)];

        for (var i = skipOverview; i < articles.Count; i++)
            enabledTexts[i - skipOverview] = articles[i].Text;

        World = new WorldLore(world, articles);
        Overview = world.Description;
        Articles = enabledTexts;
        Tags = world.Tags.Count > 0 ? [.. world.Tags] : [];
    }
}

/// <summary>Takes a slice of the chat's history as the prompt sees it, or raw without records.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Messages", Description = "A slice of the chat's history, carrying its message ids.", Icon = AppIcons.Phrases, Color = ScenarioCatalog.InputColor, AlwaysRuns = true)]
internal sealed class MessagesNode : IGraphNode
{
    public const string NodeKey = "chat.messages";

    [GraphInput(Title = "Skip start", Min = 0)]
    public int SkipStart { get; set; }

    [GraphInput(Title = "Skip end", Min = 0)]
    public int SkipEnd { get; set; }

    [GraphInput(Title = "Last", Min = 0, Description = "How many from the end; 0 for all.")]
    public int Last { get; set; }

    [GraphInput(Title = "Raw", Description = "Reads the messages as they are, ignoring records.")]
    public bool Raw { get; set; }

    [GraphOutput(Title = "Slice")]
    public MessageSlice Slice { get; set; } = MessageSlice.Empty;

    [GraphOutput(Title = "Text")]
    public string Text { get; set; } = string.Empty;

    [GraphOutput(Title = "Count")]
    public int Count { get; set; }

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);
        ChatHistorySlice sliced = ChatRecords.Slice(scope.Chat.Messages, scope.Chat.Records, SkipStart, SkipEnd, Last, Raw);
        MessageSlice result = MessageSlice.From(sliced, scope.CharacterName, scope.UserName);
        scope.RememberSlice(context.NodeId, result);
        Slice = result;
        Text = result.Text;
        Count = result.Messages.Count;
    }
}

/// <summary>Reads the chat's latest active record carrying a given tag.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Record", Description = "The chat's record of a tag: its text, whether found, and whether stale.", Icon = AppIcons.Tags, Color = ScenarioCatalog.InputColor, AlwaysRuns = true)]
internal sealed class RecordNode : IGraphNode
{
    public const string NodeKey = "chat.record";

    [GraphInput(Title = "Tag", MaxLength = 80)]
    public string Tag { get; set; } = string.Empty;

    [GraphInput(Title = "Fallback", MaxLength = 120, Description = "Handed out as Text when no record of this tag exists yet.")]
    public string Fallback { get; set; } = string.Empty;

    [GraphOutput(Title = "Text")]
    public string Text { get; set; } = string.Empty;

    [GraphOutput(Title = "Found")]
    public bool Found { get; set; }

    [GraphOutput(Title = "Stale")]
    public bool Stale { get; set; }

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);
        var beforeAnchorId = scope.ResolveOverwrittenAnchor(Tag);
        ChatRecord? record = ChatRecords.FindByTag(scope.Chat.Messages, scope.Chat.Records, Tag, beforeAnchorId);
        Found = record is not null;
        Stale = record?.Stale ?? false;
        Text = record?.Text ?? Fallback;
    }
}

/// <summary>Reads the composer's current draft text, falling back to preview text during a canvas run with an empty composer.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Draft", Description = "The composer's current draft text.", Icon = AppIcons.AuthorNotes, Color = ScenarioCatalog.InputColor, AlwaysRuns = true)]
internal sealed class ReadDraftNode : IGraphNode
{
    public const string NodeKey = "chat.draft-input";

    [GraphInput(Title = "Preview text", MaxLines = 3, Description = "Used during a canvas preview when the composer draft is empty.")]
    public string PreviewText { get; set; } = string.Empty;

    [GraphOutput(Title = "Text")]
    public string Text { get; set; } = string.Empty;

    [GraphOutput(Title = "Has text")]
    public bool HasText { get; set; }

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);
        var text = !scope.CommitToChat && scope.InitialDraft.Length == 0 ? PreviewText : scope.InitialDraft;
        Text = text;
        HasText = !string.IsNullOrWhiteSpace(text);
    }
}

/// <summary>Lists gallery image keys available to the chat and resolves a key to its served image address.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Gallery", Description = "Lists available gallery keys and resolves a key to an image.", Icon = AppIcons.Gallery, Color = ScenarioCatalog.InputColor, AlwaysRuns = true)]
internal sealed class GalleryNode : IGraphNode
{
    public const string NodeKey = "chat.gallery";

    [GraphInput(Title = "Key", MaxLength = 120, Placeholder = "happy or global.sunset", Description = "Gallery key to resolve; bare slugs check the character's gallery before global.")]
    public string Key { get; set; } = string.Empty;

    [GraphInput(Title = "Character gallery", Description = "Includes the open chat's character gallery in the available keys.")]
    public bool IncludeCharacter { get; set; } = true;

    [GraphInput(Title = "Global gallery", Description = "Includes the global gallery in the available keys.")]
    public bool IncludeGlobal { get; set; } = true;

    [GraphOutput(Title = "Image", Image = true)]
    public string Image { get; set; } = string.Empty;

    [GraphOutput(Title = "Resolved key")]
    public string ResolvedKey { get; set; } = string.Empty;

    [GraphOutput(Title = "Found")]
    public bool Found { get; set; }

    [GraphOutput(Title = "Keys")]
    public string[] Keys { get; set; } = [];

    [GraphOutput(Title = "Available", Description = "Available keys formatted one per line for prompt instructions.")]
    public string Available { get; set; } = string.Empty;

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);
        var charId = scope.Chat.Character;
        List<string> keys = [];

        if (IncludeCharacter && charId is not null)
        {
            IReadOnlyList<GalleryImage> charImages = GalleryPictures.ListCharacter(scope.Data, charId, scope.Character?.ImageTags);

            for (var i = 0; i < charImages.Count; i++)
                keys.Add(charImages[i].CopyKey);
        }

        if (IncludeGlobal)
        {
            IReadOnlyList<GalleryImage> globalImages = GalleryPictures.ListGlobal(scope.Data, scope.Settings.ImageTags);

            for (var i = 0; i < globalImages.Count; i++)
                keys.Add(globalImages[i].CopyKey);
        }

        Keys = [.. keys];
        Available = string.Join('\n', keys);

        if (string.IsNullOrWhiteSpace(Key))
        {
            ClearResolved();
            return;
        }

        (string Key, string Path)? resolved = GalleryPictures.Resolve(
            scope.Data,
            Key,
            IncludeCharacter ? charId : null,
            IncludeCharacter ? scope.Character?.ImageTags : null,
            IncludeGlobal ? scope.Settings.ImageTags : null
        );

        if (resolved is not (var resolvedKey, _) || !IsScopeEnabled(resolvedKey))
        {
            ClearResolved();
            return;
        }

        PictureContent? pictures = context.Services?.GetService(typeof(PictureContent)) as PictureContent;
        Image = pictures?.VisualAddressOf(resolvedKey, charId, scope.Character?.ImageTags, scope.Settings.ImageTags) ?? resolvedKey;
        ResolvedKey = resolvedKey;
        Found = true;
    }

    private void ClearResolved()
    {
        Image = string.Empty;
        ResolvedKey = string.Empty;
        Found = false;
    }

    private bool IsScopeEnabled(string resolvedKey)
    {
        if (resolvedKey.StartsWith(GalleryPictures.ChatPrefix, StringComparison.Ordinal))
            return true;

        return resolvedKey.StartsWith(GalleryPictures.GlobalScope + ".", StringComparison.Ordinal)
            ? IncludeGlobal
            : IncludeCharacter;
    }
}

/// <summary>Measures the chat's assembled prompt fill against the active context length and tests threshold crossing.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Context", Description = "Measures prompt token fill against the context length and detects threshold crossings.", Icon = AppIcons.Context, Color = ScenarioCatalog.InputColor, AlwaysRuns = true)]
internal sealed class ContextNode : IGraphNodeAsync
{
    public const string NodeKey = "chat.context";

    [GraphInput(Title = "Threshold", Min = 5, Max = 95, Step = 5, Unit = "%", Description = "Context fill percentage tested by Reached and Crossed.")]
    public int Threshold { get; set; } = 50;

    [GraphOutput(Title = "Crossed", Description = "True when this turn moved context fill from below Threshold to at or above it.")]
    public bool Crossed { get; set; }

    [GraphOutput(Title = "Reached", Description = "True whenever current context fill is at or above Threshold.")]
    public bool Reached { get; set; }

    [GraphOutput(Title = "Percent", Description = "Current prompt context fill from 0 to 100%.")]
    public double Percent { get; set; }

    [GraphOutput(Title = "Tokens", Description = "Current assembled chat prompt tokens.")]
    public int Tokens { get; set; }

    [GraphOutput(Title = "Context", Description = "Resolved maximum context length in tokens.")]
    public int ContextLength { get; set; }

    public async ValueTask ExecuteAsync(UINodeRunContext context, CancellationToken cancellationToken = default)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);
        (var currentTokens, var previousTokens, var contextLength) = await scope.MeasureContextAsync(cancellationToken).ConfigureAwait(false);
        var threshold = Math.Clamp(Threshold, 5, 95);
        var currentPercent = contextLength > 0 ? currentTokens * 100.0 / contextLength : 0.0;
        var previousPercent = contextLength > 0 ? previousTokens * 100.0 / contextLength : 0.0;

        Tokens = currentTokens;
        ContextLength = contextLength;
        Percent = Math.Round(currentPercent, 1);
        Reached = contextLength > 0 && currentPercent >= threshold;
        Crossed = contextLength > 0 && previousPercent < threshold && currentPercent >= threshold;
    }
}

/// <summary>Matches comma-separated World Info triggers (<c>word</c>, <c>stem*</c>, <c>-exclusion</c>) against wired text or recent chat turns.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Trigger match", Description = "Tests World Info trigger words, stems (*) and exclusions (-) on text or recent turns.", Icon = AppIcons.TriggerMatch, Color = ScenarioCatalog.InputColor, AlwaysRuns = true)]
internal sealed class TriggerMatchNode : IGraphNode
{
    public const string NodeKey = "chat.trigger-match";

    [GraphInput(Title = "Triggers", MaxLines = 2, Placeholder = "rain*, tavern, -combat", Description = "Comma-separated World Info triggers: whole word, prefix*, or -exclusion.")]
    public string Triggers { get; set; } = string.Empty;

    [GraphInput(Title = "Text", PinOnly = true, Description = "Optional text to test; when unwired, scans the latest chat messages.")]
    public string Text { get; set; } = string.Empty;

    [GraphInput(Title = "Scan depth", Min = 1, Max = 32, Step = 1, Description = "Recent messages scanned when Text is unwired.")]
    public int ScanDepth { get; set; } = 2;

    [GraphOutput(Title = "Matched")]
    public bool Matched { get; set; }

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);

        if (string.IsNullOrWhiteSpace(Triggers))
        {
            Matched = false;
            return;
        }

        if (scope.HasIncomingEdge(context.NodeId, nameof(Text)))
        {
            Matched = WorldInfo.Matches([Triggers], [Text]);
            return;
        }

        IReadOnlyList<PromptMessage> projected = ChatRecords.ProjectHistory(scope.Chat.Messages, scope.Chat.Records);
        var depth = Math.Max(1, ScanDepth);
        var start = Math.Max(0, projected.Count - depth);
        List<string> texts = [with(projected.Count - start + 1)];

        for (var i = start; i < projected.Count; i++)
            texts.Add(projected[i].Text);

        if (scope.PendingOutgoingDraft is string pendingDraft)
            texts.Add(pendingDraft);

        Matched = WorldInfo.Matches([Triggers], texts);
    }
}

/// <summary>Defines one setting-agnostic narrative cue with its short classifier tag and situation description.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Cue", Description = "One narrative director cue: its tag, when it fits, and the prompt direction.", Icon = AppIcons.Cue, Color = ScenarioCatalog.CueColor)]
internal sealed class CueNode : IGraphNode
{
    public const string NodeKey = "chat.cue";

    [GraphInput(Title = "Tag", MaxLength = 64, Placeholder = "ambient-shift", Description = "Short identifier the classifier replies with to choose this cue.")]
    public string Tag { get; set; } = string.Empty;

    [GraphInput(Title = "When", MaxLines = 2, Placeholder = "the surroundings have faded into the background", Description = "Situation check shown in the classifier menu.")]
    public string When { get; set; } = string.Empty;

    [GraphInput(Title = "Cue", MaxLines = 4, Placeholder = "Let a concrete detail of the environment shift...", Description = "Direction injected into the prompt when this cue is picked.")]
    public string Cue { get; set; } = string.Empty;

    [GraphOutput(Title = "Cue")]
    public ScenarioCue? Result { get; set; }

    public void Execute(UINodeRunContext context)
    {
        var tag = Tag.Trim();
        var cue = Cue.Trim();

        Result = tag.Length == 0 || cue.Length == 0 ? null : new ScenarioCue(tag, When.Trim(), cue);
    }
}

/// <summary>Gathers connected <see cref="CueNode"/>s, optionally samples a subset while avoiding the previous cue, and formats a classifier menu.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Cue menu", Description = "Gathers Cue nodes, optionally samples a subset, and formats options for a classifier.", Icon = AppIcons.CueMenu, Color = ScenarioCatalog.CueColor, AlwaysRuns = true)]
internal sealed class CueMenuNode : IGraphNode
{
    public const string NodeKey = "chat.cue-menu";

    [GraphInput(Title = "Cues", Multiple = true, PinOnly = true, Description = "Connect any number of Cue nodes here.")]
    public ScenarioCue[] Cues { get; set; } = [];

    [GraphInput(Title = "Sample", Min = 0, Max = 32, Step = 1, Description = "Randomly offers up to this many cues per run; 0 offers every connected cue.")]
    public int Sample { get; set; }

    [GraphInput(Title = "Allow none", Description = "Appends '- none' so the model can pass when no cue fits.")]
    public bool AllowNone { get; set; } = true;

    [GraphInput(Title = "Avoid repeat", Description = "Omits the cue chosen on this scenario's previous run when other cues exist.")]
    public bool AvoidRepeat { get; set; } = true;

    [GraphOutput(Title = "Pool")]
    public ScenarioCuePool Pool { get; set; } = ScenarioCuePool.Empty;

    [GraphOutput(Title = "Options", Description = "Formatted '- tag: when' lines for the classifier prompt.")]
    public string Options { get; set; } = string.Empty;

    [GraphOutput(Title = "Count")]
    public int Count { get; set; }

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);
        List<ScenarioCue> candidates = [];
        HashSet<string> seenTags = [with(StringComparer.OrdinalIgnoreCase)];

        for (var i = 0; i < Cues.Length; i++)
        {
            if (Cues[i] is not ScenarioCue cue || string.IsNullOrWhiteSpace(cue.Tag) || string.IsNullOrWhiteSpace(cue.Cue))
                continue;

            if (seenTags.Add(cue.Tag))
                candidates.Add(cue);
        }

        if (AvoidRepeat && candidates.Count > 1 && ChatRecords.LatestScenarioRecord(scope.Chat.Messages, scope.Chat.Records, scope.ScenarioId)?.Text is string lastText)
        {
            for (var i = candidates.Count - 1; i >= 0; i--)
            {
                if (candidates.Count > 1 && string.Equals(candidates[i].Cue, lastText, StringComparison.Ordinal))
                    candidates.RemoveAt(i);
            }
        }

        List<ScenarioCue> chosen = SelectSubset(scope, candidates, Math.Max(0, Sample));
        StringBuilder menu = new();

        for (var i = 0; i < chosen.Count; i++)
        {
            ScenarioCue item = chosen[i];

            if (menu.Length > 0)
                _ = menu.Append('\n');

            _ = string.IsNullOrWhiteSpace(item.When)
                ? menu.Append("- ").Append(item.Tag)
                : menu.Append("- ").Append(item.Tag).Append(": ").Append(item.When);
        }

        if (AllowNone)
        {
            if (menu.Length > 0)
                _ = menu.Append('\n');

            _ = menu.Append("- none: none of the above fit right now; let the scene unfold naturally");
        }

        Pool = new ScenarioCuePool(chosen);
        Options = menu.ToString();
        Count = chosen.Count;
    }

    private static List<ScenarioCue> SelectSubset(ScenarioRunScope scope, List<ScenarioCue> candidates, int sample)
    {
        if (sample <= 0 || sample >= candidates.Count)
            return candidates;

        var indices = new int[candidates.Count];

        for (var i = 0; i < indices.Length; i++)
            indices[i] = i;

        for (var i = 0; i < sample; i++)
        {
            var pick = i + scope.DrawRandomInt(indices.Length - i);
            (indices[i], indices[pick]) = (indices[pick], indices[i]);
        }

        Array.Sort(indices, 0, sample);
        List<ScenarioCue> picked = [with(sample)];

        for (var i = 0; i < sample; i++)
            picked.Add(candidates[indices[i]]);

        return picked;
    }
}

/// <summary>Resolves the classifier model's reply against a <see cref="ScenarioCuePool"/> and hands out the chosen cue text.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Cue pick", Description = "Extracts the chosen tag from a model reply and resolves its Cue text.", Icon = AppIcons.CuePick, Color = ScenarioCatalog.CueColor)]
internal sealed class CuePickNode : IGraphNode
{
    public const string NodeKey = "chat.cue-pick";

    private static readonly char[] TrimChars = [' ', '\t', '\r', '\n', '"', '\'', '`', '*', '_', '[', ']', '(', ')', '{', '}', '.', ',', ';', ':', '-', '!', '?'];

    [GraphInput(Title = "Pool", PinOnly = true, Required = true)]
    public ScenarioCuePool? Pool { get; set; }

    [GraphInput(Title = "Choice", PinOnly = true, Description = "Classifier output naming a cue tag or 'none'.")]
    public string Choice { get; set; } = string.Empty;

    [GraphOutput(Title = "Cue")]
    public string Cue { get; set; } = string.Empty;

    [GraphOutput(Title = "Tag")]
    public string Tag { get; set; } = string.Empty;

    [GraphOutput(Title = "Matched")]
    public bool Matched { get; set; }

    public void Execute(UINodeRunContext context)
    {
        if (Pool is null || Pool.Cues.Count == 0 || string.IsNullOrWhiteSpace(Choice))
        {
            Clear(context, "none");
            return;
        }

        ScenarioCue? matched = MatchChoice(Pool.Cues, Choice);

        if (matched is null)
        {
            Clear(context, Choice.Trim());
            return;
        }

        Cue = matched.Cue;
        Tag = matched.Tag;
        Matched = true;
        context.Log($"Picked cue '{matched.Tag}'.");
    }

    private void Clear(UINodeRunContext context, string raw)
    {
        Cue = string.Empty;
        Tag = string.Empty;
        Matched = false;
        context.Log(raw.Length == 0 || string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase) ? "No cue picked ('none')." : $"No cue matched '{raw}'.");
    }

    private static ScenarioCue? MatchChoice(IReadOnlyList<ScenarioCue> cues, string rawChoice)
    {
        var firstLine = FirstNonEmptyLine(rawChoice);
        var cleaned = StripLabelPrefix(firstLine).Trim(TrimChars);

        if (cleaned.Length == 0 || string.Equals(cleaned, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        for (var i = 0; i < cues.Count; i++)
        {
            if (string.Equals(cues[i].Tag, cleaned, StringComparison.OrdinalIgnoreCase))
                return cues[i];
        }

        if (ContainsBoundedToken(firstLine, "none"))
            return null;

        ScenarioCue? best = null;

        for (var i = 0; i < cues.Count; i++)
        {
            ScenarioCue candidate = cues[i];

            if (ContainsBoundedToken(rawChoice, candidate.Tag) && (best is null || candidate.Tag.Length > best.Tag.Length))
                best = candidate;
        }

        return best;
    }

    private static string FirstNonEmptyLine(string text)
    {
        foreach (ReadOnlySpan<char> line in text.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> trimmed = line.Trim();

            if (!trimmed.IsEmpty)
                return trimmed.ToString();
        }

        return string.Empty;
    }

    private static string StripLabelPrefix(string line)
    {
        foreach (var prefix in (string[])["tag:", "cue:", "choice:", "pick:"])
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line[prefix.Length..].TrimStart();
        }

        return line;
    }

    private static bool ContainsBoundedToken(ReadOnlySpan<char> text, ReadOnlySpan<char> token)
    {
        if (token.IsEmpty || text.Length < token.Length)
            return false;

        var from = 0;

        while (from <= text.Length - token.Length)
        {
            var offset = text[from..].IndexOf(token, StringComparison.OrdinalIgnoreCase);

            if (offset < 0)
                return false;

            var pos = from + offset;
            from = pos + 1;

            var leftOk = pos == 0 || !IsTagChar(text[pos - 1]);
            var rightIndex = pos + token.Length;
            var rightOk = rightIndex >= text.Length || !IsTagChar(text[rightIndex]);

            if (leftOk && rightOk)
                return true;
        }

        return false;
    }

    private static bool IsTagChar(char ch)
        => char.IsLetterOrDigit(ch) || ch is '-' or '_';
}

/// <summary>Assembles prompt blocks from whatever chat inputs and trailing instruction are wired into it.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Collector", Description = "Assembles prompt blocks from optional chat parts and a trailing instruction.", Icon = UIGlyphs.Merge, Color = ScenarioCatalog.PromptColor)]
internal sealed class CollectorNode : IGraphNode
{
    public const string NodeKey = "chat.collector";

    [GraphInput(Title = "Prompt", PinOnly = true)]
    public string Prompt { get; set; } = string.Empty;

    [GraphInput(Title = "Persona", PinOnly = true)]
    public PersonaCard? Persona { get; set; }

    [GraphInput(Title = "Character", PinOnly = true)]
    public CharacterCard? Character { get; set; }

    [GraphInput(Title = "World", PinOnly = true)]
    public WorldLore? World { get; set; }

    [GraphInput(Title = "Messages", PinOnly = true)]
    public MessageSlice? Messages { get; set; }

    [GraphInput(Title = "Instruction", MaxLines = 4)]
    public string Instruction { get; set; } = string.Empty;

    [GraphOutput(Title = "Prompt")]
    public CollectedPrompt Blocks { get; set; } = new(new PromptSpec());

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);
        Character? cleanCharacter = Character is null
            ? null
            : Character.Character with
            {
                SystemPrompt = string.IsNullOrWhiteSpace(Prompt) ? null : Character.Character.SystemPrompt,
                PostHistoryInstructions = null,
                AuthorsNote = null,
                ExampleMessages = string.Empty
            };

        PromptTurn? instructionTurn = string.IsNullOrWhiteSpace(Instruction)
            ? null
            : new PromptTurn(PromptRole.User, PromptBlocks.Instruction, Instruction.Trim());

        Blocks = new CollectedPrompt(new PromptSpec
        {
            SystemPrompt = Prompt,
            Character = cleanCharacter,
            CharacterName = Character?.Name ?? scope.CharacterName,
            Persona = Persona?.Persona,
            UserName = Persona?.Name ?? scope.UserName,
            Lore = World?.Articles ?? [],
            History = Messages?.Messages ?? [],
            Instruction = instructionTurn,
            ReplySpeaker = string.Empty
        });
    }
}

/// <summary>Sends assembled prompt blocks to the active or a named connection profile, with optional sampler overrides.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Model call", Description = "Runs a generation on the active or a named connection profile.", Icon = AppIcons.Generation, Color = ScenarioCatalog.PromptColor, ShowProgress = true, AlwaysRuns = true)]
internal sealed class ModelCallNode : IGraphNodeAsync
{
    public const string NodeKey = "chat.model-call";
    public const string ConnectionChoicesKey = "chat.connections";
    public const string SamplerChoicesKey = "chat.samplers";

    [GraphInput(Title = "Prompt", PinOnly = true, Required = true)]
    public CollectedPrompt? Prompt { get; set; }

    [GraphInput(Title = "When", PinOnly = true, Description = "Runs this call only when true; unwired, always runs.")]
    public bool When { get; set; } = true;

    [GraphInput(Title = "Connection", ChoicesKey = ConnectionChoicesKey, Placeholder = "Active", Description = "Connection profile to run on, or the active connection.")]
    public string Connection { get; set; } = string.Empty;

    [GraphInput(Title = "Samplers", ChoicesKey = SamplerChoicesKey, Placeholder = "From connection", Description = "Sampler preset to run with, or the connection's own preset.")]
    public string Samplers { get; set; } = string.Empty;

    [GraphInput(Title = "Thinking")]
    public ScenarioThinking Thinking { get; set; } = ScenarioThinking.FromProfile;

    [GraphInput(Title = "Override reply length", NoPin = true)]
    public bool OverrideReplyLength { get; set; }

    [GraphInput(Title = "Reply length", VisibleWhen = nameof(OverrideReplyLength), Min = 16, Max = 8192, Step = 16)]
    public int ReplyLength { get; set; } = 256;

    [GraphInput(Title = "Override temperature", NoPin = true)]
    public bool OverrideTemperature { get; set; }

    [GraphInput(Title = "Temperature", VisibleWhen = nameof(OverrideTemperature), Min = 0, Max = 2, Step = 0.05)]
    public double Temperature { get; set; } = 0.8;

    [GraphInput(Title = "Override Min P", NoPin = true)]
    public bool OverrideMinP { get; set; }

    [GraphInput(Title = "Min P", VisibleWhen = nameof(OverrideMinP), Min = 0, Max = 1, Step = 0.01)]
    public double MinP { get; set; } = 0.05;

    [GraphInput(Title = "Override Top P", NoPin = true)]
    public bool OverrideTopP { get; set; }

    [GraphInput(Title = "Top P", VisibleWhen = nameof(OverrideTopP), Min = 0, Max = 1, Step = 0.01)]
    public double TopP { get; set; } = 0.95;

    [GraphInput(Title = "Override Top K", NoPin = true)]
    public bool OverrideTopK { get; set; }

    [GraphInput(Title = "Top K", VisibleWhen = nameof(OverrideTopK), Min = 0, Max = 200, Step = 1)]
    public int TopK { get; set; }

    [GraphInput(Title = "Override repetition penalty", NoPin = true)]
    public bool OverrideRepetitionPenalty { get; set; }

    [GraphInput(Title = "Repetition penalty", VisibleWhen = nameof(OverrideRepetitionPenalty), Min = 1, Max = 2, Step = 0.01)]
    public double RepetitionPenalty { get; set; } = 1.05;

    [GraphInput(Title = "Override presence penalty", NoPin = true)]
    public bool OverridePresencePenalty { get; set; }

    [GraphInput(Title = "Presence penalty", VisibleWhen = nameof(OverridePresencePenalty), Min = -2, Max = 2, Step = 0.05)]
    public double PresencePenalty { get; set; }

    [GraphInput(Title = "Override frequency penalty", NoPin = true)]
    public bool OverrideFrequencyPenalty { get; set; }

    [GraphInput(Title = "Frequency penalty", VisibleWhen = nameof(OverrideFrequencyPenalty), Min = -2, Max = 2, Step = 0.05)]
    public double FrequencyPenalty { get; set; }

    [GraphInput(Title = "Override seed", NoPin = true)]
    public bool OverrideSeed { get; set; }

    [GraphInput(Title = "Seed", VisibleWhen = nameof(OverrideSeed), Min = 0, Step = 1)]
    public long Seed { get; set; }

    [GraphOutput(Title = "Text")]
    public string Text { get; set; } = string.Empty;

    [GraphOutput(Title = "Thought")]
    public string Thought { get; set; } = string.Empty;

    public async ValueTask ExecuteAsync(UINodeRunContext context, CancellationToken cancellationToken = default)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);

        if (!scope.ShouldRunAction(context, When))
        {
            Text = string.Empty;
            Thought = string.Empty;
            return;
        }

        if (Prompt is null)
            throw new UINodeFailureException("No prompt is wired into Model call.");

        ActiveProfile profile = await ResolveProfileAsync(scope, cancellationToken).ConfigureAwait(false);

        if (profile.Connection.Address is not Uri address)
            throw new UINodeFailureException($"The connection '{profile.Connection.Name}' has no server address.");

        SamplerPreset samplers = ApplyOverrides(await ResolveSamplersAsync(scope, profile.Samplers, cancellationToken).ConfigureAwait(false));
        ModelFormat format = ApplyThinking(profile.Format);
        DraftWriteMode? draftMode = scope.ResolveDraftWriteMode(context.NodeId);
        var feedsDraft = draftMode is not null;
        var impersonating = draftMode == DraftWriteMode.Continue;
        var continuingDraft = impersonating && scope.InitialDraft.Length > 0;
        var hasTopContent = Prompt.Spec.SystemPrompt.Length > 0 || Prompt.Spec.Character is not null || Prompt.Spec.Persona is not null || Prompt.Spec.Lore.Count > 0;
        PromptPreset promptPreset = hasTopContent ? profile.Prompt : profile.Prompt with { ChatStart = string.Empty };
        PromptTurn? instruction = Prompt.Spec.Instruction;

        if (impersonating && instruction is not null && instruction.Block == PromptBlocks.Instruction)
            instruction = instruction with { Block = PromptBlocks.Impersonate };

        PromptSpec spec = Prompt.Spec with
        {
            Prompt = promptPreset,
            Instruction = instruction,
            ReplySpeaker = impersonating && string.IsNullOrEmpty(Prompt.Spec.ReplySpeaker) ? scope.UserName : Prompt.Spec.ReplySpeaker,
            Continuation = continuingDraft ? scope.InitialDraft : Prompt.Spec.Continuation
        };

        KoboldCppClient kobold = scope.Clients.For(address);
        using IDisposable serverSlot = await scope.Clients.AcquireSlotAsync(address, cancellationToken).ConfigureAwait(false);
        ResolvedContext resolvedContext = await ContextLengths.ResolveAsync(profile.Connection, kobold, cancellationToken).ConfigureAwait(false);

        if (resolvedContext.ServerAway)
            throw new UINodeFailureException($"The model server at '{address}' did not answer.");

        var model = await KoboldCppClients.TryGetModelNameAsync(kobold, cancellationToken).ConfigureAwait(false);
        BuiltPrompt built = await scope.Clients.BuildPromptWithFallbackAsync(spec, format, resolvedContext.Length, samplers.ReplyLength, address, kobold, model, cancellationToken).ConfigureAwait(false);
        SeedChoice seed = Seeds.Pick(samplers.Seed, scope.Chat.Seed, regenerate: false);
        var genKey = $"ne_{Guid.NewGuid():N}";
        KoboldGenerateRequest request = KoboldGenerateRequest.From(built.Text, samplers, resolvedContext.Length, seed.Seed, format.AllStopSequences(), genKey);
        ReplyReader reader = new(continuingDraft ? new ThoughtFormat() : format.Thought, impersonating && !continuingDraft ? scope.UserName : null);
        StringBuilder sent = new();
        Stopwatch clock = new();
        double? promptSeconds = null;
        var anchorId = scope.ResolveStreamingAnchor(context.NodeId);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        await context.ReportAsync("Generating…", 0.1).ConfigureAwait(false);
        using IDisposable? active = scope.ActiveGenerations?.Track(kobold, genKey);
        clock.Start();

        try
        {
            await foreach (var piece in kobold.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (piece.Length > 0)
                    promptSeconds ??= clock.Elapsed.TotalSeconds;

                _ = sent.Append(piece);

                if (!reader.Append(piece))
                    continue;

                if (anchorId is not null && scope.OnStreamChunkAsync is not null)
                    await scope.OnStreamChunkAsync(context.NodeId, anchorId, reader.Text, reader.Thought).ConfigureAwait(false);

                if (feedsDraft && scope.OnDraftChunkAsync is not null && reader.Text.Length > 0)
                {
                    var partialDraft = continuingDraft
                        ? scope.InitialDraft + reader.Text
                        : reader.Text;
                    await scope.OnDraftChunkAsync(partialDraft).ConfigureAwait(false);
                }
            }
        }
        catch (HttpRequestException exception)
        {
            throw new UINodeFailureException($"The model server failed: {exception.Message}");
        }
        finally
        {
            clock.Stop();
            active?.Dispose();
        }

        _ = reader.Complete();
        (var replyTokens, TokenCounts replyCounts) = await KoboldCppClients.CountReplyAsync(kobold, sent.ToString(), built).ConfigureAwait(false);
        bool? thinkingFlag = format.Thought.IsDefined ? !continuingDraft && format.Thought.Enabled : null;
        GenerationRecord record = new()
        {
            Id = ChatLibrary.NewMessageId(),
            Purpose = impersonating ? GenerationPurpose.Impersonate : scope.ScenarioName,
            AnchorMessage = scope.Chat.Messages.Count > 0 ? scope.Chat.Messages[^1].Id : null,
            Connection = profile.ConnectionId,
            Model = model,
            ContextLength = resolvedContext.Length,
            Samplers = GenerationSamplers.From(samplers, seed.Seed, thinkingFlag),
            PromptTokens = built.Blocks,
            ReplyTokens = replyTokens,
            Counts = replyCounts == TokenCounts.Exact ? built.Counts : TokenCounts.Approximate,
            Started = started,
            PromptSeconds = promptSeconds,
            Seconds = clock.Elapsed.TotalSeconds
        };

        scope.RecordGeneration(context.NodeId, record, reader.Thought.Length == 0 ? null : reader.Thought);
        Text = (continuingDraft ? scope.InitialDraft + reader.Text : reader.Text).Trim();
        Thought = reader.Thought.Trim();

        await context.LogAsync(string.Create(CultureInfo.InvariantCulture, $"{built.Tokens} prompt + {replyTokens} reply tokens in {clock.Elapsed.TotalSeconds:F1} s.")).ConfigureAwait(false);
    }

    private Task<ActiveProfile> ResolveProfileAsync(ScenarioRunScope scope, CancellationToken cancellationToken)
    {
        var requested = Connection.Trim();

        if (requested.Length == 0)
            return Task.FromResult(scope.ActiveProfile ?? throw new UINodeFailureException("No active connection is configured."));

        if (scope.PreloadedProfiles is IReadOnlyList<(string Id, ActiveProfile Profile)> preloaded)
        {
            for (var i = 0; i < preloaded.Count; i++)
            {
                if (string.Equals(preloaded[i].Id, requested, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(preloaded[i].Profile.Connection.Name, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(preloaded[i].Profile);
                }
            }

            throw new UINodeFailureException($"The connection profile '{requested}' was not found or has been deleted.");
        }

        return ResolveProfileFromDiskAsync(scope, requested, cancellationToken);
    }

    private static async Task<ActiveProfile> ResolveProfileFromDiskAsync(ScenarioRunScope scope, string requested, CancellationToken cancellationToken)
    {
        StoredDocument<ConnectionProfile> match = await FindDocumentAsync(scope.Data.Connections, requested, cancellationToken).ConfigureAwait(false)
            ?? throw new UINodeFailureException($"The connection profile '{requested}' was not found or has been deleted.");

        return await Profiles.ResolveAsync(scope.Data, match.Id, match.Document, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredDocument<T>?> FindDocumentAsync<T>(DocumentStore<T> store, string idOrName, CancellationToken cancellationToken)
        where T : NamedDocument
    {
        if (Slug.IsValidId(idOrName) && await store.ReadAsync(idOrName, cancellationToken).ConfigureAwait(false) is T direct)
            return new StoredDocument<T>(idOrName, direct);

        DocumentListing<T> listing = await store.ListAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < listing.Items.Count; i++)
        {
            StoredDocument<T> stored = listing.Items[i];

            if (string.Equals(stored.Id, idOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stored.Document.Name, idOrName, StringComparison.OrdinalIgnoreCase))
            {
                return stored;
            }
        }

        return null;
    }

    private Task<SamplerPreset> ResolveSamplersAsync(ScenarioRunScope scope, SamplerPreset fallback, CancellationToken cancellationToken)
    {
        var requested = Samplers.Trim();

        if (requested.Length == 0)
            return Task.FromResult(fallback);

        if (scope.PreloadedSamplers is IReadOnlyList<StoredDocument<SamplerPreset>> preloaded)
        {
            for (var i = 0; i < preloaded.Count; i++)
            {
                if (string.Equals(preloaded[i].Id, requested, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(preloaded[i].Document.Name, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(preloaded[i].Document);
                }
            }

            throw new UINodeFailureException($"The sampler preset '{requested}' was not found or has been deleted.");
        }

        return ResolveSamplersFromDiskAsync(scope, requested, cancellationToken);
    }

    private static async Task<SamplerPreset> ResolveSamplersFromDiskAsync(ScenarioRunScope scope, string requested, CancellationToken cancellationToken)
    {
        StoredDocument<SamplerPreset> match = await FindDocumentAsync(scope.Data.Samplers, requested, cancellationToken).ConfigureAwait(false)
            ?? throw new UINodeFailureException($"The sampler preset '{requested}' was not found or has been deleted.");

        return match.Document;
    }

    private SamplerPreset ApplyOverrides(SamplerPreset preset)
        => preset with
        {
            ReplyLength = OverrideReplyLength ? ReplyLength : preset.ReplyLength,
            Temperature = OverrideTemperature ? Temperature : preset.Temperature,
            MinP = OverrideMinP ? MinP : preset.MinP,
            TopP = OverrideTopP ? TopP : preset.TopP,
            TopK = OverrideTopK ? TopK : preset.TopK,
            RepetitionPenalty = OverrideRepetitionPenalty ? RepetitionPenalty : preset.RepetitionPenalty,
            PresencePenalty = OverridePresencePenalty ? PresencePenalty : preset.PresencePenalty,
            FrequencyPenalty = OverrideFrequencyPenalty ? FrequencyPenalty : preset.FrequencyPenalty,
            Seed = OverrideSeed ? new SeedRule { Mode = SeedMode.Fixed, Value = Seed } : preset.Seed
        };

    private ModelFormat ApplyThinking(ModelFormat format)
        => !format.Thought.IsDefined || Thinking == ScenarioThinking.FromProfile
            ? format
            : format with { Thought = format.Thought with { Enabled = Thinking == ScenarioThinking.On } };
}

/// <summary>Writes a record in place of a message slice: the messages stay in the feed and leave the prompt.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Replace", Description = "Writes a record sent in place of a message slice.", Icon = AppIcons.Regenerate, Color = ScenarioCatalog.WriteColor, AlwaysRuns = true)]
internal sealed class ReplaceRecordNode : IGraphNode
{
    public const string NodeKey = "chat.replace";

    [GraphInput(Title = "Slice", PinOnly = true, Required = true)]
    public MessageSlice? Slice { get; set; }

    [GraphInput(Title = "Text", PinOnly = true)]
    public string Text { get; set; } = string.Empty;

    [GraphInput(Title = "When", PinOnly = true, Description = "Writes this record only when true; unwired, always runs.")]
    public bool When { get; set; } = true;

    [GraphInput(Title = "Role")]
    public MessageRole Role { get; set; } = MessageRole.System;

    [GraphInput(Title = "Tag", MaxLength = 80)]
    public string Tag { get; set; } = string.Empty;

    [GraphInput(Title = "Template", MaxLines = 3, Placeholder = "{0}", Description = "Wraps the record when sent in the prompt; {0} is the record text.")]
    public string Template { get; set; } = string.Empty;

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);

        if (!scope.ShouldRunAction(context, When))
            return;

        if (Slice?.FirstMessageId is not string firstId || Slice.LastMessageId is not string lastId)
            throw new UINodeFailureException("The message slice has no messages to replace.");

        var cleaned = Text.Trim();

        if (cleaned.Length == 0)
            throw new UINodeFailureException("The record text is empty.");

        if (!scope.CommitToChat)
        {
            context.Log($"Preview: would replace [{firstId}..{lastId}] ({cleaned.Length} chars).");
            return;
        }

        (var generationId, var thought) = scope.ResolveSourceGeneration(context.NodeId);
        scope.StageRecord(new ChatRecord
        {
            Id = ChatLibrary.NewMessageId(),
            Scenario = scope.ScenarioId,
            Tag = string.IsNullOrWhiteSpace(Tag) ? null : Tag.Trim(),
            Replaces = [firstId, lastId],
            Role = Role,
            Text = cleaned,
            Template = string.IsNullOrWhiteSpace(Template) ? null : Template.Trim(),
            Thought = thought,
            Generation = generationId
        });
    }
}

/// <summary>Writes a record immediately after the chat's last message, or after a wired slice's last message.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Insert", Description = "Writes a record after the chat's or a slice's last message.", Icon = UIGlyphs.Add, Color = ScenarioCatalog.WriteColor, AlwaysRuns = true)]
internal sealed class InsertRecordNode : IGraphNode
{
    public const string NodeKey = "chat.insert";

    [GraphInput(Title = "Text", PinOnly = true)]
    public string Text { get; set; } = string.Empty;

    [GraphInput(Title = "Slice", PinOnly = true)]
    public MessageSlice? Slice { get; set; }

    [GraphInput(Title = "When", PinOnly = true, Description = "Writes this record only when true; unwired, always runs.")]
    public bool When { get; set; } = true;

    [GraphInput(Title = "Role")]
    public MessageRole Role { get; set; } = MessageRole.System;

    [GraphInput(Title = "Tag", MaxLength = 80)]
    public string Tag { get; set; } = string.Empty;

    [GraphInput(Title = "Template", MaxLines = 3, Placeholder = "{0}", Description = "Wraps the record when sent in the prompt; {0} is the record text.")]
    public string Template { get; set; } = string.Empty;

    [GraphInput(Title = "One turn", Description = "Sends this record only while its anchor message is the very last message of the history.")]
    public bool OneTurn { get; set; }

    [GraphInput(Title = "Ignore empty", Description = "Skips silently instead of failing when Text is blank.")]
    public bool IgnoreEmpty { get; set; }

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);

        if (!scope.ShouldRunAction(context, When))
            return;

        var cleaned = Text.Trim();

        if (cleaned.Length == 0)
        {
            if (IgnoreEmpty)
            {
                context.Log("Skipped: record text is empty.");
                return;
            }

            throw new UINodeFailureException("The record text is empty.");
        }

        var afterId = Slice?.LastMessageId
            ?? (scope.Chat.Messages.Count > 0 ? scope.Chat.Messages[^1].Id : null)
            ?? throw new UINodeFailureException("The chat has no messages to insert a record after.");

        if (!scope.CommitToChat)
        {
            context.Log($"Preview: would insert after '{afterId}' ({cleaned.Length} chars{(OneTurn ? ", one turn" : string.Empty)}).");
            return;
        }

        (var generationId, var thought) = scope.ResolveSourceGeneration(context.NodeId);
        scope.StageRecord(new ChatRecord
        {
            Id = ChatLibrary.NewMessageId(),
            Scenario = scope.ScenarioId,
            Tag = string.IsNullOrWhiteSpace(Tag) ? null : Tag.Trim(),
            After = afterId,
            Role = Role,
            Text = cleaned,
            Template = string.IsNullOrWhiteSpace(Template) ? null : Template.Trim(),
            Thought = thought,
            Generation = generationId,
            OneTurn = OneTurn
        });
    }
}

/// <summary>Writes or continues text in the chat composer's draft field.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Write draft", Description = "Writes or continues text in the chat composer's draft field.", Icon = AppIcons.Impersonate, Color = ScenarioCatalog.WriteColor, AlwaysRuns = true)]
internal sealed class WriteDraftNode : IGraphNode
{
    public const string NodeKey = "chat.draft";

    [GraphInput(Title = "Text", PinOnly = true)]
    public string Text { get; set; } = string.Empty;

    [GraphInput(Title = "When", PinOnly = true, Description = "Writes the draft only when true; unwired, always runs.")]
    public bool When { get; set; } = true;

    [GraphInput(Title = "Mode")]
    public DraftWriteMode Mode { get; set; } = DraftWriteMode.Replace;

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);

        if (!scope.ShouldRunAction(context, When))
            return;

        (var generationId, _) = scope.ResolveSourceGeneration(context.NodeId);
        var combined = Mode == DraftWriteMode.Continue && generationId is null && scope.InitialDraft.Length > 0
            ? scope.InitialDraft + Text
            : Text;
        var cleaned = combined.Trim();

        if (cleaned.Length == 0)
            throw new UINodeFailureException("The draft text is empty.");

        if (!scope.CommitToChat)
        {
            context.Log($"Preview: would write draft ({cleaned.Length} chars, {Mode}).");
            return;
        }

        var continued = Mode == DraftWriteMode.Continue && scope.InitialDraft.Length > 0;
        scope.StageDraft(cleaned, generationId, continued);
    }
}

/// <summary>Sets or clears the chat's timeline-bound stage picture or background at the current turn.</summary>
[GraphNode(Key = NodeKey, Category = ScenarioCatalog.ChatCategory, Title = "Set visual", Description = "Sets or clears the chat's stage picture or background at the current turn.", Icon = AppIcons.Stage, Color = ScenarioCatalog.WriteColor, AlwaysRuns = true)]
internal sealed class SetVisualNode : IGraphNode
{
    public const string NodeKey = "chat.visual";

    [GraphInput(Title = "When", PinOnly = true, Description = "Updates the visual only when true; unwired, always runs.")]
    public bool When { get; set; } = true;

    [GraphInput(Title = "Target")]
    public ChatVisualTarget Target { get; set; } = ChatVisualTarget.Stage;

    [GraphInput(Title = "Image", Image = true, Large = true, Placeholder = "happy or global.sunset", Description = "An image output or gallery key (<char>.<id>, global.<id>, or <id>).")]
    public string Image { get; set; } = string.Empty;

    [GraphInput(Title = "Clear", Description = "Clears the active visual for this target from this turn onward.")]
    public bool Clear { get; set; }

    [GraphInput(Title = "Slice", PinOnly = true, Description = "Optional slice whose last message anchors the visual; defaults to the chat's last message.")]
    public MessageSlice? Slice { get; set; }

    [GraphInput(Title = "Ignore missing", Description = "Skips silently instead of failing when the image key is blank or not found.")]
    public bool IgnoreMissing { get; set; } = true;

    public void Execute(UINodeRunContext context)
    {
        ScenarioRunScope scope = ScenarioRunScope.From(context);

        if (!scope.ShouldRunAction(context, When))
            return;

        var afterId = Slice?.LastMessageId ?? (scope.Chat.Messages.Count > 0 ? scope.Chat.Messages[^1].Id : null);

        if (Clear)
        {
            if (!scope.CommitToChat)
            {
                context.Log($"Preview: would clear {Target}.");
                return;
            }

            (var clearGenId, _) = scope.ResolveSourceGeneration(context.NodeId);
            scope.StageVisual(new ChatVisual
            {
                Id = ChatLibrary.NewMessageId(),
                Scenario = scope.ScenarioId,
                Target = Target,
                After = afterId,
                Image = null,
                Generation = clearGenId
            });
            return;
        }

        PictureContent? pictures = context.Services?.GetService(typeof(PictureContent)) as PictureContent;
        (string Key, bool IsPreview)? resolved = pictures is not null
            ? pictures.TryResolveVisualKey(Image, scope.Chat.Character, scope.Character?.ImageTags, scope.Settings.ImageTags)
            : GalleryPictures.Resolve(scope.Data, Image, scope.Chat.Character, scope.Character?.ImageTags, scope.Settings.ImageTags) is (var fallbackKey, _) ? (fallbackKey, false) : null;

        if (resolved is not (var resolvedKey, var isPreview))
        {
            if (IgnoreMissing)
            {
                context.Log($"Skipped: visual '{Image.Trim()}' was not found.");
                return;
            }

            throw new UINodeFailureException(string.IsNullOrWhiteSpace(Image) ? $"No image was provided for {Target}." : $"The gallery image '{Image.Trim()}' was not found.");
        }

        if (!scope.CommitToChat || isPreview)
        {
            context.Log($"Preview: would set {Target} to '{resolvedKey}'.");
            return;
        }

        (var generationId, _) = scope.ResolveSourceGeneration(context.NodeId);
        scope.StageVisual(new ChatVisual
        {
            Id = ChatLibrary.NewMessageId(),
            Scenario = scope.ScenarioId,
            Target = Target,
            After = afterId,
            Image = resolvedKey,
            Generation = generationId
        });
    }
}
