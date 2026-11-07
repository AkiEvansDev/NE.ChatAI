using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>One non-zero part of a context window on a <see cref="ShareBarComponent"/>.</summary>
internal sealed partial class ContextShareItem : RecursiveObservable, IBindableItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    [RecursiveMember]
    public partial string BlockKey { get; set; } = string.Empty;

    [RecursiveMember]
    public partial int Tokens { get; set; }
}

/// <summary>One recorded model call across the chat's turn-by-turn charts: its prompt blocks, reply tokens, speed and timing.</summary>
internal sealed partial class TurnChartItem : RecursiveObservable, IBindableItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    [RecursiveMember]
    public partial string Turn { get; set; } = string.Empty;

    [RecursiveMember]
    public partial int? System { get; set; }

    [RecursiveMember]
    public partial int? Description { get; set; }

    [RecursiveMember]
    public partial int? Personality { get; set; }

    [RecursiveMember]
    public partial int? Scenario { get; set; }

    [RecursiveMember]
    public partial int? Persona { get; set; }

    [RecursiveMember]
    public partial int? Lore { get; set; }

    [RecursiveMember]
    public partial int? Examples { get; set; }

    [RecursiveMember]
    public partial int? Story { get; set; }

    [RecursiveMember]
    public partial int? AuthorsNote { get; set; }

    [RecursiveMember]
    public partial int? History { get; set; }

    [RecursiveMember]
    public partial int? PostHistory { get; set; }

    [RecursiveMember]
    public partial int? Instruction { get; set; }

    [RecursiveMember]
    public partial int? Impersonate { get; set; }

    [RecursiveMember]
    public partial int? Opening { get; set; }

    [RecursiveMember]
    public partial int? Reply { get; set; }

    [RecursiveMember]
    public partial double? TokensPerSecond { get; set; }

    [RecursiveMember]
    public partial double? PromptSeconds { get; set; }

    [RecursiveMember]
    public partial double? GenerationSeconds { get; set; }
}

/// <summary>
/// The chat's statistics dialog and a message's Info dialog: context fill, tokens per turn and prompt block, speed and timing, and
/// the snapshot of samplers and counts (always marked exact or approximate).
/// </summary>
internal sealed partial class ChatController
{
    public const string StatisticsDialogKey = "chatai-statistics";
    public const string MessageInfoDialogKey = "chatai-message-info";

    public const string StatisticsAction = "statistics";
    public const string InfoAction = "info";

    public const string ActiveScope = "active";
    public const string AllScope = "all";

    private bool _statisticsLoaded;

    [RecursiveMember]
    public partial string StatisticsScope { get; set; } = ActiveScope;

    [RecursiveMember]
    public partial UIVisibility StatisticsEmptyVisibility { get; set; } = UIVisibility.Visible;

