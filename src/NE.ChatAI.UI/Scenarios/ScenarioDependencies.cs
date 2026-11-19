using System;
using System.Collections.Generic;
using System.Text.Json;
using NE.ChatAI.Chats;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Scenarios;

/// <summary>
/// Infers read-after-write and write-after-write dependencies across an ordered batch of scenarios so independent sheets run in
/// parallel while sheets sharing projected history, record tags or draft state wait for their upstream writers.
/// </summary>
internal static class ScenarioDependencies
{
    public static int[][] Analyze(IReadOnlyList<StoredDocument<Scenario>> scenarios, IReadOnlyList<UINodeDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(documents);

        var count = scenarios.Count;
        var result = new int[count][];

        if (count == 0)
            return result;

        ScenarioAccess[] accesses = new ScenarioAccess[count];

        for (var i = 0; i < count; i++)
        {
            Scenario normalized = scenarios[i].Document.UpgradeLegacyTrigger();
            UINodeDocument sheet = ReferenceEquals(normalized, scenarios[i].Document) ? documents[i] : ScenarioExecutor.ReadSheet(normalized);
            accesses[i] = Inspect(normalized, sheet);
        }

        for (var later = 0; later < count; later++)
        {
            List<int>? upstream = null;

            for (var earlier = 0; earlier < later; earlier++)
            {
                if (!DependsOn(accesses[earlier], accesses[later]))
                    continue;

                upstream ??= [];
                upstream.Add(earlier);
            }

            result[later] = upstream is null ? [] : [.. upstream];
        }

        return result;
    }

    private static ScenarioAccess Inspect(Scenario scenario, UINodeDocument document)
    {
        ScenarioAccess access = new()
        {
            ReadsProjectedHistory = scenario.Trigger.LegacyContextShare is not null
        };

        for (var i = 0; i < document.Nodes.Length; i++)
        {
            UINode node = document.Nodes[i];

            if (!node.Enabled)
                continue;

            if (string.Equals(node.Type, MessagesNode.NodeKey, StringComparison.Ordinal))
            {
                if (!IsRawMessagesNode(document, node))
                    access.ReadsProjectedHistory = true;
            }
            else if (string.Equals(node.Type, ContextNode.NodeKey, StringComparison.Ordinal))
            {
                access.ReadsProjectedHistory = true;
            }
            else if (string.Equals(node.Type, TriggerMatchNode.NodeKey, StringComparison.Ordinal))
            {
                if (FindIncomingSource(document, node.Id, nameof(TriggerMatchNode.Text)) is null)
                {
                    access.ReadsProjectedHistory = true;
                    access.ReadsDraft = true;
                }
            }
            else if (string.Equals(node.Type, RecordNode.NodeKey, StringComparison.Ordinal))
            {
                ResolveTagPin(document, node, nameof(RecordNode.Tag), access.ReadTags, out var dynamicTag, out _);

                if (dynamicTag)
                    access.ReadsAnyTag = true;
            }
            else if (string.Equals(node.Type, ReplaceRecordNode.NodeKey, StringComparison.Ordinal) || string.Equals(node.Type, InsertRecordNode.NodeKey, StringComparison.Ordinal))
            {
                access.ModifiesProjectedHistory = true;
                ResolveTagPin(document, node, nameof(InsertRecordNode.Tag), access.WrittenTags, out var dynamicTag, out var untagged);

                if (dynamicTag)
                    access.WritesAnyTag = true;

                if (untagged)
                    access.WritesUntaggedRecord = true;
            }
            else if (string.Equals(node.Type, ReadDraftNode.NodeKey, StringComparison.Ordinal))
            {
                access.ReadsDraft = true;
            }
            else if (string.Equals(node.Type, WriteDraftNode.NodeKey, StringComparison.Ordinal))
            {
                access.WritesDraft = true;

                if (ScenarioRunScope.ReadDraftWriteMode(node) == DraftWriteMode.Continue)
                    access.ReadsDraft = true;
            }
            else if (string.Equals(node.Type, SetVisualNode.NodeKey, StringComparison.Ordinal))
            {
                if (FindIncomingSource(document, node.Id, nameof(SetVisualNode.Target)) is not null)
                    access.WritesAnyVisualTarget = true;
                else
                    _ = access.WrittenVisualTargets.Add(ReadVisualTarget(node));
            }
        }

        return access;
    }

    private static bool IsRawMessagesNode(UINodeDocument document, UINode node)
    {
        UINode? wiredSource = FindIncomingSource(document, node.Id, nameof(MessagesNode.Raw));

        if (wiredSource is not null)
            return string.Equals(wiredSource.Type, "graph.bool", StringComparison.Ordinal) && !HasIncomingEdge(document, wiredSource.Id) && ReadBool(wiredSource, "Value");

        return ReadBool(node, nameof(MessagesNode.Raw));
    }

