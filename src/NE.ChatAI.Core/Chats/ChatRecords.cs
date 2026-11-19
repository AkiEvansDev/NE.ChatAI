using System;
using System.Collections.Generic;
using NE.ChatAI.Prompts;

namespace NE.ChatAI.Chats;

/// <summary>One turn in the history as the prompt sees it, remembering the first and last chat message ids it stands for.</summary>
public sealed record ChatProjectedTurn(PromptMessage Message, string FirstMessageId, string LastMessageId, string? RecordId = null);

/// <summary>A slice of chat history handed to a scenario, with the underlying message range a replacement record covers.</summary>
public sealed record ChatHistorySlice(IReadOnlyList<PromptMessage> Messages, string? FirstMessageId, string? LastMessageId);

/// <summary>
/// Projects, slices and maintains <see cref="Chat.Records"/> as scenarios write them and as the reader edits, swipes or rolls back
/// the chat (<c>docs/GRAPHS.md</c>).
/// </summary>
public static class ChatRecords
{
    /// <summary>How many active-branch records sharing a tag are kept across distinct anchors: the current one and one previous backup.</summary>
    public const int MaxActivePerTag = 2;

    /// <summary>The history as the main prompt sends it: active non-stale replacements substituted and insertions placed after their message.</summary>
    public static IReadOnlyList<PromptMessage> ProjectHistory(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatRecord> records)
    {
        IReadOnlyList<ChatProjectedTurn> projected = Project(messages, records);
        List<PromptMessage> history = [with(projected.Count)];

        for (var i = 0; i < projected.Count; i++)
            history.Add(projected[i].Message);

        return history;
    }

    /// <summary>Projects <paramref name="messages"/> with every active non-stale record whose covered messages stand in <paramref name="messages"/>.</summary>
    public static IReadOnlyList<ChatProjectedTurn> Project(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatRecord> records)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(records);

        if (messages.Count == 0)
            return [];

        if (records.Count == 0)
            return RawTurns(messages);

