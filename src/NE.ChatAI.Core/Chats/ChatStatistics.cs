using System;
using System.Collections.Generic;
using System.Globalization;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;

namespace NE.ChatAI.Chats;

/// <summary>What role a recorded generation played in the chat.</summary>
public enum GenerationCallKind
{
    Reply,
    Continuation,
    Impersonate,
    Auxiliary
}

/// <summary>One non-zero share of a context window: a prompt block or the reply itself.</summary>
public sealed record BlockShare(string Block, int Tokens);

/// <summary>One recorded model call prepared for turn-by-turn charts.</summary>
public sealed record GenerationCallSummary
{
    public required GenerationRecord Record { get; init; }

    public int Index { get; init; }

    public GenerationCallKind Kind { get; init; }

    public string? MessageId { get; init; }

    public required string Label { get; init; }

    public int PromptTokens { get; init; }

    public int ReplyTokens { get; init; }

    public int TotalTokens => PromptTokens + ReplyTokens;

    public double? PromptSeconds { get; init; }

    public double GenerationSeconds { get; init; }

    public double? TokensPerSecond { get; init; }

    public double? PromptTokensPerSecond { get; init; }
}

/// <summary>A message variant's primary generation and its continuations combined for the message's Info view.</summary>
public sealed record MessageGenerationSummary
{
    public required GenerationRecord Primary { get; init; }

    public IReadOnlyList<GenerationRecord> Continuations { get; init; } = [];

    public int ContextLength { get; init; }

    public IReadOnlyList<BlockShare> Shares { get; init; } = [];

    public int PromptTokenCount { get; init; }

    public int ReplyTokens { get; init; }

    public int TotalTokens => PromptTokenCount + ReplyTokens;

    public TokenCounts Counts { get; init; }

    public double? PromptSeconds { get; init; }

    public double? GenerationSeconds { get; init; }

    public double Seconds { get; init; }

    public double? TokensPerSecond { get; init; }

    public double? PromptTokensPerSecond { get; init; }

    public long? Seed { get; init; }

    public string? Model { get; init; }

    public string? Connection { get; init; }

    public GenerationSamplers? Samplers { get; init; }
}

/// <summary>
/// Selects and aggregates <see cref="GenerationRecord"/>s for the chat's charts and a message's Info dialog: either the calls along the
/// active branch (chosen variants, their continuations, and auxiliary scenario runs on that timeline) or every recorded call.
/// </summary>
public static class ChatStatistics
{
    public const string ReplyBlock = "reply";
    public const string ExtraBlock = "extra";

    private const double MinSpeedWindowSeconds = 0.01;
    private const double MinPromptSpeedWindowSeconds = 0.004;

    /// <summary>The prompt blocks in the order <see cref="PromptBuilder"/> assembles them.</summary>
    public static IReadOnlyList<string> OrderedPromptBlocks { get; } =
    [
        PromptBlocks.System,
        PromptBlocks.Description,
        PromptBlocks.Personality,
        PromptBlocks.Scenario,
        PromptBlocks.Persona,
        PromptBlocks.Lore,
        PromptBlocks.Examples,
        PromptBlocks.Story,
        PromptBlocks.AuthorsNote,
        PromptBlocks.History,
        PromptBlocks.PostHistory,
        PromptBlocks.Instruction,
        PromptBlocks.Impersonate,
        PromptBlocks.Opening
    ];

    private static readonly HashSet<string> KnownBlockSet = new(OrderedPromptBlocks, StringComparer.Ordinal);

    /// <summary>The calls in scope — the active branch's or all of them — numbered and measured for the charts.</summary>
    public static IReadOnlyList<GenerationCallSummary> SelectCalls(Chat chat, bool allCalls)
    {
        ArgumentNullException.ThrowIfNull(chat);

        if (chat.Generations.Count == 0)
            return [];

        HashSet<string> activeIds = [];
        HashSet<string> activeRecordIds = [];
        HashSet<string> inactiveRecordIds = [];
        HashSet<string> continuationIds = [];
        Dictionary<string, string> messageByGeneration = [with(StringComparer.Ordinal)];

        CollectActive(chat.Messages, activeIds, messageByGeneration);
        CollectAll(chat.Messages, continuationIds, messageByGeneration);
        CollectRecordGenerations(chat.Messages, chat.Records, activeRecordIds, inactiveRecordIds, messageByGeneration);

        var lastMessageGenIndex = -1;

        for (var i = 0; i < chat.Generations.Count; i++)
        {
            if (messageByGeneration.ContainsKey(chat.Generations[i].Id))
                lastMessageGenIndex = i;
        }

        List<GenerationCallSummary> selected = [with(chat.Generations.Count)];
        var lastTurnActive = chat.Messages.Count > 0;

        for (var i = 0; i < chat.Generations.Count; i++)
        {
            GenerationRecord record = chat.Generations[i];
            GenerationCallKind kind = KindOf(record, continuationIds);
            var include = allCalls;

            if (kind == GenerationCallKind.Auxiliary)
            {
                include |= activeRecordIds.Contains(record.Id) || (!inactiveRecordIds.Contains(record.Id) && lastTurnActive);
            }
            else if (kind == GenerationCallKind.Impersonate && !messageByGeneration.ContainsKey(record.Id) && i > lastMessageGenIndex)
            {
                include |= lastTurnActive;
            }
            else
            {
                var isActive = activeIds.Contains(record.Id);
                lastTurnActive = isActive;
                include |= isActive;
            }

            if (!include)
                continue;

            var index = selected.Count + 1;
            _ = messageByGeneration.TryGetValue(record.Id, out var messageId);
            selected.Add(SummarizeCall(record, index, kind, messageId));
        }

        return selected;
    }