    private static void ResolveTagPin(UINodeDocument document, UINode node, string pinName, HashSet<string> tags, out bool dynamicTag, out bool untagged)
    {
        dynamicTag = false;
        untagged = false;

        UINode? wiredSource = FindIncomingSource(document, node.Id, pinName);

        if (wiredSource is not null)
        {
            if (string.Equals(wiredSource.Type, "graph.text", StringComparison.Ordinal) && !HasIncomingEdge(document, wiredSource.Id))
            {
                var constant = ReadString(wiredSource, "Value");

                if (string.IsNullOrWhiteSpace(constant))
                    untagged = true;
                else
                    _ = tags.Add(constant.Trim());

                return;
            }

            dynamicTag = true;
            return;
        }

        var direct = ReadString(node, pinName);

        if (string.IsNullOrWhiteSpace(direct))
            untagged = true;
        else
            _ = tags.Add(direct.Trim());
    }

    private static UINode? FindIncomingSource(UINodeDocument document, string toNodeId, string toPin)
    {
        for (var i = 0; i < document.Edges.Length; i++)
        {
            UINodeEdge edge = document.Edges[i];

            if (!string.Equals(edge.ToNode, toNodeId, StringComparison.Ordinal) || !string.Equals(edge.ToPin, toPin, StringComparison.Ordinal))
                continue;

            UINode? source = FindEnabledNode(document, edge.FromNode);

            if (source is not null)
                return source;
        }

        return null;
    }

    private static UINode? FindEnabledNode(UINodeDocument document, string nodeId)
    {
        for (var i = 0; i < document.Nodes.Length; i++)
        {
            UINode node = document.Nodes[i];

            if (node.Enabled && string.Equals(node.Id, nodeId, StringComparison.Ordinal))
                return node;
        }

        return null;
    }

    private static bool HasIncomingEdge(UINodeDocument document, string toNodeId)
    {
        for (var i = 0; i < document.Edges.Length; i++)
        {
            UINodeEdge edge = document.Edges[i];

            if (string.Equals(edge.ToNode, toNodeId, StringComparison.Ordinal) && FindEnabledNode(document, edge.FromNode) is not null)
                return true;
        }

        return false;
    }

    private static bool ReadBool(UINode node, string key)
    {
        if (!node.Values.TryGetValue(key, out var value) || value is null)
            return false;

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false
        };
    }

    private static string? ReadString(UINode node, string key)
    {
        if (!node.Values.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
    }

    private static ChatVisualTarget ReadVisualTarget(UINode node)
    {
        if (!node.Values.TryGetValue(nameof(SetVisualNode.Target), out var value) || value is null)
            return ChatVisualTarget.Stage;

        return value switch
        {
            ChatVisualTarget target => target,
            JsonElement { ValueKind: JsonValueKind.String } element when Enum.TryParse(element.GetString(), ignoreCase: true, out ChatVisualTarget parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var index) && Enum.IsDefined((ChatVisualTarget)index) => (ChatVisualTarget)index,
            string text when Enum.TryParse(text, ignoreCase: true, out ChatVisualTarget parsed) => parsed,
            _ => ChatVisualTarget.Stage
        };
    }

    private static bool DependsOn(ScenarioAccess earlier, ScenarioAccess later)
    {
        if (earlier.ModifiesProjectedHistory && later.ReadsProjectedHistory)
            return true;

        if (TagsOverlap(earlier.WrittenTags, earlier.WritesAnyTag, later.ReadTags, later.ReadsAnyTag))
            return true;

        if (TagsOverlap(earlier.WrittenTags, earlier.WritesAnyTag, later.WrittenTags, later.WritesAnyTag))
            return true;

        if (earlier.WritesUntaggedRecord && later.WritesUntaggedRecord)
            return true;

        if (earlier.WritesDraft && (later.ReadsDraft || later.WritesDraft))
            return true;

        if (earlier.WritesAnyVisualTarget && (later.WritesAnyVisualTarget || later.WrittenVisualTargets.Count > 0))
            return true;

        if (later.WritesAnyVisualTarget && earlier.WrittenVisualTargets.Count > 0)
            return true;

        return earlier.WrittenVisualTargets.Overlaps(later.WrittenVisualTargets);
    }

    private static bool TagsOverlap(HashSet<string> first, bool firstAny, HashSet<string> second, bool secondAny)
    {
        if (firstAny && (secondAny || second.Count > 0))
            return true;

        if (secondAny && first.Count > 0)
            return true;

        return first.Overlaps(second);
    }

    private sealed class ScenarioAccess
    {
        public bool ReadsProjectedHistory { get; set; }

        public bool ModifiesProjectedHistory { get; set; }

        public HashSet<string> ReadTags { get; } = [with(StringComparer.Ordinal)];

        public bool ReadsAnyTag { get; set; }

        public HashSet<string> WrittenTags { get; } = [with(StringComparer.Ordinal)];

        public bool WritesAnyTag { get; set; }

        public bool WritesUntaggedRecord { get; set; }

        public bool ReadsDraft { get; set; }

        public bool WritesDraft { get; set; }

        public HashSet<ChatVisualTarget> WrittenVisualTargets { get; } = [];

        public bool WritesAnyVisualTarget { get; set; }
    }
}
