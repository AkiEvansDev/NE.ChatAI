using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Scenarios;

/// <summary>When an enabled scenario runs automatically.</summary>
public enum ScenarioTriggerKind
{
    /// <summary>Runs only when started by hand from the chat's menu or the canvas.</summary>
    Manual,

    /// <summary>Runs on a non-empty composer draft when Send is pressed, before the message enters the chat.</summary>
    BeforeSend,

    /// <summary>Runs after the user's message stands at the end of the chat, before the model writes its reply.</summary>
    BeforeReply,

    /// <summary>Runs once every finished reply is written.</summary>
    AfterReply
}

/// <summary>Optional probability and turn-pacing limits checked before an automatic scenario runs.</summary>
public sealed record ScenarioGate
{
    public const int DefaultChance = 100;
    public const int MaxTurns = 64;

    public bool Enabled { get; init; }

    /// <summary>Probability (<c>1..100%</c>) of running when triggered.</summary>
    public int Chance { get; init; } = DefaultChance;

    /// <summary>User turns along the active branch to skip after this scenario writes to the chat.</summary>
    public int Cooldown { get; init; }

    /// <summary>Minimum user turns along the active branch before this scenario can run.</summary>
    public int MinTurns { get; init; }

    [JsonIgnore]
    public bool IsDefault => !Enabled && Chance == DefaultChance && Cooldown == 0 && MinTurns == 0;
}

/// <summary>The lifecycle phase and optional pacing gate that start an enabled scenario automatically.</summary>
[JsonConverter(typeof(ScenarioTriggerJsonConverter))]
public sealed record ScenarioTrigger
{
    public ScenarioTriggerKind Kind { get; init; } = ScenarioTriggerKind.Manual;

    public ScenarioGate Gate { get; init; } = new();

    /// <summary>Context share (<c>0.05..0.95</c>) carried from a legacy <c>contextShare</c> trigger until its sheet is upgraded to <c>chat.context</c> -&gt; <c>When</c>.</summary>
    [JsonIgnore]
    public double? LegacyContextShare { get; init; }
}

internal sealed class ScenarioTriggerJsonConverter : JsonConverter<ScenarioTrigger>
{
    public override ScenarioTrigger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a JSON object for ScenarioTrigger.");

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        var kindText = root.TryGetProperty("kind", out JsonElement kindProp) && kindProp.ValueKind == JsonValueKind.String
            ? kindProp.GetString()
            : null;
        ScenarioGate gate = ReadGate(root);

        if (string.Equals(kindText, "contextShare", StringComparison.OrdinalIgnoreCase))
        {
            var share = root.TryGetProperty("share", out JsonElement shareProp) && shareProp.TryGetDouble(out var parsedShare)
                ? Math.Clamp(parsedShare, 0.05, 0.95)
                : 0.5;

            return new ScenarioTrigger
            {
                Kind = ScenarioTriggerKind.AfterReply,
                Gate = gate,
                LegacyContextShare = share
            };
        }

        ScenarioTriggerKind kind = kindText is not null && Enum.TryParse(kindText, ignoreCase: true, out ScenarioTriggerKind parsed) && Enum.IsDefined(parsed)
            ? parsed
            : ScenarioTriggerKind.Manual;

