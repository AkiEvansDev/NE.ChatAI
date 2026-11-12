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
        List<(int First, int Last, int Order, ChatRecord Record)> replacements = [];
        Dictionary<int, List<ChatRecord>> insertions = [];

        for (var i = 0; i < records.Count; i++)
        {
            ChatRecord record = records[i];

            if (record.Stale)
                continue;

            if (TryResolveRange(record, indexById, out var first, out var last))
            {
                replacements.Add((first, last, i, record));
            }
            else if (record.After is string afterId && indexById.TryGetValue(afterId, out var afterIndex) && IsVariantActive(record, messages[afterIndex]))
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
                turns.Add(new ChatProjectedTurn(new PromptMessage(record.Role, FormatRecordText(record), record.Thought), messages[first].Id, messages[last].Id, record.Id));
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
            turns.Add(new ChatProjectedTurn(new PromptMessage(record.Role, FormatRecordText(record), record.Thought), messageId, messageId, record.Id));
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

    /// <summary>Finds the active timeline's latest record carrying <paramref name="tag"/>, stale or current; <c>null</c> when none matches.</summary>
    public static ChatRecord? FindByTag(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatRecord> records, string? tag)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(records);

        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var trimmed = tag.Trim();

        for (var i = records.Count - 1; i >= 0; i--)
        {
            ChatRecord record = records[i];

            if (string.Equals(record.Tag, trimmed, StringComparison.Ordinal) && AnchorMessageId(record, messages) is not null)
                return record;
        }

        return null;
    }

    /// <summary>Commits <paramref name="written"/> records and <paramref name="generations"/> onto <paramref name="chat"/>, replacing earlier active-branch records with matching tags.</summary>
    public static Chat PutMany(Chat chat, IReadOnlyList<ChatRecord> written, IReadOnlyList<GenerationRecord> generations)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(generations);

        if (written.Count == 0 && generations.Count == 0)
            return chat;

        List<ChatRecord> records = [.. chat.Records];

        for (var i = 0; i < written.Count; i++)
        {
            ChatRecord incoming = NormalizeRecord(written[i], chat.Messages);
            var replaced = false;

            if (incoming.Tag is string tag)
            {
                for (var j = records.Count - 1; j >= 0; j--)
                {
                    if (string.Equals(records[j].Tag, tag, StringComparison.Ordinal) && AnchorMessageId(records[j], chat.Messages) is not null)
                    {
                        if (!replaced)
                        {
                            records[j] = incoming;
                            replaced = true;
                        }
                        else
                        {
                            records.RemoveAt(j);
                        }
                    }
                }
            }

            if (!replaced)
                records.Add(incoming);
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
