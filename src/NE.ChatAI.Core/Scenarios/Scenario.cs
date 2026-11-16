using System.Text.Json;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Scenarios;

/// <summary>When an enabled scenario runs automatically.</summary>
public enum ScenarioTriggerKind
{
    /// <summary>Runs only when started by hand from the chat's menu or the canvas.</summary>
    Manual,

    /// <summary>Runs on a non-empty composer draft when Send is pressed, before the message enters the chat.</summary>
    BeforeSend,

    /// <summary>Runs once every finished reply is written.</summary>
    AfterReply,

    /// <summary>Runs once the prompt's share of the context crosses <see cref="ScenarioTrigger.Share"/> from below.</summary>
    ContextShare
}

/// <summary>The rule that starts an enabled scenario automatically.</summary>
public sealed record ScenarioTrigger
{
    public ScenarioTriggerKind Kind { get; init; } = ScenarioTriggerKind.Manual;

    /// <summary>The share of the context (<c>0..1</c>) that starts a <see cref="ScenarioTriggerKind.ContextShare"/> scenario.</summary>
    public double Share { get; init; } = 0.5;

    /// <summary>Whether this trigger holds after a reply moved the prompt's context share from <paramref name="previousShare"/> to <paramref name="currentShare"/>.</summary>
    public bool FiresAfterReply(double previousShare, double currentShare)
        => Kind switch
        {
            ScenarioTriggerKind.AfterReply => true,
            ScenarioTriggerKind.ContextShare => previousShare < Share && currentShare >= Share,
            _ => false
        };
}

/// <summary>A node sheet the auxiliary model runs on a chat, kept under <c>graphs/&lt;id&gt;.json</c>.</summary>
public sealed record Scenario : NamedDocument
{
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
}
