using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
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

/// <summary>
/// Per-run context passed through <see cref="UINodeRunContext.Services"/> while one scenario sheet executes: the chat it reads,
/// live streaming callbacks for the feed's folded block, and the records and generations staged for atomic commit when the run ends.
/// </summary>
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
    private string? _lastGenerationId;
    private string? _lastThought;

    public required DataFolder Data { get; init; }

    public required KoboldCppClients Clients { get; init; }

    public ActiveProfile? ActiveProfile { get; init; }

    public IReadOnlyList<(string Id, ActiveProfile Profile)>? PreloadedProfiles { get; init; }

    public IReadOnlyList<StoredDocument<SamplerPreset>>? PreloadedSamplers { get; init; }

    public required AppSettings Settings { get; init; }

    public string? ChatId { get; init; }

    public required Chat Chat { get; init; }

    /// <summary>Optional snapshot of the chat before the current turn started, used by <see cref="ContextNode"/> to detect rising-edge threshold crossings.</summary>
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

    /// <summary>Whether writers stage records onto the chat; <c>false</c> during a canvas preview run on <c>/scenarios</c>.</summary>
    public bool CommitToChat { get; init; } = true;

    /// <summary>Whether trigger gates and single-branch pre-model <c>When</c> guards pass unconditionally because the reader started the scenario by hand.</summary>
    public bool BypassGates { get; init; }

    /// <summary>Optional deterministic <c>[0, maxExclusive)</c> generator for testing <see cref="ScenarioGate"/> and <see cref="CueMenuNode"/>.</summary>
    public Func<int, int>? RandomNext { get; init; }

    /// <summary>The composer draft as it stood before the scenario started, so a draft-writing model call can continue it.</summary>
    public string InitialDraft { get; init; } = string.Empty;

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

    /// <summary>Checks the scenario's sidebar <see cref="ScenarioGate"/> before any node runs; rolls <see cref="ScenarioGate.Chance"/> eagerly when the sheet has no <c>When</c> edges.</summary>
    public bool CheckTriggerGateBeforeRun()
    {
        if (BypassGates || !CommitToChat || Trigger.Kind == ScenarioTriggerKind.Manual || !Trigger.Gate.Enabled)
            return true;

        var pendingOutgoingTurn = InitialDraft.Length > 0 && (Chat.Messages.Count == 0 || Chat.Messages[^1].Role != MessageRole.User) ? 1 : 0;
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
                && FindEnabledNode(edge.FromNode) is not null
                && FindEnabledNode(edge.ToNode) is UINode target
                && IsActionNode(target.Type))
            {
                return true;
            }
        }

        return false;
    }

    private UINode? FindEnabledNode(string nodeId)
    {
        for (var i = 0; i < Document.Nodes.Length; i++)
        {
            UINode node = Document.Nodes[i];

            if (node.Enabled && string.Equals(node.Id, nodeId, StringComparison.Ordinal))
                return node;
        }

        return null;
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

    /// <summary>Evaluates whether an action node (<see cref="ModelCallNode"/> or writer) should execute, propagating upstream skips and lazy gate rolls.</summary>
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
            if (_skippedNodes.Count == 0)
                return false;

            HashSet<string> visited = [with(StringComparer.Ordinal), nodeId];
            Queue<string> pending = new();
            pending.Enqueue(nodeId);

            while (pending.Count > 0)
            {
                var currentId = pending.Dequeue();

                for (var i = 0; i < Document.Edges.Length; i++)
                {
                    UINodeEdge edge = Document.Edges[i];

                    if (!string.Equals(edge.ToNode, currentId, StringComparison.Ordinal))
                        continue;

                    if (_skippedNodes.Contains(edge.FromNode))
                        return true;

                    if (FindEnabledNode(edge.FromNode) is UINode source && visited.Add(source.Id))
                        pending.Enqueue(source.Id);
                }
            }

            return false;
        }
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
    {
        HashSet<string> visited = [with(StringComparer.Ordinal), nodeId];
        Queue<string> pending = new();
        pending.Enqueue(nodeId);

        while (pending.Count > 0)
        {
            var currentId = pending.Dequeue();

            for (var i = 0; i < Document.Edges.Length; i++)
            {
                UINodeEdge edge = Document.Edges[i];

                if (!string.Equals(edge.ToNode, currentId, StringComparison.Ordinal) || FindEnabledNode(edge.FromNode) is not UINode source)
                    continue;

                if (IsActionNode(source.Type))
                    return true;

                if (visited.Add(source.Id))
                    pending.Enqueue(source.Id);
            }
        }

        return false;
    }

    private int CountPreModelWhenTargets()
    {
        HashSet<string> targets = [with(StringComparer.Ordinal)];

        for (var i = 0; i < Document.Edges.Length; i++)
        {
            UINodeEdge edge = Document.Edges[i];

            if (string.Equals(edge.ToPin, nameof(ModelCallNode.When), StringComparison.Ordinal)
                && FindEnabledNode(edge.FromNode) is not null
                && FindEnabledNode(edge.ToNode) is UINode target
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
            _lastGenerationId = record.Id;
            _lastThought = thought;
        }
    }

    public (string? GenerationId, string? Thought) ResolveSourceGeneration(string writerNodeId)
    {
        lock (_sync)
        {
            if (FindUpstreamGenerationUnlocked(writerNodeId) is (var id, var thought))
                return (id, thought);

            return (_lastGenerationId, _lastThought);
        }
    }

    private (string GenerationId, string? Thought)? FindUpstreamGenerationUnlocked(string writerNodeId)
    {
        HashSet<string> visited = [with(StringComparer.Ordinal), writerNodeId];
        Queue<string> pending = new();
        pending.Enqueue(writerNodeId);

        while (pending.Count > 0)
        {
            var currentId = pending.Dequeue();

            for (var i = 0; i < Document.Edges.Length; i++)
            {
                UINodeEdge edge = Document.Edges[i];

                if (!string.Equals(edge.ToNode, currentId, StringComparison.Ordinal))
                    continue;

                if (_generationsByNode.TryGetValue(edge.FromNode, out (string GenerationId, string? Thought) found))
                    return found;

                if (FindEnabledNode(edge.FromNode) is UINode source && visited.Add(source.Id))
                    pending.Enqueue(source.Id);
            }
        }

        return null;
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

    /// <summary>Resolves the <see cref="DraftWriteMode"/> of an enabled downstream <see cref="WriteDraftNode"/>, or <c>null</c> when <paramref name="modelNodeId"/> does not feed one.</summary>
    public DraftWriteMode? ResolveDraftWriteMode(string modelNodeId)
    {
        HashSet<string> visited = [with(StringComparer.Ordinal), modelNodeId];
        Queue<string> pending = new();
        pending.Enqueue(modelNodeId);

        while (pending.Count > 0)
        {
            var currentId = pending.Dequeue();

            for (var i = 0; i < Document.Edges.Length; i++)
            {
                UINodeEdge edge = Document.Edges[i];

                if (!string.Equals(edge.FromNode, currentId, StringComparison.Ordinal))
                    continue;

                UINode? target = FindEnabledNode(edge.ToNode);

                if (target is null)
                    continue;

                if (string.Equals(target.Type, WriteDraftNode.NodeKey, StringComparison.Ordinal))
                    return ReadDraftWriteMode(target);

                if (visited.Add(target.Id))
                    pending.Enqueue(target.Id);
            }
        }

        return null;
    }

    internal static DraftWriteMode ReadDraftWriteMode(UINode node)
    {
        if (!node.Values.TryGetValue(nameof(WriteDraftNode.Mode), out var value) || value is null)
            return DraftWriteMode.Replace;

        return value switch
        {
            DraftWriteMode mode => mode,
            JsonElement { ValueKind: JsonValueKind.String } element when Enum.TryParse(element.GetString(), ignoreCase: true, out DraftWriteMode parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var index) && Enum.IsDefined((DraftWriteMode)index) => (DraftWriteMode)index,
            string text when Enum.TryParse(text, ignoreCase: true, out DraftWriteMode parsed) => parsed,
            _ => DraftWriteMode.Replace
        };
    }

    /// <summary>Finds the message id under which a streaming model call's folded block should appear before downstream writers run.</summary>
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

                UINode? target = FindEnabledNode(edge.ToNode);

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

    /// <summary>Finds the earliest target anchor message id written by an enabled writer node carrying <paramref name="tag"/>, so a reader of the same tag skips a record already sitting at that anchor.</summary>
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
        var direct = instance.Node switch
        {
            InsertRecordNode insert => insert.Tag,
            ReplaceRecordNode replace => replace.Tag,
            _ => null
        };

        if (direct is null)
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
                && FindEnabledNode(edge.FromNode) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Measures and caches the assembled main-prompt token counts for <see cref="Chat"/> and the pre-turn baseline against the active connection's context length.</summary>
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
                model = await TryGetModelNameAsync(kobold, cancellationToken).ConfigureAwait(false);
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

    private static async Task<string?> TryGetModelNameAsync(KoboldCppClient kobold, CancellationToken cancellationToken)
    {
        try
        {
            var model = (await kobold.GetModelAsync(cancellationToken).ConfigureAwait(false)).Trim();

            return model.Length == 0 ? null : model;
        }
        catch (HttpRequestException)
        {
            return null;
        }
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

        if (kobold is not null && profile.Connection.Address is Uri address)
        {
            try
            {
                ITokenCounter counter = Clients.CounterFor(address, kobold, model);
                BuiltPrompt exact = await PromptBuilder.BuildAsync(spec, profile.Format, contextLength, profile.Samplers.ReplyLength, counter, cancellationToken).ConfigureAwait(false);

                return exact.Tokens;
            }
            catch (HttpRequestException)
            {
            }
        }

        BuiltPrompt approximate = await PromptBuilder.BuildAsync(spec, profile.Format, contextLength, profile.Samplers.ReplyLength, ApproximateTokenCounter.Instance, cancellationToken).ConfigureAwait(false);

        return approximate.Tokens;
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
}