        return new ScenarioTrigger { Kind = kind, Gate = gate };
    }

    private static ScenarioGate ReadGate(JsonElement root)
    {
        if (!root.TryGetProperty("gate", out JsonElement gateProp) || gateProp.ValueKind != JsonValueKind.Object)
            return new ScenarioGate();

        var enabled = gateProp.TryGetProperty("enabled", out JsonElement enabledProp) && enabledProp.ValueKind is JsonValueKind.True or JsonValueKind.False
            && enabledProp.GetBoolean();
        var chance = gateProp.TryGetProperty("chance", out JsonElement chanceProp) && chanceProp.TryGetInt32(out var parsedChance)
            ? Math.Clamp(parsedChance, 1, ScenarioGate.DefaultChance)
            : ScenarioGate.DefaultChance;
        var cooldown = gateProp.TryGetProperty("cooldown", out JsonElement cooldownProp) && cooldownProp.TryGetInt32(out var parsedCooldown)
            ? Math.Clamp(parsedCooldown, 0, ScenarioGate.MaxTurns)
            : 0;
        var minTurns = gateProp.TryGetProperty("minTurns", out JsonElement minTurnsProp) && minTurnsProp.TryGetInt32(out var parsedMinTurns)
            ? Math.Clamp(parsedMinTurns, 0, ScenarioGate.MaxTurns)
            : 0;

        return new ScenarioGate
        {
            Enabled = enabled,
            Chance = chance,
            Cooldown = cooldown,
            MinTurns = minTurns
        };
    }

    public override void Write(Utf8JsonWriter writer, ScenarioTrigger value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        var kindName = value.Kind switch
        {
            ScenarioTriggerKind.BeforeSend => "beforeSend",
            ScenarioTriggerKind.BeforeReply => "beforeReply",
            ScenarioTriggerKind.AfterReply => "afterReply",
            _ => "manual"
        };

        writer.WriteStartObject();
        writer.WriteString("kind", kindName);

        if (value.Kind != ScenarioTriggerKind.Manual && !value.Gate.IsDefault)
        {
            writer.WritePropertyName("gate");
            writer.WriteStartObject();
            writer.WriteBoolean("enabled", value.Gate.Enabled);
            writer.WriteNumber("chance", Math.Clamp(value.Gate.Chance, 1, ScenarioGate.DefaultChance));
            writer.WriteNumber("cooldown", Math.Clamp(value.Gate.Cooldown, 0, ScenarioGate.MaxTurns));
            writer.WriteNumber("minTurns", Math.Clamp(value.Gate.MinTurns, 0, ScenarioGate.MaxTurns));
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}

/// <summary>A node sheet the auxiliary model runs on a chat, kept under <c>graphs/&lt;id&gt;.json</c>.</summary>
public sealed record Scenario : NamedDocument
{
    private const string ContextNodeKey = "chat.context";
    private const string LegacyGateNodeKey = "chat.gate";
    private const string ModelCallNodeKey = "chat.model-call";

    private static readonly string[] WriterNodeKeys =
    [
        "chat.replace",
        "chat.insert",
        "chat.draft",
        "chat.visual"
    ];

    /// <summary>Whether the scenario is a built-in action invoked from its own menu item rather than listed under Scenarios.</summary>
    public bool System { get; init; }

    /// <summary>Whether the scenario runs on its trigger across every chat.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether the scenario also stands directly in the chat's root extras menu.</summary>
    public bool Pinned { get; init; }

    /// <summary>Where the scenario stands among the others when several run in one batch.</summary>
    public int Order { get; init; }

    public ScenarioTrigger Trigger { get; init; } = new();

    /// <summary>The node canvas document as raw JSON, so <c>Core</c> carries no UI graph dependency.</summary>
    public JsonElement? Sheet { get; init; }

    /// <summary>Upgrades a scenario read with a legacy <c>contextShare</c> trigger or retired <c>chat.gate</c> node into <c>Trigger.Gate</c> and <c>When</c> edges.</summary>
    public Scenario UpgradeLegacyTrigger()
    {
        var hasLegacyGateNode = Sheet is JsonElement sheetElement && HasNodeOfType(sheetElement, LegacyGateNodeKey);

        if (Trigger.LegacyContextShare is null && !hasLegacyGateNode)
            return this;

        var rawSheet = Sheet is JsonElement element && element.ValueKind == JsonValueKind.Object ? element.GetRawText() : null;
        JsonObject sheet = rawSheet is not null && JsonNode.Parse(rawSheet) is JsonObject parsed
            ? parsed
            : [];

        JsonArray nodes = sheet["nodes"] as JsonArray ?? [];
        JsonArray edges = sheet["edges"] as JsonArray ?? [];
        sheet["nodes"] = nodes;
        sheet["edges"] = edges;
        sheet["groups"] ??= new JsonArray();
        sheet["parameters"] ??= new JsonArray();

        ScenarioGate upgradedGate = Trigger.Gate;

        if (hasLegacyGateNode)
            upgradedGate = MigrateGateNodes(nodes, edges, upgradedGate);

        if (Trigger.LegacyContextShare is double share)
            AddContextWhenEdges(nodes, edges, share);

        return this with
        {
            Trigger = new ScenarioTrigger
            {
                Kind = Trigger.LegacyContextShare is not null ? ScenarioTriggerKind.AfterReply : Trigger.Kind,
                Gate = upgradedGate
            },
            Sheet = JsonSerializer.SerializeToElement(sheet, DocumentJson.Options)
        };
    }

    private static bool HasNodeOfType(JsonElement sheet, string nodeType)
    {
        if (sheet.ValueKind != JsonValueKind.Object || !sheet.TryGetProperty("nodes", out JsonElement nodes) || nodes.ValueKind != JsonValueKind.Array)
            return false;

        foreach (JsonElement node in nodes.EnumerateArray())
        {
            if (node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty("type", out JsonElement typeProp)
                && typeProp.ValueKind == JsonValueKind.String
                && string.Equals(typeProp.GetString(), nodeType, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static ScenarioGate MigrateGateNodes(JsonArray nodes, JsonArray edges, ScenarioGate currentGate)
    {
        ScenarioGate migrated = currentGate;
        HashSet<string> gateIds = [with(StringComparer.Ordinal)];
        List<(string FromNode, string FromPin)> conditions = [];

        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i] is not JsonObject node || !string.Equals((string?)node["type"], LegacyGateNodeKey, StringComparison.Ordinal))
                continue;

            var id = (string?)node["id"];
            var nodeEnabled = node["enabled"] is not JsonNode enabledNode || enabledNode.GetValue<bool>();

            if (!string.IsNullOrEmpty(id))
                _ = gateIds.Add(id);

            if (nodeEnabled && node["values"] is JsonObject values)
            {
                var chance = ReadInt(values, "Chance", ScenarioGate.DefaultChance, 1, ScenarioGate.DefaultChance);
                var cooldown = ReadInt(values, "Cooldown", 0, 0, ScenarioGate.MaxTurns);
                var minTurns = ReadInt(values, "MinTurns", 0, 0, ScenarioGate.MaxTurns);

                if (chance < ScenarioGate.DefaultChance || cooldown > 0 || minTurns > 0)
                {
                    migrated = new ScenarioGate
                    {
                        Enabled = true,
                        Chance = chance,
                        Cooldown = cooldown,
                        MinTurns = minTurns
                    };
                }
            }

            nodes.RemoveAt(i);
        }

        for (var i = edges.Count - 1; i >= 0; i--)
        {
            if (edges[i] is not JsonObject edge)
                continue;

            var fromNode = (string?)edge["fromNode"];
            var fromPin = (string?)edge["fromPin"];
            var toNode = (string?)edge["toNode"];
            var toPin = (string?)edge["toPin"];

            if (toNode is not null && gateIds.Contains(toNode))
            {
                if (string.Equals(toPin, "Condition", StringComparison.Ordinal) && !string.IsNullOrEmpty(fromNode) && !string.IsNullOrEmpty(fromPin))
                    conditions.Add((fromNode, fromPin));

                edges.RemoveAt(i);
            }
            else if (fromNode is not null && gateIds.Contains(fromNode))
            {
                edges.RemoveAt(i);
            }
        }

        for (var i = 0; i < conditions.Count; i++)
            WireConditionToTargets(nodes, edges, conditions[i].FromNode, conditions[i].FromPin);

        return migrated;
    }

    private static int ReadInt(JsonObject values, string key, int fallback, int min, int max)
    {
        if (values[key] is not JsonNode node)
            return fallback;

        try
        {
            return Math.Clamp(node.GetValue<int>(), min, max);
        }
        catch (FormatException)
        {
            return fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static void WireConditionToTargets(JsonArray nodes, JsonArray edges, string fromNode, string fromPin)
    {
        List<string> targets = CollectEnabledTargets(nodes, [ModelCallNodeKey]);

        if (targets.Count == 0)
            targets = CollectEnabledTargets(nodes, WriterNodeKeys);

        for (var i = 0; i < targets.Count; i++)
        {
            var targetId = targets[i];

            if (HasIncomingPin(edges, targetId, "When"))
                continue;

            edges.Add(new JsonObject
            {
                ["id"] = NextUniqueId(edges, "e-when"),
                ["fromNode"] = fromNode,
                ["fromPin"] = fromPin,
                ["toNode"] = targetId,
                ["toPin"] = "When"
            });
        }
    }

    private static List<string> CollectEnabledTargets(JsonArray nodes, string[] types)
    {
        List<string> ids = [];

        foreach (JsonNode? item in nodes)
        {
            if (item is not JsonObject node)
                continue;

            if (node["enabled"] is JsonNode enabled && !enabled.GetValue<bool>())
                continue;

            var type = (string?)node["type"];
            var id = (string?)node["id"];

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type))
                continue;

            for (var i = 0; i < types.Length; i++)
            {
                if (string.Equals(type, types[i], StringComparison.Ordinal))
                {
                    ids.Add(id);
                    break;
                }
            }
        }

        return ids;
    }

    private static bool HasIncomingPin(JsonArray edges, string toNode, string toPin)
    {
        foreach (JsonNode? item in edges)
        {
            if (item is JsonObject edge
                && string.Equals((string?)edge["toNode"], toNode, StringComparison.Ordinal)
                && string.Equals((string?)edge["toPin"], toPin, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NextUniqueId(JsonArray items, string baseId)
    {
        if (!ContainsId(items, baseId))
            return baseId;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";

            if (!ContainsId(items, candidate))
                return candidate;
        }
    }

    private static bool ContainsId(JsonArray items, string id)
    {
        foreach (JsonNode? item in items)
        {
            if (item is JsonObject obj && string.Equals((string?)obj["id"], id, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void AddContextWhenEdges(JsonArray nodes, JsonArray edges, double share)
    {
        var contextId = FindEnabledNodeId(nodes, ContextNodeKey);

        if (contextId is null)
        {
            (var minX, var minY, var hasNode) = FindTopLeft(nodes);
            contextId = NextUniqueId(nodes, "context");
            var threshold = Math.Clamp((int)Math.Round(share * 100.0), 5, 95);

            nodes.Insert(0, new JsonObject
            {
                ["id"] = contextId,
                ["type"] = ContextNodeKey,
                ["x"] = hasNode ? minX : 0.0,
                ["y"] = hasNode ? minY - 288.0 : 0.0,
                ["values"] = new JsonObject
                {
                    ["Threshold"] = threshold
                }
            });
        }

        WireConditionToTargets(nodes, edges, contextId, "Crossed");
    }

    private static string? FindEnabledNodeId(JsonArray nodes, string nodeType)
    {
        foreach (JsonNode? item in nodes)
        {
            if (item is not JsonObject node || !string.Equals((string?)node["type"], nodeType, StringComparison.Ordinal))
                continue;

            if (node["enabled"] is not JsonNode enabled || enabled.GetValue<bool>())
                return (string?)node["id"];
        }

        return null;
    }

    private static (double MinX, double MinY, bool HasNode) FindTopLeft(JsonArray nodes)
    {
        var minX = 0.0;
        var minY = 0.0;
        var hasNode = false;

        foreach (JsonNode? item in nodes)
        {
            if (item is not JsonObject existing)
                continue;

            var nx = existing["x"]?.GetValue<double>() ?? 0.0;
            var ny = existing["y"]?.GetValue<double>() ?? 0.0;

            if (!hasNode || nx < minX)
                minX = nx;

            if (!hasNode || ny < minY)
                minY = ny;

            hasNode = true;
        }

        return (minX, minY, hasNode);
    }
}
