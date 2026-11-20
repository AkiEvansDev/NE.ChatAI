using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Backends.KoboldCpp;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Plugins;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Secrets;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Scenarios;

/// <summary>Staged mutations produced by one scenario run, ready to apply onto a <see cref="Chat"/>.</summary>
internal sealed record ScenarioDelta(IReadOnlyList<ChatRecord> Records, IReadOnlyList<GenerationRecord> Generations, string? Draft = null, string? DraftGenerationId = null, bool DraftContinued = false, IReadOnlyList<ChatVisual>? Visuals = null)
{
    public static ScenarioDelta Empty { get; } = new([], []);

    public Chat ApplyTo(Chat chat)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var hasRecords = Records.Count > 0;
        var hasVisuals = Visuals is { Count: > 0 };
        var hasGenerations = Generations.Count > 0;

        if (!hasRecords && !hasVisuals && !hasGenerations)
            return chat;

        Chat updated = chat;

        if (hasRecords)
            updated = ChatRecords.PutMany(updated, Records, Generations);

        if (hasVisuals)
            updated = ChatVisuals.PutMany(updated, Visuals!, hasRecords ? [] : Generations);
        else if (!hasRecords && hasGenerations)
            updated = ChatRecords.PutMany(updated, [], Generations);

        return updated;
    }
}

