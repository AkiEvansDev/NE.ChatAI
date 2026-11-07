using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using NE.ChatAI.Samplers;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Chats;

/// <summary>Who wrote a message.</summary>
public enum MessageRole
{
    User,
    Character,
    System
}

/// <summary>One of a reply's variants: its words, the thought before them, and how it was made.</summary>
public sealed record MessageVariant
{
    public string Text { get; init; } = string.Empty;

    public string? Thought { get; init; }

    /// <summary>The seed it was generated with; <c>null</c> for a variant not generated — a first message, an edit.</summary>
    public long? Seed { get; init; }

    /// <summary>The id of its generation record in the chat.</summary>
    public string? Generation { get; init; }

    /// <summary>The ids of the generation records that continued it, in the order they ran.</summary>
    public IReadOnlyList<string>? Continuations { get; init; }

    /// <summary>The messages that followed it, kept while another variant is shown; none on the shown one, whose are the chat's own.</summary>
    public IReadOnlyList<ChatMessage>? Tail { get; init; }
}

/// <summary>A message: the reader's words, or a character's reply with its variants and the one chosen.</summary>
public sealed record ChatMessage
{
    public required string Id { get; init; }

    public MessageRole Role { get; init; }

    public IReadOnlyList<MessageVariant> Variants { get; init; } = [];

    public int Chosen { get; init; }

    public DateTimeOffset At { get; init; }

    /// <summary>The variant shown and sent: the chosen one.</summary>
    [JsonIgnore]
    public MessageVariant Current => Variants.Count == 0 ? new MessageVariant() : Variants[Math.Clamp(Chosen, 0, Variants.Count - 1)];
}

/// <summary>Whether a token count came from the model's own tokenizer or is an estimate.</summary>
public enum TokenCounts
{
    Exact,
    Approximate
}

/// <summary>Well-known <see cref="GenerationRecord.Purpose"/> values; a scenario uses its own name.</summary>
public static class GenerationPurpose
{
    public const string Reply = "reply";
    public const string Impersonate = "impersonate";
}

/// <summary>A model call as it was made: the record the charts are drawn from.</summary>
public sealed record GenerationRecord
{
    public required string Id { get; init; }

    public string Purpose { get; init; } = GenerationPurpose.Reply;

    public string? Connection { get; init; }

    public string? Model { get; init; }

    public int ContextLength { get; init; }

    public GenerationSamplers? Samplers { get; init; }

    /// <summary>The prompt's tokens by block: <c>story</c>, <c>history</c>, <c>authorsNote</c> and the rest.</summary>
    public IReadOnlyDictionary<string, int> PromptTokens { get; init; } = new Dictionary<string, int>();

    public int ReplyTokens { get; init; }

    public TokenCounts Counts { get; init; } = TokenCounts.Exact;

    public DateTimeOffset Started { get; init; }

    /// <summary>Seconds from sending the prompt to the first streamed token; <c>null</c> when no token arrived or before this field existed.</summary>
    public double? PromptSeconds { get; init; }

    public double Seconds { get; init; }

    /// <summary>Every token sent in the prompt, across all blocks.</summary>
    [JsonIgnore]
    public int PromptTokenCount
    {
        get
        {
            var total = 0;

            foreach (var tokens in PromptTokens.Values)
                total += tokens;

            return total;
        }
    }

    /// <summary>Seconds spent streaming tokens after the first one arrived; <c>null</c> when <see cref="PromptSeconds"/> is unknown.</summary>
    [JsonIgnore]
    public double? GenerationSeconds => PromptSeconds is double prompt ? Math.Max(0, Seconds - prompt) : null;
}

/// <summary>What a scenario wrote into a chat: sent in place of a message range (<see cref="Replaces"/>) or right after one message (<see cref="After"/>).</summary>
public sealed record ChatRecord
{
    public required string Id { get; init; }

    /// <summary>The id of the scenario document that wrote this record.</summary>
    public string? Scenario { get; init; }

    /// <summary>An optional key; a new record with the same tag replaces the chat's earlier one.</summary>
    public string? Tag { get; init; }

    /// <summary>The first and last message ids of the range sent as this record; <c>null</c> when <see cref="After"/> is set.</summary>
    public IReadOnlyList<string>? Replaces { get; init; }

    /// <summary>The id of the message this record is sent immediately after; <c>null</c> when <see cref="Replaces"/> is set.</summary>
    public string? After { get; init; }

    public MessageRole Role { get; init; } = MessageRole.System;

    public string Text { get; init; } = string.Empty;

    public string? Thought { get; init; }

    /// <summary>The id of the generation record that produced this text, when one did.</summary>
    public string? Generation { get; init; }

    /// <summary>Whether a covered message was edited or deleted since; a stale record is shown as stale and left out of the prompt until remade or edited.</summary>
    public bool Stale { get; init; }
}

/// <summary>A chat with a character: its messages, what scenarios wrote into it, and every model call made for it.</summary>
public sealed record Chat : NamedDocument
{
    public string? Character { get; init; }

    public string? Persona { get; init; }

    /// <summary>The chat's own seed under <see cref="SeedMode.PerChat"/>.</summary>
    public long? Seed { get; init; }

    public string? AuthorsNote { get; init; }

    public string? Background { get; init; }

    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    public IReadOnlyList<ChatRecord> Records { get; init; } = [];

    public IReadOnlyList<GenerationRecord> Generations { get; init; } = [];
}