        Dictionary<string, int> indexById = IndexMap(messages);
        Dictionary<string, (int Anchor, int Span, int RecordIndex)>? latestByTag = SelectLatestNonStaleByTag(records, indexById, messages);
        List<(int First, int Last, int Order, ChatRecord Record)> replacements = [];
        Dictionary<int, List<ChatRecord>> insertions = [];

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];

            if (record.Stale)
                continue;

            if (record.Tag is string tag && latestByTag is not null && latestByTag.TryGetValue(tag, out (int Anchor, int Span, int RecordIndex) latest) && latest.RecordIndex != i)
                continue;

            if (TryResolveRange(record, indexById, out var first, out var last))
            {
                replacements.Add((first, last, i, record));
            }
            else if (record.After is string afterId
                && indexById.TryGetValue(afterId, out var afterIndex)
                && IsVariantActive(record, messages[afterIndex])
                && (!record.OneTurn || afterIndex == messages.Count - 1))
            {
                if (!insertions.TryGetValue(afterIndex, out List<ChatRecord>? list))
                    insertions[afterIndex] = list = [];

                list.Add(record);
            }
        }

        if (replacements.Count == 0 && insertions.Count == 0)
            return RawTurns(messages);

        // Broader or newer replacements starting at or covering the same index win cleanly.
        replacements.Sort(static (left, right) =>
        {
            var byStart = left.First.CompareTo(right.First);

            if (byStart != 0)
                return byStart;

            var byEnd = right.Last.CompareTo(left.Last);

            return byEnd != 0 ? byEnd : right.Order.CompareTo(left.Order);
        });

        List<ChatProjectedTurn> turns = [with(messages.Count + insertions.Count)];
        var repCursor = 0;

        for (var i = 0; i < messages.Count; i++)
        {
            while (repCursor < replacements.Count && replacements[repCursor].First < i)
                repCursor++;

            if (repCursor < replacements.Count && replacements[repCursor].First == i)
            {
                (var first, var last, _, ChatRecord record) = replacements[repCursor];
                turns.Add(new ChatProjectedTurn(new PromptMessage(record.Role, FormatRecordText(record), record.Thought, ExpandMacros: true), messages[first].Id, messages[last].Id, record.Id));
                AppendInsertions(turns, insertions, last, messages[last].Id);
                i = last;
                continue;
            }

            ChatMessage message = messages[i];
            turns.Add(new ChatProjectedTurn(new PromptMessage(message.Role, message.Current.Text, message.Current.Thought), message.Id, message.Id));
            AppendInsertions(turns, insertions, i, message.Id);
        }

        return turns;
    }

    private static List<ChatProjectedTurn> RawTurns(IReadOnlyList<ChatMessage> messages)
    {
        List<ChatProjectedTurn> turns = [with(messages.Count)];

        for (var i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            turns.Add(new ChatProjectedTurn(new PromptMessage(message.Role, message.Current.Text, message.Current.Thought), message.Id, message.Id));
        }

        return turns;
    }

    private static Dictionary<string, int> IndexMap(IReadOnlyList<ChatMessage> messages)
    {
        Dictionary<string, int> map = new(messages.Count, StringComparer.Ordinal);

        for (var i = 0; i < messages.Count; i++)
            map[messages[i].Id] = i;

        return map;
    }

    private static Dictionary<string, (int Anchor, int Span, int RecordIndex)>? SelectLatestNonStaleByTag(IReadOnlyList<ChatRecord> records, Dictionary<string, int> indexById, IReadOnlyList<ChatMessage> messages)
    {
        Dictionary<string, (int Anchor, int Span, int RecordIndex)>? bestByTag = null;

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];

            if (record.Stale
                || record.Tag is not string tag
                || !TryResolveActiveAnchor(record, indexById, messages, out var anchor, out var span)
                || (record.OneTurn && record.Replaces is not { Count: >= 2 } && anchor != messages.Count - 1))
            {
                continue;
            }

            bestByTag ??= [with(StringComparer.Ordinal)];

            if (!bestByTag.TryGetValue(tag, out (int Anchor, int Span, int RecordIndex) current)
                || anchor > current.Anchor
                || (anchor == current.Anchor && span >= current.Span))
            {
                bestByTag[tag] = (anchor, span, i);
            }
        }

        return bestByTag;
    }

    private static bool TryResolveActiveAnchor(ChatRecord record, Dictionary<string, int> indexById, IReadOnlyList<ChatMessage> messages, out int anchorIndex, out int span)
    {
        if (TryResolveRange(record, indexById, out var first, out var last))
        {
            anchorIndex = last;
            span = last - first;
            return true;
        }

        if (record.After is string afterId && indexById.TryGetValue(afterId, out var afterIndex) && IsVariantActive(record, messages[afterIndex]))
        {
            anchorIndex = afterIndex;
            span = 0;
            return true;
        }

        anchorIndex = -1;
        span = 0;
        return false;
    }

    private static bool TryResolveRange(ChatRecord record, Dictionary<string, int> indexById, out int first, out int last)
    {
        first = -1;
        last = -1;

        if (record.Replaces is not { Count: >= 2 } range)
            return false;

        return indexById.TryGetValue(range[0], out first)
            && indexById.TryGetValue(range[^1], out last)
            && first <= last;
    }

    private static bool IsVariantActive(ChatRecord record, ChatMessage message)
        => record.Variant is not int variant || ChatEditing.Shown(message) == variant;

    private static string FormatRecordText(ChatRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Template))
            return record.Text;

        return record.Template.Contains("{0}", StringComparison.Ordinal)
            ? record.Template.Replace("{0}", record.Text, StringComparison.Ordinal)
            : $"{record.Template}\n{record.Text}";
    }

    private static void AppendInsertions(List<ChatProjectedTurn> turns, Dictionary<int, List<ChatRecord>> insertions, int index, string messageId)
    {
        if (!insertions.TryGetValue(index, out List<ChatRecord>? list))
            return;

        for (var i = 0; i < list.Count; i++)
        {
            ChatRecord record = list[i];
            turns.Add(new ChatProjectedTurn(new PromptMessage(record.Role, FormatRecordText(record), record.Thought, ExpandMacros: true), messageId, messageId, record.Id));
        }
    }

    /// <summary>Takes a slice of the projected (or raw) history: skipping <paramref name="skipStart"/> and <paramref name="skipEnd"/>, then keeping the last <paramref name="last"/> turns when positive.</summary>
    public static ChatHistorySlice Slice(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatRecord> records, int skipStart, int skipEnd, int last, bool raw)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(records);

        IReadOnlyList<ChatProjectedTurn> source = raw ? RawTurns(messages) : Project(messages, records);
        var start = Math.Clamp(Math.Max(0, skipStart), 0, source.Count);
        var end = Math.Clamp(source.Count - Math.Max(0, skipEnd), start, source.Count);

        if (last > 0 && end - start > last)
            start = end - last;

        if (start >= end)
            return new ChatHistorySlice([], null, null);

        List<PromptMessage> sliced = [with(end - start)];

        for (var i = start; i < end; i++)
            sliced.Add(source[i].Message);

        return new ChatHistorySlice(sliced, source[start].FirstMessageId, source[end - 1].LastMessageId);
    }

    /// <summary>The message id in <paramref name="messages"/> under which <paramref name="record"/>'s folded block stands, or <c>null</c> when its messages sit in another variant's tail or another variant of that message.</summary>
    public static string? AnchorMessageId(ChatRecord record, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(messages);

        if (record.Replaces is { Count: >= 2 } range)
        {
            var first = ChatEditing.IndexOf(messages, range[0]);
            var last = ChatEditing.IndexOf(messages, range[^1]);

            return first >= 0 && last >= first ? range[^1] : null;
        }

        if (record.After is string afterId)
        {
            var at = ChatEditing.IndexOf(messages, afterId);

            return at >= 0 && IsVariantActive(record, messages[at]) ? afterId : null;
        }

        return null;
    }

    /// <summary>The message id under which <paramref name="record"/> renders its folded block in the feed, or <c>null</c> when inactive or superseded by a newer active record of the same tag.</summary>
    public static string? VisibleAnchorMessageId(ChatRecord record, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatRecord> records)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(records);

        if (AnchorMessageId(record, messages) is not string anchorId)
            return null;

        if (record.Tag is not string tag)
            return anchorId;

        Dictionary<string, int> indexById = IndexMap(messages);

        if (!TryResolveActiveAnchor(record, indexById, messages, out var ownAnchor, out var ownSpan))
            return null;

        var passedSelf = false;

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord other = records[i];

            if (ReferenceEquals(other, record) || other.Id == record.Id)
            {
                passedSelf = true;
                continue;
            }

            if (!string.Equals(other.Tag, tag, StringComparison.Ordinal)
                || !TryResolveActiveAnchor(other, indexById, messages, out var otherAnchor, out var otherSpan))
            {
                continue;
            }

            if (otherAnchor > ownAnchor
                || (otherAnchor == ownAnchor && (otherSpan > ownSpan || (otherSpan == ownSpan && passedSelf))))
            {
                return null;
            }
        }

        return anchorId;
    }

    /// <summary>Finds the active timeline's latest record carrying <paramref name="tag"/> (optionally strictly before <paramref name="beforeAnchorId"/>), stale or current; <c>null</c> when none matches.</summary>
    public static ChatRecord? FindByTag(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatRecord> records, string? tag, string? beforeAnchorId = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(records);

        if (string.IsNullOrWhiteSpace(tag) || records.Count == 0 || messages.Count == 0)
            return null;

        var trimmed = tag.Trim();
        Dictionary<string, int> indexById = IndexMap(messages);
        var cutoff = -1;
        var hasCutoff = beforeAnchorId is not null && indexById.TryGetValue(beforeAnchorId, out cutoff);
        ChatRecord? best = null;
        var bestAnchor = -1;
        var bestSpan = -1;

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];

            if (!string.Equals(record.Tag, trimmed, StringComparison.Ordinal)
                || !TryResolveActiveAnchor(record, indexById, messages, out var anchorIndex, out var span))
            {
                continue;
            }

            if (hasCutoff && anchorIndex >= cutoff)
                continue;

            if (best is null || anchorIndex > bestAnchor || (anchorIndex == bestAnchor && span >= bestSpan))
            {
                best = record;
                bestAnchor = anchorIndex;
                bestSpan = span;
            }
        }

        return best;
    }

    /// <summary>Counts how many <see cref="MessageRole.User"/> turns stand along <paramref name="messages"/>.</summary>
    public static int CountUserTurns(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var count = 0;

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == MessageRole.User)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Counts how many <see cref="MessageRole.User"/> turns have elapsed strictly after the latest active-branch record or visual written by
    /// <paramref name="scenarioId"/> on <paramref name="chat"/>; <c>null</c> when that scenario has no active output on the branch.
    /// </summary>
    public static int? TurnsSinceScenarioOutput(Chat chat, string? scenarioId)
    {
        ArgumentNullException.ThrowIfNull(chat);

        if (string.IsNullOrWhiteSpace(scenarioId) || (chat.Records.Count == 0 && chat.Visuals.Count == 0))
            return null;

        var trimmed = scenarioId.Trim();
        Dictionary<string, int>? indexById = chat.Messages.Count > 0 ? IndexMap(chat.Messages) : null;
        var latestAnchor = int.MinValue;

        if (indexById is not null)
        {
            for (var i = 0; i < chat.Records.Count; i++)
            {
                ChatRecord record = chat.Records[i];

                if (record.Stale
                    || !string.Equals(record.Scenario, trimmed, StringComparison.Ordinal)
                    || !TryResolveActiveAnchor(record, indexById, chat.Messages, out var anchorIndex, out _))
                {
                    continue;
                }

                if (anchorIndex > latestAnchor)
                    latestAnchor = anchorIndex;
            }
        }

        for (var i = 0; i < chat.Visuals.Count; i++)
        {
            ChatVisual visual = chat.Visuals[i];

            if (!string.Equals(visual.Scenario, trimmed, StringComparison.Ordinal))
                continue;

            if (visual.After is null)
            {
                if (-1 > latestAnchor)
                    latestAnchor = -1;
            }
            else if (indexById is not null
                && indexById.TryGetValue(visual.After, out var visualIndex)
                && ChatVisuals.IsActive(visual, chat.Messages)
                && visualIndex > latestAnchor)
            {
                latestAnchor = visualIndex;
            }
        }

        if (latestAnchor == int.MinValue)
            return null;

        var turns = 0;

        for (var i = Math.Max(0, latestAnchor + 1); i < chat.Messages.Count; i++)
        {
            if (chat.Messages[i].Role == MessageRole.User)
                turns++;
        }

        return turns;
    }

    /// <summary>Finds the latest active-branch record written by <paramref name="scenarioId"/> along <paramref name="messages"/>, or <c>null</c> when none stands on the branch.</summary>
    public static ChatRecord? LatestScenarioRecord(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatRecord> records, string? scenarioId)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(records);

        if (string.IsNullOrWhiteSpace(scenarioId) || records.Count == 0 || messages.Count == 0)
            return null;

        var trimmed = scenarioId.Trim();
        Dictionary<string, int> indexById = IndexMap(messages);
        ChatRecord? best = null;
        var bestAnchor = -1;
        var bestSpan = -1;

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];

            if (record.Stale
                || !string.Equals(record.Scenario, trimmed, StringComparison.Ordinal)
                || !TryResolveActiveAnchor(record, indexById, messages, out var anchorIndex, out var span))
            {
                continue;
            }

            if (best is null || anchorIndex > bestAnchor || (anchorIndex == bestAnchor && span >= bestSpan))
            {
                best = record;
                bestAnchor = anchorIndex;
                bestSpan = span;
            }
        }

        return best;
    }

    /// <summary>Commits <paramref name="written"/> records and <paramref name="generations"/> onto <paramref name="chat"/>, keeping the current and one previous active-branch record per tag.</summary>
    public static Chat PutMany(Chat chat, IReadOnlyList<ChatRecord> written, IReadOnlyList<GenerationRecord> generations)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(generations);

        if (written.Count == 0 && generations.Count == 0)
            return chat;

        List<ChatRecord> records = [.. chat.Records];
        Dictionary<string, int>? indexById = written.Count > 0 ? IndexMap(chat.Messages) : null;

        for (var i = 0; i < written.Count; i++)
        {
            ChatRecord incoming = NormalizeRecord(written[i], chat.Messages);

            if (incoming.Tag is string tag && indexById is not null && TryResolveActiveAnchor(incoming, indexById, chat.Messages, out var incomingAnchor, out _))
            {
                // Replace any active record of the same tag at the same anchor in place so re-runs do not evict the previous turn's backup.
                for (var j = records.Count - 1; j >= 0; j--)
                {
                    if (string.Equals(records[j].Tag, tag, StringComparison.Ordinal)
                        && TryResolveActiveAnchor(records[j], indexById, chat.Messages, out var existingAnchor, out _)
                        && existingAnchor == incomingAnchor)
                    {
                        records.RemoveAt(j);
                    }
                }

                records.Add(incoming);
                TrimActiveTagHistory(records, tag, indexById, chat.Messages);
            }
            else
            {
                records.Add(incoming);
            }
        }

        IReadOnlyList<GenerationRecord> allGenerations = generations.Count == 0 ? chat.Generations : [.. chat.Generations, .. generations];

        return chat with { Records = records, Generations = allGenerations };
    }

    private static ChatRecord NormalizeRecord(ChatRecord record, IReadOnlyList<ChatMessage> messages)
    {
        var trimmed = string.IsNullOrWhiteSpace(record.Tag) ? null : record.Tag.Trim();
        var text = record.Text.TrimEnd('\r', '\n');
        var thought = record.Thought?.TrimEnd('\r', '\n');
        var variant = record.Variant;

        if (variant is null && record.Replaces is not { Count: >= 2 } && record.After is string afterId)
        {
            var at = ChatEditing.IndexOf(messages, afterId);

            if (at >= 0)
                variant = ChatEditing.Shown(messages[at]);
        }

        return string.Equals(record.Tag, trimmed, StringComparison.Ordinal)
            && string.Equals(record.Text, text, StringComparison.Ordinal)
            && string.Equals(record.Thought, thought, StringComparison.Ordinal)
            && record.Variant == variant
            ? record
            : record with { Tag = trimmed, Text = text, Thought = thought, Variant = variant };
    }

    private static void TrimActiveTagHistory(List<ChatRecord> records, string tag, Dictionary<string, int> indexById, IReadOnlyList<ChatMessage> messages)
    {
        List<(int ListIndex, int Anchor, int Span)> active = [];

        for (var i = 0; i < records.Count; i++)
        {
            if (string.Equals(records[i].Tag, tag, StringComparison.Ordinal)
                && TryResolveActiveAnchor(records[i], indexById, messages, out var anchor, out var span))
            {
                active.Add((i, anchor, span));
            }
        }

        if (active.Count <= MaxActivePerTag)
            return;

        active.Sort(static (left, right) =>
        {
            var byAnchor = left.Anchor.CompareTo(right.Anchor);

            if (byAnchor != 0)
                return byAnchor;

            var bySpan = left.Span.CompareTo(right.Span);

            return bySpan != 0 ? bySpan : left.ListIndex.CompareTo(right.ListIndex);
        });

        var removeCount = active.Count - MaxActivePerTag;
        List<int> removeIndices = [with(removeCount)];

        for (var i = 0; i < removeCount; i++)
            removeIndices.Add(active[i].ListIndex);

        removeIndices.Sort();

        for (var i = removeIndices.Count - 1; i >= 0; i--)
            records.RemoveAt(removeIndices[i]);
    }

    /// <summary>Stamps <paramref name="variant"/> on any insertion record anchored to <paramref name="messageId"/> that was saved without an explicit variant index.</summary>
    internal static IReadOnlyList<ChatRecord> PinVariantRecords(IReadOnlyList<ChatRecord> records, string messageId, int variant)
    {
        if (records.Count == 0)
            return records;

        List<ChatRecord>? changed = null;

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];

            if (record.Replaces is { Count: >= 2 } || record.Variant is not null || !string.Equals(record.After, messageId, StringComparison.Ordinal))
                continue;

            changed ??= [.. records];
            changed[i] = record with { Variant = variant };
        }

        return changed ?? records;
    }

    /// <summary>Updates a record's text and marks it current again.</summary>
    public static Chat Edit(Chat chat, string recordId, string text)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(text);

        var index = IndexOfRecord(chat.Records, recordId);

        if (index < 0)
            return chat;

        List<ChatRecord> records = [.. chat.Records];
        records[index] = records[index] with { Text = text.TrimEnd('\r', '\n'), Stale = false };

        return chat with { Records = records };
    }

    private static int IndexOfRecord(IReadOnlyList<ChatRecord> records, string? recordId)
    {
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].Id == recordId)
                return i;
        }

        return -1;
    }

    /// <summary>Removes one record from the chat; its generation record stays, as the model call was made.</summary>
    public static Chat Delete(Chat chat, string recordId)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var index = IndexOfRecord(chat.Records, recordId);

        if (index < 0)
            return chat;

        List<ChatRecord> records = [.. chat.Records];
        records.RemoveAt(index);

        return chat with { Records = records };
    }

    /// <summary>Marks stale any active record covering or following the edited message at <paramref name="editedAt"/>.</summary>
    internal static IReadOnlyList<ChatRecord> OnMessageEdited(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatRecord> records, int editedAt)
    {
        if (records.Count == 0)
            return records;

        Dictionary<string, int> indexById = IndexMap(messages);
        List<ChatRecord>? changed = null;

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];

            if (record.Stale)
                continue;

            var hit = (TryResolveRange(record, indexById, out var first, out var last) && first <= editedAt && editedAt <= last)
                || (record.After is string afterId && indexById.TryGetValue(afterId, out var afterIndex) && afterIndex == editedAt && IsVariantActive(record, messages[editedAt]));

            if (!hit)
                continue;

            changed ??= [.. records];
            changed[i] = record with { Stale = true };
        }

        return changed ?? records;
    }

    /// <summary>Marks stale any replacement covering the deleted message at <paramref name="deletedAt"/> and drops insertion or replacement records whose messages are gone.</summary>
    internal static IReadOnlyList<ChatRecord> OnMessageDeleted(IReadOnlyList<ChatMessage> before, IReadOnlyList<ChatRecord> records, int deletedAt, IReadOnlyList<ChatMessage> after)
    {
        if (records.Count == 0)
            return records;

        if (after.Count == 0)
            return [];

        Dictionary<string, int> indexById = IndexMap(before);
        HashSet<string> survivingIds = CollectAllMessageIds(after);
        List<ChatRecord>? changed = null;

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];

            if (TryResolveRange(record, indexById, out var first, out var last) && first <= deletedAt && deletedAt <= last)
            {
                changed ??= CopyPrefix(records, i);

                if (first == last)
                    continue;

                changed.Add(deletedAt == first
                    ? record with { Replaces = [before[first + 1].Id, before[last].Id], Stale = true }
                    : deletedAt == last
                        ? record with { Replaces = [before[first].Id, before[last - 1].Id], Stale = true }
                        : record with { Stale = true });
            }
            else if (record.After is string afterId && indexById.TryGetValue(afterId, out var afterIndex) && afterIndex == deletedAt)
            {
                changed ??= CopyPrefix(records, i);
            }
            else if (!ReferencesSurvive(record, survivingIds))
            {
                changed ??= CopyPrefix(records, i);
            }
            else
            {
                changed?.Add(record);
            }
        }

        return changed ?? records;
    }

    private static HashSet<string> CollectAllMessageIds(IReadOnlyList<ChatMessage> messages)
    {
        HashSet<string> ids = [with(StringComparer.Ordinal)];
        AddMessageIds(messages, ids);
        return ids;
    }

    private static void AddMessageIds(IReadOnlyList<ChatMessage> messages, HashSet<string> ids)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            _ = ids.Add(message.Id);

            for (var j = 0; j < message.Variants.Count; j++)
            {
                if (message.Variants[j].Tail is { Count: > 0 } tail)
                    AddMessageIds(tail, ids);
            }
        }
    }

    private static List<ChatRecord> CopyPrefix(IReadOnlyList<ChatRecord> records, int count)
    {
        List<ChatRecord> copy = [with(records.Count)];

        for (var i = 0; i < count; i++)
            copy.Add(records[i]);

        return copy;
    }

    private static bool ReferencesSurvive(ChatRecord record, HashSet<string> survivingIds)
    {
        if (record.Replaces is { Count: >= 2 } range)
            return survivingIds.Contains(range[0]) && survivingIds.Contains(range[^1]);

        return record.After is string afterId && survivingIds.Contains(afterId);
    }

    /// <summary>Removes insertion records (<see cref="ChatRecord.After"/>) anchored at or after <paramref name="fromAt"/> when a message is refreshed.</summary>
    public static IReadOnlyList<ChatRecord> DropInsertionsFrom(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatRecord> records, int fromAt)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0 || fromAt < 0 || fromAt >= messages.Count)
            return records;

        Dictionary<string, int> indexById = IndexMap(messages);
        List<ChatRecord>? kept = null;

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];
            var drop = record.Replaces is not { Count: >= 2 }
                && record.After is string afterId
                && indexById.TryGetValue(afterId, out var afterIndex)
                && (afterIndex > fromAt || (afterIndex == fromAt && IsVariantActive(record, messages[fromAt])));

            if (drop)
            {
                kept ??= CopyPrefix(records, i);
            }
            else
            {
                kept?.Add(record);
            }
        }

        return kept ?? records;
    }

    /// <summary>Marks stale and clamps any replacement touched when the chat is remade or rolled back, dropping records whose messages are gone.</summary>
    internal static IReadOnlyList<ChatRecord> OnRolledBack(IReadOnlyList<ChatMessage> before, IReadOnlyList<ChatRecord> records, IReadOnlyList<ChatMessage> after, int? remadeAt = null)
    {
        if (records.Count == 0)
            return records;

        if (after.Count == 0)
            return [];

        Dictionary<string, int> indexById = IndexMap(before);
        HashSet<string> survivingIds = CollectAllMessageIds(after);
        var keptCount = after.Count;
        var cutoff = remadeAt ?? keptCount;
        List<ChatRecord>? changed = null;

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];

            if (TryResolveRange(record, indexById, out var first, out var last))
            {
                if (last < cutoff)
                {
                    changed?.Add(record);
                    continue;
                }

                changed ??= CopyPrefix(records, i);

                if (first < keptCount)
                    changed.Add(record with { Replaces = [before[first].Id, after[Math.Min(last, keptCount - 1)].Id], Stale = true });
            }
            else if (record.After is string afterId && indexById.TryGetValue(afterId, out var afterIndex))
            {
                if (afterIndex < cutoff || (afterIndex == cutoff && remadeAt is int remade && !IsVariantActive(record, before[remade])))
                {
                    changed?.Add(record);
                    continue;
                }

                changed ??= CopyPrefix(records, i);
            }
            else if (!ReferencesSurvive(record, survivingIds))
            {
                changed ??= CopyPrefix(records, i);
            }
            else
            {
                changed?.Add(record);
            }
        }

        return changed ?? records;
    }

    /// <summary>Keeps records belonging up to <paramref name="upToAt"/> for a branched chat, marking partial ranges stale and dropping future records.</summary>
    internal static IReadOnlyList<ChatRecord> OnBranchedUpTo(IReadOnlyList<ChatMessage> before, IReadOnlyList<ChatRecord> records, int upToAt, IReadOnlyList<ChatMessage> after)
    {
        if (records.Count == 0 || after.Count == 0)
            return [];

        Dictionary<string, int> indexById = IndexMap(before);
        HashSet<string> survivingIds = CollectAllMessageIds(after);
        var lastKeptId = after[^1].Id;
        List<ChatRecord> kept = [with(records.Count)];

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];

            if (TryResolveRange(record, indexById, out var first, out var last))
            {
                if (first > upToAt)
                    continue;

                kept.Add(last <= upToAt ? record : record with { Replaces = [before[first].Id, lastKeptId], Stale = true });
            }
            else if (record.After is string afterId && indexById.TryGetValue(afterId, out var afterIndex))
            {
                if (afterIndex <= upToAt)
                    kept.Add(record);
            }
            else if (ReferencesSurvive(record, survivingIds))
            {
                kept.Add(record);
            }
        }

        return kept;
    }
}
