using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Plugins;
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
    private readonly List<GenerationRecord> _generations = [];
    private readonly List<ChatRecord> _stagedRecords = [];
    private readonly List<ChatVisual> _stagedVisuals = [];
    private UINodeNetwork? _network;
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

    public Character? Character { get; init; }

    public Persona? Persona { get; init; }

    public World? World { get; init; }

    public required string CharacterName { get; init; }

    public required string UserName { get; init; }

    public required string ScenarioId { get; init; }

    public required string ScenarioName { get; init; }

    public required UINodeDocument Document { get; init; }

    /// <summary>Whether writers stage records onto the chat; <c>false</c> during a canvas preview run on <c>/scenarios</c>.</summary>
    public bool CommitToChat { get; init; } = true;

    /// <summary>The composer draft as it stood before the scenario started, so a draft-writing model call can continue it.</summary>
    public string InitialDraft { get; init; } = string.Empty;

    public ActiveGenerations? ActiveGenerations { get; init; }

    public Func<string, string, string, string, ValueTask>? OnStreamChunkAsync { get; init; }

    public Func<string, ValueTask>? OnDraftChunkAsync { get; init; }

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
}