    [RecursiveMember]
    public partial UIVisibility StatisticsContentVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember(false)]
    public RecursiveCollection<KeyValueActionItem> StatisticsSummary { get; } = [];

    [RecursiveMember]
    public partial UIPhrase? LatestContextTitle { get; set; }

    [RecursiveMember]
    public partial double? LatestContextTotal { get; set; }

    [RecursiveMember(false)]
    public RecursiveCollection<ContextShareItem> LatestContextShares { get; } = [];

    [RecursiveMember(false)]
    public RecursiveCollection<TurnChartItem> TurnChartRows { get; } = [];

    [RecursiveMember]
    public partial UIPhrase? MessageInfoContextTitle { get; set; }

    [RecursiveMember]
    public partial double? MessageInfoContextTotal { get; set; }

    [RecursiveMember(false)]
    public RecursiveCollection<ContextShareItem> MessageInfoShares { get; } = [];

    [RecursiveMember(false)]
    public RecursiveCollection<KeyValueActionItem> MessageInfoDetails { get; } = [];

    [RecursiveMember]
    public partial UIVisibility MessageInfoSamplersVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember(false)]
    public RecursiveCollection<KeyValueActionItem> MessageInfoSamplers { get; } = [];

    /// <summary>Switches between the active branch's generations and every recorded call in the chat.</summary>
    [UICommand]
    public async Task SwitchStatisticsScopeAsync(string scope, CancellationToken cancellationToken)
    {
        var normalized = scope == AllScope ? AllScope : ActiveScope;

        await Context.Runtime.InvokeAsync(() =>
        {
            StatisticsScope = normalized;
            _statisticsLoaded = true;
            BuildChatStatistics();
        }, cancellationToken).ConfigureAwait(false);
    }

    private void BuildChatStatistics()
    {
        StatisticsSummary.Clear();
        LatestContextShares.Clear();
        TurnChartRows.Clear();

        if (_chat is null)
        {
            LatestContextTitle = null;
            LatestContextTotal = null;
            StatisticsEmptyVisibility = UIVisibility.Visible;
            StatisticsContentVisibility = UIVisibility.Collapsed;
            return;
        }

        IReadOnlyList<GenerationCallSummary> calls = ChatStatistics.SelectCalls(_chat, StatisticsScope == AllScope);

        if (calls.Count == 0)
        {
            LatestContextTitle = null;
            LatestContextTotal = null;
            StatisticsEmptyVisibility = UIVisibility.Visible;
            StatisticsContentVisibility = UIVisibility.Collapsed;
            return;
        }

        StatisticsEmptyVisibility = UIVisibility.Collapsed;
        StatisticsContentVisibility = UIVisibility.Visible;

        var exactCount = 0;
        var approximateCount = 0;
        var totalPromptTokens = 0;
        var totalReplyTokens = 0;
        var speedTokens = 0;
        var speedSeconds = 0d;

        foreach (GenerationCallSummary call in calls)
        {
            if (call.Record.Counts == TokenCounts.Exact)
                exactCount++;
            else
                approximateCount++;

            totalPromptTokens += call.PromptTokens;
            totalReplyTokens += call.ReplyTokens;

            if (call.TokensPerSecond is not null)
            {
                speedTokens += call.ReplyTokens;
                speedSeconds += call.GenerationSeconds;
            }

            TurnChartRows.Add(ToTurnRow(call));
        }

        StatisticsSummary.Add(UIDetails.Row("calls", "chatai.stats.calls", UIPhrase.Of("chatai.stats.tokens.count", ("tokens", calls.Count))));
        StatisticsSummary.Add(UIDetails.Row("counts", "chatai.stats.counts", CountsPhrase(exactCount, approximateCount)));
        StatisticsSummary.Add(UIDetails.Row("tokens", "chatai.stats.tokens.total", UIPhrase.Of("chatai.stats.tokens.pair", ("prompt", totalPromptTokens), ("reply", totalReplyTokens))));

        if (speedTokens > 0 && speedSeconds > 0)
            StatisticsSummary.Add(UIDetails.Row("speed", "chatai.stats.speed.average", UIPhrase.Of("chatai.stats.speed.value", ("speed", Math.Round(speedTokens / speedSeconds, 1)))));

        GenerationCallSummary latest = calls[^1];
        IReadOnlyList<BlockShare> shares = ChatStatistics.SharesOf(latest.Record.PromptTokens, latest.ReplyTokens);

        FillShares(LatestContextShares, shares);
        LatestContextTotal = latest.Record.ContextLength > 0 ? latest.Record.ContextLength : null;
        LatestContextTitle = latest.Record.ContextLength > 0
            ? UIPhrase.Of("chatai.stats.context.latest", ("turn", latest.Label), ("used", latest.TotalTokens), ("context", latest.Record.ContextLength), ("counts", ShortCountsPhrase(latest.Record.Counts)))
            : UIPhrase.Of("chatai.stats.context.latest.unbounded", ("turn", latest.Label), ("used", latest.TotalTokens), ("counts", ShortCountsPhrase(latest.Record.Counts)));
    }

    private static TurnChartItem ToTurnRow(GenerationCallSummary call)
    {
        IReadOnlyDictionary<string, int> blocks = call.Record.PromptTokens;
        var story = BlockTokens(blocks, PromptBlocks.Story) + ExtraBlockTokens(blocks);

        return new TurnChartItem
        {
            Id = call.Record.Id,
            Turn = call.Label,
            System = NullableTokens(BlockTokens(blocks, PromptBlocks.System)),
            Description = NullableTokens(BlockTokens(blocks, PromptBlocks.Description)),
            Personality = NullableTokens(BlockTokens(blocks, PromptBlocks.Personality)),
            Scenario = NullableTokens(BlockTokens(blocks, PromptBlocks.Scenario)),
            Persona = NullableTokens(BlockTokens(blocks, PromptBlocks.Persona)),
            Lore = NullableTokens(BlockTokens(blocks, PromptBlocks.Lore)),
            Examples = NullableTokens(BlockTokens(blocks, PromptBlocks.Examples)),
            Story = NullableTokens(story),
            AuthorsNote = NullableTokens(BlockTokens(blocks, PromptBlocks.AuthorsNote)),
            History = NullableTokens(BlockTokens(blocks, PromptBlocks.History)),
            PostHistory = NullableTokens(BlockTokens(blocks, PromptBlocks.PostHistory)),
            Instruction = NullableTokens(BlockTokens(blocks, PromptBlocks.Instruction)),
            Impersonate = NullableTokens(BlockTokens(blocks, PromptBlocks.Impersonate)),
            Opening = NullableTokens(BlockTokens(blocks, PromptBlocks.Opening)),
            Reply = NullableTokens(call.ReplyTokens),
            TokensPerSecond = call.TokensPerSecond is double speed ? Math.Round(speed, 2) : null,
            PromptSeconds = call.PromptSeconds is double prompt ? Math.Round(prompt, 2) : null,
            GenerationSeconds = Math.Round(call.GenerationSeconds, 2)
        };
    }

    private static int BlockTokens(IReadOnlyDictionary<string, int> blocks, string name)
        => blocks.TryGetValue(name, out var tokens) && tokens > 0 ? tokens : 0;

    private static int ExtraBlockTokens(IReadOnlyDictionary<string, int> blocks)
    {
        var extra = 0;

        foreach (KeyValuePair<string, int> pair in blocks)
        {
            if (pair.Value <= 0)
                continue;

            var known = false;

            for (var i = 0; i < ChatStatistics.OrderedPromptBlocks.Count; i++)
            {
                if (string.Equals(ChatStatistics.OrderedPromptBlocks[i], pair.Key, StringComparison.Ordinal))
                {
                    known = true;
                    break;
                }
            }

            if (!known)
                extra += pair.Value;
        }

        return extra;
    }

    /// <summary>Absent blocks stay <see langword="null"/> so <see cref="ChartComponentBase{T}.SetHideEmptySeries"/> omits unused series from the legend.</summary>
    private static int? NullableTokens(int tokens)
        => tokens > 0 ? tokens : null;

    private static UIPhrase CountsPhrase(int exact, int approximate)
        => approximate == 0
            ? new UIPhrase("chatai.stats.counts.exact")
            : exact == 0
                ? new UIPhrase("chatai.stats.counts.approximate")
                : UIPhrase.Of("chatai.stats.counts.mixed", ("exact", exact), ("approximate", approximate));

    private static void FillShares(RecursiveCollection<ContextShareItem> target, IReadOnlyList<BlockShare> shares)
    {
        foreach (BlockShare share in shares)
            target.Add(new ContextShareItem { Id = share.Block, BlockKey = BlockPhraseKey(share.Block), Tokens = share.Tokens });
    }

    /// <summary>The localization key for one block of the prompt or the reply.</summary>
    public static string BlockPhraseKey(string block)
        => "chatai.stats.block." + block;

    private static UIPhrase ShortCountsPhrase(TokenCounts counts)
        => new(counts == TokenCounts.Exact ? "chatai.stats.counts.short.exact" : "chatai.stats.counts.short.approximate");

    private async Task<UICommandResult> OpenStatisticsAsync(CancellationToken cancellationToken)
    {
        var open = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            open = _chat is not null;

            if (!open)
                return;

            _statisticsLoaded = true;
            BuildChatStatistics();
        }, cancellationToken).ConfigureAwait(false);

        return open ? UICommandResult.Ok([new OpenDialogEffect(StatisticsDialogKey)]) : UICommandResult.Ok();
    }

    private void RefreshLoadedStatistics()
    {
        if (_statisticsLoaded)
            BuildChatStatistics();
    }

    private async Task<UICommandResult> OpenMessageInfoAsync(string messageId, CancellationToken cancellationToken)
    {
        DocumentListing<ConnectionProfile> connections = await Data.Connections.ListAsync(cancellationToken).ConfigureAwait(false);
        var open = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (_chat is null || ChatStatistics.ForMessage(_chat, messageId) is not MessageGenerationSummary summary)
                return;

            open = true;
            BuildMessageInfo(summary, connections);
        }, cancellationToken).ConfigureAwait(false);

        return open ? UICommandResult.Ok([new OpenDialogEffect(MessageInfoDialogKey)]) : UICommandResult.Ok();
    }

    private void BuildMessageInfo(MessageGenerationSummary summary, DocumentListing<ConnectionProfile> connections)
    {
        MessageInfoShares.Clear();
        MessageInfoDetails.Clear();
        MessageInfoSamplers.Clear();

        FillShares(MessageInfoShares, summary.Shares);
        MessageInfoContextTotal = summary.ContextLength > 0 ? summary.ContextLength : null;
        MessageInfoContextTitle = summary.ContextLength > 0
            ? UIPhrase.Of("chatai.stats.context.call", ("used", summary.TotalTokens), ("context", summary.ContextLength), ("counts", ShortCountsPhrase(summary.Counts)))
            : UIPhrase.Of("chatai.stats.context.call.unbounded", ("used", summary.TotalTokens), ("counts", ShortCountsPhrase(summary.Counts)));

        MessageInfoDetails.Add(UIDetails.Row("counts", "chatai.stats.counts", CountsPhrase(summary.Counts == TokenCounts.Exact ? 1 : 0, summary.Counts == TokenCounts.Approximate ? 1 : 0)));
        MessageInfoDetails.Add(UIDetails.Row("prompt", "chatai.stats.prompt", UIPhrase.Of("chatai.stats.tokens.count", ("tokens", summary.PromptTokenCount))));
        MessageInfoDetails.Add(UIDetails.Row("reply", "chatai.stats.reply", UIPhrase.Of("chatai.stats.tokens.count", ("tokens", summary.ReplyTokens))));

        if (summary.Continuations.Count > 0)
            MessageInfoDetails.Add(UIDetails.Row("continuations", "chatai.stats.continuations", UIPhrase.Of("chatai.stats.tokens.count", ("tokens", summary.Continuations.Count))));

        if (summary.PromptSeconds is double ttft)
        {
            MessageInfoDetails.Add(UIDetails.Row("ttft", "chatai.stats.ttft", FormatSecondsAndSpeed(ttft, summary.PromptTokensPerSecond)));

            if (summary.GenerationSeconds is double genSeconds)
                MessageInfoDetails.Add(UIDetails.Row("generation", "chatai.stats.generation", FormatSecondsAndSpeed(genSeconds, summary.TokensPerSecond)));

            MessageInfoDetails.Add(UIDetails.Row("duration", "chatai.stats.duration", UIPhrase.Of("chatai.stats.seconds", ("seconds", Math.Round(summary.Seconds, 2)))));
        }
        else
        {
            MessageInfoDetails.Add(UIDetails.Row("duration", "chatai.stats.duration", FormatSecondsAndSpeed(summary.Seconds, summary.TokensPerSecond)));
        }

        if (!string.IsNullOrWhiteSpace(summary.Model))
            MessageInfoDetails.Add(UIDetails.Row("model", "chatai.stats.model", UIPhrase.Text(ShortModelName(summary.Model))));

        if (!string.IsNullOrWhiteSpace(summary.Connection))
            MessageInfoDetails.Add(UIDetails.Row("connection", "chatai.generation.connection", UIPhrase.Text(ConnectionName(connections, summary.Connection))));

        if (summary.Seed is long seed)
            MessageInfoDetails.Add(UIDetails.Row("seed", "chatai.samplers.seed", UIPhrase.Text(seed.ToString(CultureInfo.InvariantCulture))));

        if (summary.Samplers is GenerationSamplers samplers)
        {
            FillSamplers(samplers);
            MessageInfoSamplersVisibility = UIVisibility.Visible;
        }
        else
        {
            MessageInfoSamplersVisibility = UIVisibility.Collapsed;
        }
    }

    private static UIPhrase FormatSecondsAndSpeed(double seconds, double? speed)
        => speed is double tokPerSec
            ? UIPhrase.Of("chatai.stats.seconds-speed", ("seconds", Math.Round(seconds, 2)), ("speed", Math.Round(tokPerSec, 1)))
            : UIPhrase.Of("chatai.stats.seconds", ("seconds", Math.Round(seconds, 2)));

    private static string ConnectionName(DocumentListing<ConnectionProfile> connections, string connectionId)
    {
        foreach (StoredDocument<ConnectionProfile> stored in connections.Items)
        {
            if (string.Equals(stored.Id, connectionId, StringComparison.Ordinal))
                return stored.Document.Name.Length > 0 ? stored.Document.Name : stored.Id;
        }

        return connectionId;
    }

    private void FillSamplers(GenerationSamplers samplers)
    {
        if (samplers.Thinking is bool thinking)
            MessageInfoSamplers.Add(UIDetails.Row("thinking", "chatai.generation.thinking", new UIPhrase(thinking ? "chatai.stats.on" : "chatai.stats.off")));

        MessageInfoSamplers.Add(UIDetails.Row("replyLength", "chatai.samplers.reply-length", UIPhrase.Of("chatai.stats.tokens.count", ("tokens", samplers.ReplyLength))));
        MessageInfoSamplers.Add(UIDetails.Row("temperature", "chatai.samplers.temperature", NumberPhrase(samplers.Temperature)));
        MessageInfoSamplers.Add(UIDetails.Row("minP", "chatai.samplers.min-p", NumberPhrase(samplers.MinP)));
        MessageInfoSamplers.Add(UIDetails.Row("topP", "chatai.samplers.top-p", NumberPhrase(samplers.TopP)));

        if (samplers.TopK > 0)
            MessageInfoSamplers.Add(UIDetails.Row("topK", "chatai.samplers.top-k", UIPhrase.Of("chatai.stats.tokens.count", ("tokens", samplers.TopK))));

        MessageInfoSamplers.Add(UIDetails.Row("repetitionPenalty", "chatai.samplers.repetition-penalty", NumberPhrase(samplers.RepetitionPenalty)));

        if (samplers.RepetitionPenaltyRange is int range)
            MessageInfoSamplers.Add(UIDetails.Row("repetitionRange", "chatai.samplers.repetition-range", UIPhrase.Of("chatai.stats.tokens.count", ("tokens", range))));

        if (samplers.RepetitionPenaltySlope is double slope)
            MessageInfoSamplers.Add(UIDetails.Row("repetitionSlope", "chatai.samplers.repetition-slope", NumberPhrase(slope)));

        if (samplers.PresencePenalty is double presence)
            MessageInfoSamplers.Add(UIDetails.Row("presencePenalty", "chatai.samplers.presence-penalty", NumberPhrase(presence)));

        if (samplers.FrequencyPenalty is double frequency)
            MessageInfoSamplers.Add(UIDetails.Row("frequencyPenalty", "chatai.samplers.frequency-penalty", NumberPhrase(frequency)));

        if (samplers.TypicalP is double typicalP)
            MessageInfoSamplers.Add(UIDetails.Row("typicalP", "chatai.samplers.typical-p", NumberPhrase(typicalP)));

        if (samplers.Tfs is double tfs)
            MessageInfoSamplers.Add(UIDetails.Row("tfs", "chatai.samplers.tfs", NumberPhrase(tfs)));

        if (samplers.TopA is double topA)
            MessageInfoSamplers.Add(UIDetails.Row("topA", "chatai.samplers.top-a", NumberPhrase(topA)));

        if (samplers.SmoothingFactor is double smoothing)
            MessageInfoSamplers.Add(UIDetails.Row("smoothing", "chatai.samplers.smoothing", NumberPhrase(smoothing)));

        if (samplers.NSigma is double nSigma)
            MessageInfoSamplers.Add(UIDetails.Row("nSigma", "chatai.samplers.n-sigma", NumberPhrase(nSigma)));

        if (samplers.Dry is DrySampler dry)
            MessageInfoSamplers.Add(UIDetails.Row("dry", "chatai.samplers.dry", UIPhrase.Of("chatai.stats.dry.value", ("multiplier", Math.Round(dry.Multiplier, 2)), ("base", Math.Round(dry.Base, 2)), ("allowed", dry.AllowedLength))));

        if (samplers.Xtc is XtcSampler xtc)
            MessageInfoSamplers.Add(UIDetails.Row("xtc", "chatai.samplers.xtc", UIPhrase.Of("chatai.stats.xtc.value", ("probability", Math.Round(xtc.Probability, 2)), ("threshold", Math.Round(xtc.Threshold, 2)))));

        if (samplers.DynamicTemperature is DynamicTemperature dynamic)
            MessageInfoSamplers.Add(UIDetails.Row("dynamic", "chatai.samplers.dynamic", UIPhrase.Of("chatai.stats.dynamic.value", ("min", Math.Round(dynamic.Min, 2)), ("max", Math.Round(dynamic.Max, 2)), ("exponent", Math.Round(dynamic.Exponent, 2)))));

        if (samplers.Mirostat is MirostatSampler mirostat)
            MessageInfoSamplers.Add(UIDetails.Row("mirostat", "chatai.stats.mirostat", UIPhrase.Of("chatai.stats.mirostat.value", ("mode", mirostat.Mode), ("tau", Math.Round(mirostat.Tau, 2)), ("eta", Math.Round(mirostat.Eta, 2)))));
    }

    private static UIPhrase NumberPhrase(double value)
        => UIPhrase.Of("chatai.stats.tokens.count", ("tokens", Math.Round(value, 4)));
}