    private static void CollectActive(IReadOnlyList<ChatMessage> messages, HashSet<string> activeIds, Dictionary<string, string> messageByGeneration)
    {
        foreach (ChatMessage message in messages)
        {
            MessageVariant current = message.Current;

            if (current.Generation is string generationId)
            {
                _ = activeIds.Add(generationId);
                messageByGeneration[generationId] = message.Id;
            }

            if (current.Continuations is { Count: > 0 } continuations)
            {
                foreach (var continuationId in continuations)
                {
                    _ = activeIds.Add(continuationId);
                    messageByGeneration[continuationId] = message.Id;
                }
            }
        }
    }

    private static void CollectAll(IReadOnlyList<ChatMessage> messages, HashSet<string> continuationIds, Dictionary<string, string> messageByGeneration)
    {
        foreach (ChatMessage message in messages)
        {
            foreach (MessageVariant variant in message.Variants)
            {
                if (variant.Generation is string generationId)
                    _ = messageByGeneration.TryAdd(generationId, message.Id);

                if (variant.Continuations is { Count: > 0 } continuations)
                {
                    foreach (var continuationId in continuations)
                    {
                        _ = continuationIds.Add(continuationId);
                        _ = messageByGeneration.TryAdd(continuationId, message.Id);
                    }
                }

                if (variant.Tail is { Count: > 0 } tail)
                    CollectAll(tail, continuationIds, messageByGeneration);
            }
        }
    }

    private static void CollectRecordGenerations(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatRecord> records, HashSet<string> activeIds, HashSet<string> inactiveIds, Dictionary<string, string> messageByGeneration)
    {
        foreach (ChatRecord record in records)
        {
            if (record.Generation is not string generationId)
                continue;

            if (ChatRecords.AnchorMessageId(record, messages) is string anchorId)
            {
                _ = activeIds.Add(generationId);
                _ = messageByGeneration.TryAdd(generationId, anchorId);
            }
            else
            {
                _ = inactiveIds.Add(generationId);
            }
        }
    }

    private static GenerationCallKind KindOf(GenerationRecord record, HashSet<string> continuationIds)
    {
        if (continuationIds.Contains(record.Id))
            return GenerationCallKind.Continuation;

        if (string.Equals(record.Purpose, GenerationPurpose.Impersonate, StringComparison.Ordinal))
            return GenerationCallKind.Impersonate;

        return string.IsNullOrWhiteSpace(record.Purpose) || string.Equals(record.Purpose, GenerationPurpose.Reply, StringComparison.Ordinal)
            ? GenerationCallKind.Reply
            : GenerationCallKind.Auxiliary;
    }

    private static GenerationCallSummary SummarizeCall(GenerationRecord record, int index, GenerationCallKind kind, string? messageId)
    {
        var promptTokens = record.PromptTokenCount;
        var replyTokens = Math.Max(0, record.ReplyTokens);
        var seconds = Math.Max(0, record.Seconds);
        double? promptSeconds = record.PromptSeconds is double ttft && ttft >= 0 ? ttft : null;
        var generationSeconds = promptSeconds is double prompt ? Math.Max(0, seconds - prompt) : seconds;

        return new GenerationCallSummary
        {
            Record = record,
            Index = index,
            Kind = kind,
            MessageId = messageId,
            Label = FormatLabel(index, kind, record.Purpose, record.Counts),
            PromptTokens = promptTokens,
            ReplyTokens = replyTokens,
            PromptSeconds = promptSeconds,
            GenerationSeconds = generationSeconds,
            TokensPerSecond = ComputeSpeed(replyTokens, generationSeconds, MinSpeedWindowSeconds),
            PromptTokensPerSecond = promptSeconds is double knownPrompt ? ComputeSpeed(promptTokens, knownPrompt, MinPromptSpeedWindowSeconds) : null
        };
    }

    private static string FormatLabel(int index, GenerationCallKind kind, string purpose, TokenCounts counts)
    {
        var mark = kind switch
        {
            GenerationCallKind.Continuation => "+",
            GenerationCallKind.Impersonate => "*",
            _ => string.Empty
        };
        var approx = counts == TokenCounts.Approximate ? "~" : string.Empty;
        var head = string.Create(CultureInfo.InvariantCulture, $"#{index}{mark}{approx}");

        return kind == GenerationCallKind.Auxiliary && !string.IsNullOrWhiteSpace(purpose)
            ? $"{head} · {purpose.Trim()}"
            : head;
    }