/// <summary>Per-run context passed through <see cref="UINodeRunContext.Services"/> while one scenario sheet executes.</summary>
internal sealed class ScenarioRunScope(IServiceProvider outer) : IServiceProvider, IScenarioContext, IScenarioSecrets, IScenarioMedia
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, MessageSlice> _slicesByNode = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, (string GenerationId, string? Thought)> _generationsByNode = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _skippedNodes = [with(StringComparer.Ordinal)];
    private readonly List<GenerationRecord> _generations = [];
    private readonly List<ChatRecord> _stagedRecords = [];
    private readonly List<ChatVisual> _stagedVisuals = [];
    private UINodeNetwork? _network;
    private Task<(int CurrentTokens, int PreviousTokens, int ContextLength)>? _measureTask;
    private bool? _chancePassed;
    private int _executedActions;
    private string? _stagedDraft;
    private string? _stagedDraftGenerationId;
    private bool _stagedDraftContinued;

    public required DataFolder Data { get; init; }

    public required KoboldCppClients Clients { get; init; }

    public ActiveProfile? ActiveProfile { get; init; }

    public IReadOnlyList<(string Id, ActiveProfile Profile)>? PreloadedProfiles { get; init; }

    public IReadOnlyList<StoredDocument<SamplerPreset>>? PreloadedSamplers { get; init; }

    public required AppSettings Settings { get; init; }

    public string? ChatId { get; init; }

    public required Chat Chat { get; init; }

    public Chat? PreTurnChat { get; init; }

    public Character? Character { get; init; }

    public Persona? Persona { get; init; }

    public World? World { get; init; }

    public required string CharacterName { get; init; }

    public required string UserName { get; init; }

    public required string ScenarioId { get; init; }

    public required string ScenarioName { get; init; }

    public ScenarioTrigger Trigger { get; init; } = new();

    public required UINodeDocument Document { get; init; }

    public bool CommitToChat { get; init; } = true;

    public bool BypassGates { get; init; }

    public Func<int, int>? RandomNext { get; init; }

    public string InitialDraft { get; init; } = string.Empty;

    // Unsent composer text during BeforeReply/AfterReply is not yet part of the conversation turn.
    public string? PendingOutgoingDraft
        => (Trigger.Kind == ScenarioTriggerKind.BeforeSend || !CommitToChat) && !string.IsNullOrWhiteSpace(InitialDraft)
            ? InitialDraft
            : null;

    public ActiveGenerations? ActiveGenerations { get; init; }

    public Func<string, string, string, string, ValueTask>? OnStreamChunkAsync { get; init; }

    public Func<string, ValueTask>? OnDraftChunkAsync { get; init; }

    public bool WasSkippedByCondition
    {
        get
        {
            lock (_sync)
                return _executedActions == 0 && _skippedNodes.Count > 0;
        }
    }

    string? IScenarioContext.CharacterId => Chat.Character;

    bool IScenarioContext.IsPreview => !CommitToChat;

    string IScenarioContext.Draft => InitialDraft;

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ScenarioRunScope)
            || serviceType == typeof(IScenarioContext)
            || serviceType == typeof(IScenarioSecrets)
            || serviceType == typeof(IScenarioMedia))
        {
            return this;
        }

        if (serviceType == typeof(HttpClient) && outer.GetService(typeof(IHttpClientFactory)) is IHttpClientFactory factory)
            return factory.CreateClient();

        return outer.GetService(serviceType);
    }

    async Task<string?> IScenarioSecrets.GetAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new UINodeFailureException("Secret name is required.");

        var key = Slug.Of(name.Trim());

        if (!Slug.IsValidId(key))
            throw new UINodeFailureException($"'{name}' is not a valid secret name.");

        if (outer.GetService(typeof(SecretStore)) is not SecretStore store)
            return null;

        try
        {
            return await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretUnreadableException exception)
        {
            throw new UINodeFailureException(exception.Message, exception);
        }
    }

    async Task<string> IScenarioMedia.SaveImageAsync(ReadOnlyMemory<byte> content, string? contentType, string? preferredName, CancellationToken cancellationToken)
    {
        if (content.IsEmpty)
            throw new UINodeFailureException("Generated image is empty.");

        if (content.Length > Views.PictureFields.MaxBackgroundBytes)
            throw new UINodeFailureException("Generated image exceeds the 16 MB limit.");

        PictureContent? pictures = outer.GetService(typeof(PictureContent)) as PictureContent;

        if (CommitToChat && ChatId is string chatId && Slug.IsValidId(chatId))
        {
            GalleryImage stored = await GalleryPictures.WriteChatMediaAsync(Data, chatId, preferredName, content, contentType, cancellationToken).ConfigureAwait(false);

            return pictures?.ChatMediaOf(chatId, stored.Id) ?? stored.Key;
        }

        return pictures is not null
            ? pictures.SavePreviewImage(content, contentType)
            : $"data:image/png;base64,{Convert.ToBase64String(content.Span)}";
    }

    public static ScenarioRunScope From(UINodeRunContext context)
        => context.Services?.GetService(typeof(ScenarioRunScope)) as ScenarioRunScope
            ?? throw new UINodeFailureException("Chat nodes run only inside a chat scenario.");

    public bool CheckTriggerGateBeforeRun()
    {
        if (BypassGates || !CommitToChat || Trigger.Kind == ScenarioTriggerKind.Manual || !Trigger.Gate.Enabled)
            return true;

        var pendingOutgoingTurn = PendingOutgoingDraft is not null && (Chat.Messages.Count == 0 || Chat.Messages[^1].Role != MessageRole.User) ? 1 : 0;
        var userTurns = ChatRecords.CountUserTurns(Chat.Messages) + pendingOutgoingTurn;
        var minTurns = Math.Max(0, Trigger.Gate.MinTurns);

        if (minTurns > 0 && userTurns < minTurns)
            return false;

        var cooldown = Math.Max(0, Trigger.Gate.Cooldown);

        if (cooldown > 0 && ChatRecords.TurnsSinceScenarioOutput(Chat, ScenarioId) is int elapsed && elapsed <= cooldown)
            return false;

        return HasAnyWiredWhenPin() || RollTriggerChance(out _);
    }

    private bool HasAnyWiredWhenPin()
    {
        for (var i = 0; i < Document.Edges.Length; i++)
        {
            UINodeEdge edge = Document.Edges[i];

            if (string.Equals(edge.ToPin, nameof(ModelCallNode.When), StringComparison.Ordinal)
                && ScenarioDependencies.FindEnabledNode(Document, edge.FromNode) is not null
                && ScenarioDependencies.FindEnabledNode(Document, edge.ToNode) is UINode target
                && IsActionNode(target.Type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsActionNode(string nodeType)
        => string.Equals(nodeType, ModelCallNode.NodeKey, StringComparison.Ordinal)
            || string.Equals(nodeType, ReplaceRecordNode.NodeKey, StringComparison.Ordinal)
            || string.Equals(nodeType, InsertRecordNode.NodeKey, StringComparison.Ordinal)
            || string.Equals(nodeType, WriteDraftNode.NodeKey, StringComparison.Ordinal)
            || string.Equals(nodeType, SetVisualNode.NodeKey, StringComparison.Ordinal);

    private bool RollTriggerChance(out int rolledPercent)
    {
        lock (_sync)
        {
            if (_chancePassed is bool cached)
            {
                rolledPercent = 0;
                return cached;
            }

            var chance = Math.Clamp(Trigger.Gate.Chance, 1, ScenarioGate.DefaultChance);

            if (chance >= ScenarioGate.DefaultChance)
            {
                _chancePassed = true;
                rolledPercent = 0;
                return true;
            }

            rolledPercent = DrawRandomInt(ScenarioGate.DefaultChance) + 1;
            var passed = rolledPercent <= chance;
            _chancePassed = passed;
            return passed;
        }
    }

    public int DrawRandomInt(int maxExclusive)
        => maxExclusive <= 1
            ? 0
            : RandomNext is not null
                ? Math.Clamp(RandomNext(maxExclusive), 0, maxExclusive - 1)
                : RandomNumberGenerator.GetInt32(maxExclusive);

    public bool ShouldRunAction(UINodeRunContext context, bool whenValue)
    {
        if (HasSkippedUpstream(context.NodeId))
        {
            MarkSkipped(context.NodeId);
            context.Log("Skipped: upstream node did not run.");
            return false;
        }

        if (!whenValue)
        {
            if (CanBypassPreModelWhen(context.NodeId))
            {
                context.Log(!CommitToChat
                    ? "Preview: When is false; continuing preview."
                    : "Manual run: When bypassed.");
            }
            else
            {
                MarkSkipped(context.NodeId);
                context.Log("Skipped: When is false.");
                return false;
            }
        }

        if (!BypassGates && CommitToChat && Trigger.Kind != ScenarioTriggerKind.Manual && Trigger.Gate.Enabled && !RollTriggerChance(out var roll))
        {
            MarkSkipped(context.NodeId);
            context.Log(roll > 0
                ? string.Create(CultureInfo.InvariantCulture, $"Skipped: gate roll {roll}% > {Math.Clamp(Trigger.Gate.Chance, 1, ScenarioGate.DefaultChance)}%.")
                : "Skipped: gate did not pass.");
            return false;
        }

        lock (_sync)
            _executedActions++;

        return true;
    }

    private bool HasSkippedUpstream(string nodeId)
    {
        lock (_sync)
        {
            return _skippedNodes.Count > 0 && SearchUpstream(nodeId, (fromNodeId, _) => _skippedNodes.Contains(fromNodeId));
        }
    }

    private bool SearchUpstream(string startNodeId, Func<string, UINode?, bool> match)
    {
        HashSet<string> visited = [with(StringComparer.Ordinal), startNodeId];
        Queue<string> pending = new();
        pending.Enqueue(startNodeId);

        while (pending.Count > 0)
        {
            var currentId = pending.Dequeue();

            for (var i = 0; i < Document.Edges.Length; i++)
            {
                UINodeEdge edge = Document.Edges[i];

                if (!string.Equals(edge.ToNode, currentId, StringComparison.Ordinal))
                    continue;

                UINode? source = ScenarioDependencies.FindEnabledNode(Document, edge.FromNode);

                if (match(edge.FromNode, source))
                    return true;

                if (source is not null && visited.Add(source.Id))
                    pending.Enqueue(source.Id);
            }
        }

        return false;
    }

    private void MarkSkipped(string nodeId)
    {
        lock (_sync)
            _ = _skippedNodes.Add(nodeId);
    }

    private bool CanBypassPreModelWhen(string nodeId)
        => (BypassGates || !CommitToChat)
            && !HasUpstreamActionNode(nodeId)
            && CountPreModelWhenTargets() <= 1;

    private bool HasUpstreamActionNode(string nodeId)
        => SearchUpstream(nodeId, static (_, source) => source is not null && IsActionNode(source.Type));

    private int CountPreModelWhenTargets()
    {
        HashSet<string> targets = [with(StringComparer.Ordinal)];

        for (var i = 0; i < Document.Edges.Length; i++)
        {
            UINodeEdge edge = Document.Edges[i];

            if (string.Equals(edge.ToPin, nameof(ModelCallNode.When), StringComparison.Ordinal)
                && ScenarioDependencies.FindEnabledNode(Document, edge.FromNode) is not null
                && ScenarioDependencies.FindEnabledNode(Document, edge.ToNode) is UINode target
                && IsActionNode(target.Type)
                && !HasUpstreamActionNode(target.Id))
            {
                _ = targets.Add(target.Id);
            }
        }

        return targets.Count;
    }

    public void RememberSlice(string nodeId, MessageSlice slice)
    {
        lock (_sync)
            _slicesByNode[nodeId] = slice;
    }

    public void RecordGeneration(string nodeId, GenerationRecord record, string? thought)
    {
        lock (_sync)
        {
            _generations.Add(record);
            _generationsByNode[nodeId] = (record.Id, thought);
        }
    }

    public (string? GenerationId, string? Thought) ResolveSourceGeneration(string writerNodeId)
    {
        lock (_sync)
        {
            (string GenerationId, string? Thought) found = default;
            var matched = SearchUpstream(writerNodeId, (fromNodeId, _) => _generationsByNode.TryGetValue(fromNodeId, out found));

            return matched ? (found.GenerationId, found.Thought) : (null, null);
        }
    }

    public void StageRecord(ChatRecord record)
    {
        lock (_sync)
            _stagedRecords.Add(record);
    }

    public void StageVisual(ChatVisual visual)
    {
        lock (_sync)
            _stagedVisuals.Add(visual);
    }

    public void StageDraft(string draft, string? generationId, bool continued)
    {
        lock (_sync)
        {
            _stagedDraft = draft;
            _stagedDraftGenerationId = generationId;
            _stagedDraftContinued = continued;
        }
    }

    public ScenarioDelta CreateDelta()
    {
        lock (_sync)
            return new ScenarioDelta([.. _stagedRecords], [.. _generations], _stagedDraft, _stagedDraftGenerationId, _stagedDraftContinued, _stagedVisuals.Count == 0 ? null : [.. _stagedVisuals]);
    }

    public DraftWriteMode? ResolveDraftWriteMode(string modelNodeId)
    {
        for (var i = 0; i < Document.Edges.Length; i++)
        {
            UINodeEdge edge = Document.Edges[i];

            if (string.Equals(edge.FromNode, modelNodeId, StringComparison.Ordinal)
                && string.Equals(edge.FromPin, nameof(ModelCallNode.Text), StringComparison.Ordinal)
                && string.Equals(edge.ToPin, nameof(WriteDraftNode.Text), StringComparison.Ordinal)
                && ScenarioDependencies.FindEnabledNode(Document, edge.ToNode) is UINode target
                && string.Equals(target.Type, WriteDraftNode.NodeKey, StringComparison.Ordinal))
            {
                return ReadDraftWriteMode(target);
            }
        }

        return null;
    }

    internal static DraftWriteMode ReadDraftWriteMode(UINode node)
        => ScenarioDependencies.ReadEnum(node, nameof(WriteDraftNode.Mode), DraftWriteMode.Replace);

    public string? ResolveStreamingAnchor(string modelNodeId)
    {
        if (Chat.Messages.Count == 0)
            return null;

        HashSet<string> visited = [with(StringComparer.Ordinal), modelNodeId];
        Queue<string> pending = new();
        pending.Enqueue(modelNodeId);
        var feedsDraft = false;

        while (pending.Count > 0)
        {
            var currentId = pending.Dequeue();

            for (var i = 0; i < Document.Edges.Length; i++)
            {
                UINodeEdge edge = Document.Edges[i];

                if (!string.Equals(edge.FromNode, currentId, StringComparison.Ordinal))
                    continue;

                UINode? target = ScenarioDependencies.FindEnabledNode(Document, edge.ToNode);

                if (target is null)
                    continue;

                if (string.Equals(target.Type, ReplaceRecordNode.NodeKey, StringComparison.Ordinal) || string.Equals(target.Type, InsertRecordNode.NodeKey, StringComparison.Ordinal) || string.Equals(target.Type, SetVisualNode.NodeKey, StringComparison.Ordinal))
                {
                    if (FindSliceAnchorForWriter(target.Id) is string sliceEnd)
                        return sliceEnd;

                    if (string.Equals(target.Type, InsertRecordNode.NodeKey, StringComparison.Ordinal) || string.Equals(target.Type, SetVisualNode.NodeKey, StringComparison.Ordinal))
                        return Chat.Messages[^1].Id;
                }

                if (string.Equals(target.Type, WriteDraftNode.NodeKey, StringComparison.Ordinal))
                    feedsDraft = true;

                if (visited.Add(target.Id))
                    pending.Enqueue(target.Id);
            }
        }

        return feedsDraft ? null : Chat.Messages[^1].Id;
    }

    private string? FindSliceAnchorForWriter(string writerNodeId)
    {
        lock (_sync)
        {
            for (var i = 0; i < Document.Edges.Length; i++)
            {
                UINodeEdge edge = Document.Edges[i];

                if (string.Equals(edge.ToNode, writerNodeId, StringComparison.Ordinal)
                    && string.Equals(edge.ToPin, nameof(ReplaceRecordNode.Slice), StringComparison.Ordinal)
                    && _slicesByNode.TryGetValue(edge.FromNode, out MessageSlice? slice)
                    && slice.LastMessageId is string lastId)
                {
                    return lastId;
                }
            }
        }

        return null;
    }

    public string? ResolveOverwrittenAnchor(string? tag)
    {
        if (Chat.Messages.Count == 0 || string.IsNullOrWhiteSpace(tag))
            return null;

        var trimmed = tag.Trim();
        UINodeNetwork network;

        lock (_sync)
            network = _network ??= ScenarioCatalog.Resolve(this).Materialize(Document);

        string? earliestAnchorId = null;
        var earliestIndex = int.MaxValue;

        for (var i = 0; i < network.Nodes.Length; i++)
        {
            UINodeInstance instance = network.Nodes[i];
            var writerTag = ResolveWriterTag(network, instance);

            if (!string.Equals(writerTag, trimmed, StringComparison.Ordinal))
                continue;

            var anchorId = ResolveWriterAnchor(network, instance);

            if (anchorId is null)
                continue;

            var index = ChatEditing.IndexOf(Chat.Messages, anchorId);

            if (index >= 0 && index < earliestIndex)
            {
                earliestIndex = index;
                earliestAnchorId = anchorId;
            }
        }

        return earliestAnchorId;
    }

    private static string? ResolveWriterTag(UINodeNetwork network, UINodeInstance instance)
    {
        string? direct;

        if (instance.Node is InsertRecordNode insert)
            direct = insert.Tag;
        else if (instance.Node is ReplaceRecordNode replace)
            direct = replace.Tag;
        else
            return null;

        for (var i = 0; i < network.Connections.Length; i++)
        {
            UINodeConnection connection = network.Connections[i];

            if (string.Equals(connection.To.Id, instance.Id, StringComparison.Ordinal)
                && string.Equals(connection.ToPin, nameof(InsertRecordNode.Tag), StringComparison.Ordinal))
            {
                return connection.From.Node is TextNode textNode && !string.IsNullOrWhiteSpace(textNode.Value)
                    ? textNode.Value.Trim()
                    : null;
            }
        }

        return string.IsNullOrWhiteSpace(direct) ? null : direct.Trim();
    }

    private string? ResolveWriterAnchor(UINodeNetwork network, UINodeInstance writer)
    {
        for (var i = 0; i < network.Connections.Length; i++)
        {
            UINodeConnection connection = network.Connections[i];

            if (!string.Equals(connection.To.Id, writer.Id, StringComparison.Ordinal)
                || !string.Equals(connection.ToPin, nameof(ReplaceRecordNode.Slice), StringComparison.Ordinal))
            {
                continue;
            }

            lock (_sync)
            {
                if (_slicesByNode.TryGetValue(connection.From.Id, out MessageSlice? slice))
                    return slice.LastMessageId;
            }

            if (connection.From.Node is MessagesNode messages)
                return ChatRecords.Slice(Chat.Messages, Chat.Records, messages.SkipStart, messages.SkipEnd, messages.Last, messages.Raw).LastMessageId;

            return null;
        }

        return writer.Node is InsertRecordNode ? Chat.Messages[^1].Id : null;
    }

    public bool HasIncomingEdge(string toNodeId, string toPin)
    {
        for (var i = 0; i < Document.Edges.Length; i++)
        {
            UINodeEdge edge = Document.Edges[i];

            if (string.Equals(edge.ToNode, toNodeId, StringComparison.Ordinal)
                && string.Equals(edge.ToPin, toPin, StringComparison.Ordinal)
                && ScenarioDependencies.FindEnabledNode(Document, edge.FromNode) is not null)
            {
                return true;
            }
        }

        return false;
    }

    public Task<(int CurrentTokens, int PreviousTokens, int ContextLength)> MeasureContextAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
            return _measureTask ??= MeasureContextCoreAsync(cancellationToken);
    }

    private async Task<(int CurrentTokens, int PreviousTokens, int ContextLength)> MeasureContextCoreAsync(CancellationToken cancellationToken)
    {
        if (ActiveProfile is not ActiveProfile profile)
            return (0, 0, 0);

        using IDisposable? serverSlot = profile.Connection.Address is Uri slotAddress
            ? await Clients.AcquireSlotAsync(slotAddress, cancellationToken).ConfigureAwait(false)
            : null;
        KoboldCppClient? kobold = null;
        string? model = null;
        int contextLength;

        if (profile.Connection.Address is Uri address)
        {
            kobold = Clients.For(address);
            ResolvedContext resolved = await ContextLengths.ResolveAsync(profile.Connection, kobold, cancellationToken).ConfigureAwait(false);
            contextLength = resolved.Length;

            if (!resolved.ServerAway)
                model = await KoboldCppClients.TryGetModelNameAsync(kobold, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            contextLength = Math.Max(0, profile.Connection.ContextLength.Value);
        }

        if (contextLength <= 0)
            return (0, 0, 0);

        Chat? previousChat = PreTurnChat ?? DerivePreviousTurnChat(Chat);
        var currentTokens = await CountChatPromptTokensAsync(profile, Chat, contextLength, kobold, model, cancellationToken).ConfigureAwait(false);
        var previousTokens = previousChat is null
            ? 0
            : await CountChatPromptTokensAsync(profile, previousChat, contextLength, kobold, model, cancellationToken).ConfigureAwait(false);

        return (currentTokens, previousTokens, contextLength);
    }

    private static Chat? DerivePreviousTurnChat(Chat chat)
    {
        var count = chat.Messages.Count;

        if (count == 0)
            return null;

        var trimFrom = count - 1;

        if (chat.Messages[trimFrom].Role == MessageRole.Character && trimFrom > 0 && chat.Messages[trimFrom - 1].Role == MessageRole.User)
            trimFrom--;

        return ChatEditing.DeleteFrom(chat, chat.Messages[trimFrom].Id);
    }

    private async Task<int> CountChatPromptTokensAsync(ActiveProfile profile, Chat chat, int contextLength, KoboldCppClient? kobold, string? model, CancellationToken cancellationToken)
    {
        IReadOnlyList<PromptMessage> projectedHistory = ChatRecords.ProjectHistory(chat.Messages, chat.Records);
        PromptSpec spec = new()
        {
            SystemPrompt = profile.SystemPrompt.Content,
            Prompt = profile.Prompt,
            Character = Character,
            CharacterName = CharacterName,
            UserName = UserName,
            Persona = Persona,
            Lore = WorldInfo.ArticlesOf(World, projectedHistory),
            AuthorsNotes = [Settings.AuthorsNote, Character?.AuthorsNote, chat.AuthorsNote],
            NotePlacement = Settings.AuthorsNotePlacement,
            History = projectedHistory
        };

        BuiltPrompt built = await Clients.BuildPromptWithFallbackAsync(spec, profile.Format, contextLength, profile.Samplers.ReplyLength, profile.Connection.Address, kobold, model, cancellationToken).ConfigureAwait(false);

        return built.Tokens;
    }
}
