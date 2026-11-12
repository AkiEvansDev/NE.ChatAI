using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NE.ChatAI.Backends.KoboldCpp;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Scenarios;

/// <summary>
/// Per-run context passed through <see cref="UINodeRunContext.Services"/> while one scenario sheet executes: the chat it reads,
/// live streaming callbacks for the feed's folded block, and the records and generations staged for atomic commit when the run ends.
/// </summary>
internal sealed class ScenarioRunScope(IServiceProvider outer) : IServiceProvider
{
    private readonly Dictionary<string, MessageSlice> _slicesByNode = [with(StringComparer.Ordinal)];

    public required DataFolder Data { get; init; }

    public required KoboldCppClients Clients { get; init; }

    public ActiveProfile? ActiveProfile { get; init; }

    public required AppSettings Settings { get; init; }

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

    public Action<KoboldCppClient?, string?>? OnActiveGeneration { get; init; }

    public Func<string, string, string, ValueTask>? OnStreamChunkAsync { get; init; }

    public Func<string, ValueTask>? OnDraftChunkAsync { get; init; }

    public List<GenerationRecord> Generations { get; } = [];

    public List<ChatRecord> StagedRecords { get; } = [];

    public string? StagedDraft { get; set; }

    public string? StagedDraftGenerationId { get; set; }

    public string? LastGenerationId { get; set; }

    public string? LastThought { get; set; }

    public object? GetService(Type serviceType)
        => serviceType == typeof(ScenarioRunScope) ? this : outer.GetService(serviceType);

    public static ScenarioRunScope From(UINodeRunContext context)
        => context.Services?.GetService(typeof(ScenarioRunScope)) as ScenarioRunScope
            ?? throw new UINodeFailureException("Chat nodes run only inside a chat scenario.");

    public void RememberSlice(string nodeId, MessageSlice slice)
        => _slicesByNode[nodeId] = slice;

    /// <summary>Whether <paramref name="modelNodeId"/> feeds an enabled <see cref="WriteDraftNode"/> downstream.</summary>
    public bool FeedsDraftWriter(string modelNodeId)
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
                    return true;

                if (visited.Add(target.Id))
                    pending.Enqueue(target.Id);
            }
        }

        return false;
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

                if (string.Equals(target.Type, ReplaceRecordNode.NodeKey, StringComparison.Ordinal) || string.Equals(target.Type, InsertRecordNode.NodeKey, StringComparison.Ordinal))
                {
                    if (FindSliceAnchorForWriter(target.Id) is string sliceEnd)
                        return sliceEnd;

                    if (string.Equals(target.Type, InsertRecordNode.NodeKey, StringComparison.Ordinal))
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

        return null;
    }
}