    private static double? ComputeSpeed(int tokens, double seconds, double minSeconds)
        => tokens > 0 && seconds >= minSeconds ? tokens / seconds : null;

    /// <summary>Whether <paramref name="message"/>'s shown variant links to a generation record present in <paramref name="chat"/>.</summary>
    public static bool HasGeneration(Chat chat, ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(message);

        if (message.Current.Generation is not string generationId)
            return false;

        foreach (GenerationRecord record in chat.Generations)
        {
            if (string.Equals(record.Id, generationId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>The shown variant's generation and continuations combined, or <c>null</c> when the message has no recorded generation.</summary>
    public static MessageGenerationSummary? ForMessage(Chat chat, string messageId)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var at = ChatEditing.IndexOf(chat.Messages, messageId);

        if (at < 0)
            return null;

        MessageVariant variant = chat.Messages[at].Current;

        if (variant.Generation is not string primaryId)
            return null;

        Dictionary<string, GenerationRecord> byId = new(chat.Generations.Count, StringComparer.Ordinal);

        foreach (GenerationRecord record in chat.Generations)
            byId[record.Id] = record;

        if (!byId.TryGetValue(primaryId, out GenerationRecord? primary))
            return null;

        List<GenerationRecord> continuations = [];

        if (variant.Continuations is { Count: > 0 } ids)
        {
            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out GenerationRecord? continuation))
                    continuations.Add(continuation);
            }
        }

        var promptTokens = primary.PromptTokenCount;
        var replyTokens = Math.Max(0, primary.ReplyTokens);
        TokenCounts counts = primary.Counts;
        var seconds = Math.Max(0, primary.Seconds);
        var allHavePromptSeconds = primary.PromptSeconds is >= 0;
        var totalPromptSeconds = primary.PromptSeconds ?? 0;
        var contextLength = primary.ContextLength;
        var model = primary.Model;

        foreach (GenerationRecord continuation in continuations)
        {
            replyTokens += Math.Max(0, continuation.ReplyTokens);

            if (continuation.Counts == TokenCounts.Approximate)
                counts = TokenCounts.Approximate;

            seconds += Math.Max(0, continuation.Seconds);

            if (continuation.PromptSeconds is double contPrompt && contPrompt >= 0)
                totalPromptSeconds += contPrompt;
            else
                allHavePromptSeconds = false;

            if (continuation.ContextLength > 0)
                contextLength = continuation.ContextLength;

            if (!string.IsNullOrWhiteSpace(continuation.Model))
                model = continuation.Model;
        }

        double? promptSeconds = allHavePromptSeconds ? totalPromptSeconds : null;
        double? generationSeconds = promptSeconds is double known ? Math.Max(0, seconds - known) : null;
        var decodeWindow = generationSeconds ?? seconds;

        return new MessageGenerationSummary
        {
            Primary = primary,
            Continuations = continuations,
            ContextLength = contextLength,
            Shares = SharesOf(primary.PromptTokens, replyTokens),
            PromptTokenCount = promptTokens,
            ReplyTokens = replyTokens,
            Counts = counts,
            PromptSeconds = promptSeconds,
            GenerationSeconds = generationSeconds,
            Seconds = seconds,
            TokensPerSecond = ComputeSpeed(replyTokens, decodeWindow, MinSpeedWindowSeconds),
            PromptTokensPerSecond = continuations.Count == 0 && primary.PromptSeconds is double firstPrompt && firstPrompt >= 0
                ? ComputeSpeed(promptTokens, firstPrompt, MinPromptSpeedWindowSeconds)
                : null,
            Seed = variant.Seed ?? primary.Samplers?.Seed,
            Model = model,
            Connection = primary.Connection,
            Samplers = primary.Samplers
        };
    }

    /// <summary>The non-zero parts of a context window, in prompt order, followed by any extra keys and the reply.</summary>
    public static IReadOnlyList<BlockShare> SharesOf(IReadOnlyDictionary<string, int> promptTokens, int replyTokens)
    {
        ArgumentNullException.ThrowIfNull(promptTokens);

        List<BlockShare> shares = [];

        foreach (var block in OrderedPromptBlocks)
        {
            if (promptTokens.TryGetValue(block, out var tokens) && tokens > 0)
                shares.Add(new BlockShare(block, tokens));
        }

        var extra = 0;

        foreach (KeyValuePair<string, int> pair in promptTokens)
        {
            if (pair.Value > 0 && !KnownBlockSet.Contains(pair.Key))
                extra += pair.Value;
        }

        if (extra > 0)
            shares.Add(new BlockShare(ExtraBlock, extra));

        if (replyTokens > 0)
            shares.Add(new BlockShare(ReplyBlock, replyTokens));

        return shares;
    }
}
