using System;
using System.Collections.Generic;
using System.Linq;

namespace NE.ChatAI.Chats;

/// <summary>
/// The changes a chat's messages take — a reply added, a variant chosen or added, a message made again, words edited or continued,
/// messages deleted.
/// </summary>
/// <remarks>
/// Each returns the chat changed, or the chat as it was when the message named is not in it. The chat's messages are the variants
/// shown; what followed a variant not shown is kept in that variant (<see cref="MessageVariant.Tail"/>) and comes back with it.
/// </remarks>
public static class ChatEditing
{
    /// <summary>Adds a message at the end, with the record of how it was made and the chat's seed after it.</summary>
    public static Chat Append(Chat chat, ChatMessage message, GenerationRecord? record, long? chatSeed)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(message);

        return chat with { Seed = chatSeed, Messages = [.. chat.Messages, message], Generations = Recorded(chat, record) };
    }

    private static IReadOnlyList<GenerationRecord> Recorded(Chat chat, GenerationRecord? record)
        => record is null ? chat.Generations : [.. chat.Generations, record];

    /// <summary>
    /// Shows another of a message's variants with what followed it: the messages after the message go into the variant shown so far,
    /// and the chosen one's come out of it. An index past either end is held at it.
    /// </summary>
    public static Chat Choose(Chat chat, string messageId, int index)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var at = IndexOf(chat.Messages, messageId);

        if (at < 0)
            return chat;

        ChatMessage message = chat.Messages[at];
        var shown = Shown(message);
        var chosen = Math.Clamp(index, 0, Math.Max(0, message.Variants.Count - 1));

        if (chosen == shown)
            return chat;

        List<MessageVariant> variants = [.. message.Variants];
        IReadOnlyList<ChatMessage> tail = variants[chosen].Tail ?? [];

        variants[shown] = variants[shown] with { Tail = After(chat, at) };
        variants[chosen] = variants[chosen] with { Tail = null };

        return chat with
        {
            Messages = [.. chat.Messages.Take(at), message with { Variants = variants, Chosen = chosen }, .. tail],
            Records = ChatRecords.PinVariantRecords(chat.Records, message.Id, shown)
        };
    }

    /// <summary>Where the message stands among <paramref name="messages"/>; -1 for one they do not hold.</summary>
    public static int IndexOf(IReadOnlyList<ChatMessage> messages, string? messageId)
    {
        ArgumentNullException.ThrowIfNull(messages);

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Id == messageId)
                return i;
        }

        return -1;
    }

    internal static int Shown(ChatMessage message)
        => Math.Clamp(message.Chosen, 0, Math.Max(0, message.Variants.Count - 1));

    /// <summary>The messages after the one at <paramref name="at"/>, to keep in a variant; none kept as no list at all.</summary>
    private static List<ChatMessage>? After(Chat chat, int at)
        => at + 1 < chat.Messages.Count ? [.. chat.Messages.Skip(at + 1)] : null;

    /// <summary>
    /// Adds a variant to a message and shows it, the messages after the message kept with the variant shown so far; the chat's seed
    /// stays, as a new variant changes this message alone.
    /// </summary>
    public static Chat AddVariant(Chat chat, string messageId, MessageVariant variant, GenerationRecord? record)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(variant);

        var at = IndexOf(chat.Messages, messageId);

        if (at < 0)
            return chat;

        ChatMessage message = chat.Messages[at];
        var shown = Shown(message);
        List<MessageVariant> variants = [.. message.Variants];

        if (variants.Count > 0)
            variants[shown] = variants[shown] with { Tail = After(chat, at) };

        variants.Add(variant with { Tail = null });

        return chat with
        {
            Messages = [.. chat.Messages.Take(at), message with { Variants = variants, Chosen = variants.Count - 1 }],
            Records = variants.Count > 1 ? ChatRecords.PinVariantRecords(chat.Records, message.Id, shown) : chat.Records,
            Generations = Recorded(chat, record)
        };
    }

    /// <summary>Puts a new variant in place of the one shown and drops the messages after the message: the message made again from there.</summary>
    public static Chat Remake(Chat chat, string messageId, MessageVariant variant, GenerationRecord? record)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(variant);

        var at = IndexOf(chat.Messages, messageId);

        if (at < 0)
            return chat;

        IReadOnlyList<ChatMessage> after = [.. chat.Messages.Take(at), WithCurrent(chat.Messages[at], variant with { Tail = null })];

        return chat with { Messages = after, Records = ChatRecords.OnRolledBack(chat.Messages, chat.Records, after, remadeAt: at), Generations = Recorded(chat, record) };
    }

    private static ChatMessage WithCurrent(ChatMessage message, MessageVariant variant)
    {
        if (message.Variants.Count == 0)
            return message with { Variants = [variant], Chosen = 0 };

        var chosen = Shown(message);

        return message with { Variants = [.. message.Variants.Select((kept, index) => index == chosen ? variant : kept)], Chosen = chosen };
    }

    /// <summary>Puts <paramref name="words"/> after the shown variant's own: the reply continued.</summary>
    public static Chat Extend(Chat chat, string messageId, string words, GenerationRecord? record)
    {
        ArgumentNullException.ThrowIfNull(words);

        Chat changed = Change(chat, messageId, message => WithCurrent(message, Extended(message.Current, words, record)));

        return ReferenceEquals(changed, chat) ? chat : changed with { Generations = Recorded(chat, record) };
    }

    private static Chat Change(Chat chat, string messageId, Func<ChatMessage, ChatMessage> change)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var index = IndexOf(chat.Messages, messageId);

        if (index < 0)
            return chat;

        List<ChatMessage> messages = [.. chat.Messages];

        messages[index] = change(messages[index]);

        return chat with { Messages = messages };
    }

    private static MessageVariant Extended(MessageVariant current, string words, GenerationRecord? record)
    {
        // A reply stopped inside its thought had no spoken text yet; its continuation opens the speech without a leading blank.
        var text = current.Text.Length == 0 ? words.TrimStart() : current.Text + words;

        if (record is null)
            return current with { Text = text };

        return current.Generation is null
            ? current with { Text = text, Generation = record.Id }
            : current with { Text = text, Continuations = [.. current.Continuations ?? [], record.Id] };
    }

    /// <summary>Puts the reader's words in place of the shown variant's; its thought, seed and record stay with it.</summary>
    public static Chat Edit(Chat chat, string messageId, string text)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(text);

        var at = IndexOf(chat.Messages, messageId);

        if (at < 0 || string.Equals(chat.Messages[at].Current.Text, text, StringComparison.Ordinal))
            return chat;

        Chat changed = Change(chat, messageId, message => WithCurrent(message, message.Current with { Text = text }));

        return changed with { Records = ChatRecords.OnMessageEdited(chat.Messages, chat.Records, at) };
    }

    /// <summary>
    /// Takes one message out of the chat, those after it staying where they are, and what followed its other variants with it; the
    /// records of its generations stay, as the calls were made all the same.
    /// </summary>
    public static Chat Delete(Chat chat, string messageId)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var at = IndexOf(chat.Messages, messageId);

        if (at < 0)
            return chat;

        IReadOnlyList<ChatMessage> after = [.. chat.Messages.Where(message => message.Id != messageId)];

        return chat with { Messages = after, Records = ChatRecords.OnMessageDeleted(chat.Messages, chat.Records, at, after) };
    }

    /// <summary>The chat up to and with a message, every message after it gone: what a branch into a new chat starts from.</summary>
    public static Chat UpTo(Chat chat, string messageId)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var at = IndexOf(chat.Messages, messageId);

        if (at < 0)
            return chat;

        ChatMessage last = WithoutTails(chat.Messages[at]);

        if (at == chat.Messages.Count - 1 && ReferenceEquals(last, chat.Messages[at]))
            return chat;

        IReadOnlyList<ChatMessage> kept = [.. chat.Messages.Take(at), last];
        IReadOnlyList<ChatRecord> keptRecords = ChatRecords.OnBranchedUpTo(chat.Messages, chat.Records, at, kept);

        return chat with { Messages = kept, Records = keptRecords, Generations = KeptGenerations(chat, at, kept, keptRecords) };
    }

    private static ChatMessage WithoutTails(ChatMessage message)
    {
        if (message.Variants.All(static variant => variant.Tail is null))
            return message;

        return message with { Variants = [.. message.Variants.Select(static variant => variant.Tail is null ? variant : variant with { Tail = null })] };
    }

    private static IReadOnlyList<GenerationRecord> KeptGenerations(Chat chat, int at, IReadOnlyList<ChatMessage> kept, IReadOnlyList<ChatRecord> keptRecords)
    {
        if (chat.Generations.Count == 0)
            return chat.Generations;

        HashSet<string> keptIds = [];
        CollectGenerations(kept, keptIds);

        HashSet<string> keptRecordIds = [];

        foreach (ChatRecord record in keptRecords)
        {
            _ = keptRecordIds.Add(record.Id);

            if (record.Generation is not null)
                _ = keptIds.Add(record.Generation);
        }

        HashSet<string> droppedIds = [];
        DateTimeOffset earliestDropped = CollectDropped(chat, at, droppedIds);

        foreach (ChatRecord record in chat.Records)
        {
            if (!keptRecordIds.Contains(record.Id) && record.Generation is not null && !keptIds.Contains(record.Generation))
                _ = droppedIds.Add(record.Generation);
        }

        var lastKeptIndex = -1;
        var firstDroppedIndex = chat.Generations.Count;

        for (var i = 0; i < chat.Generations.Count; i++)
        {
            GenerationRecord record = chat.Generations[i];

            if (keptIds.Contains(record.Id))
            {
                lastKeptIndex = i;
            }
            else if (droppedIds.Contains(record.Id))
            {
                if (i < firstDroppedIndex)
                    firstDroppedIndex = i;

                earliestDropped = Earlier(earliestDropped, record.Started);
            }
        }

        List<GenerationRecord> result = [with(chat.Generations.Count)];

        for (var i = 0; i < chat.Generations.Count; i++)
        {
            GenerationRecord record = chat.Generations[i];

            if (keptIds.Contains(record.Id))
                result.Add(record);
            else if (!droppedIds.Contains(record.Id) && i < firstDroppedIndex && KeptUnlinked(record, i, lastKeptIndex, earliestDropped))
                result.Add(record);
        }

        return result;
    }

    private static void CollectGenerations(IReadOnlyList<ChatMessage> messages, HashSet<string> ids)
    {
        foreach (ChatMessage message in messages)
        {
            foreach (MessageVariant variant in message.Variants)
            {
                if (variant.Generation is not null)
                    _ = ids.Add(variant.Generation);

                if (variant.Continuations is not null)
                {
                    foreach (var continuation in variant.Continuations)
                        _ = ids.Add(continuation);
                }

                if (variant.Tail is not null)
                    CollectGenerations(variant.Tail, ids);
            }
        }
    }

    private static DateTimeOffset CollectDropped(Chat chat, int at, HashSet<string> ids)
    {
        DateTimeOffset earliest = DateTimeOffset.MaxValue;

        foreach (MessageVariant variant in chat.Messages[at].Variants)
        {
            if (variant.Tail is not null)
            {
                CollectGenerations(variant.Tail, ids);
                earliest = Earlier(earliest, EarliestMessage(variant.Tail));
            }
        }

        for (var i = at + 1; i < chat.Messages.Count; i++)
        {
            ChatMessage message = chat.Messages[i];
            CollectGenerations([message], ids);
            earliest = Earlier(earliest, EarliestMessage([message]));
        }

        return earliest;
    }

    private static DateTimeOffset Earlier(DateTimeOffset current, DateTimeOffset candidate)
        => candidate > DateTimeOffset.MinValue && candidate < current ? candidate : current;

    private static DateTimeOffset EarliestMessage(IReadOnlyList<ChatMessage> messages)
    {
        DateTimeOffset earliest = DateTimeOffset.MaxValue;

        foreach (ChatMessage message in messages)
        {
            earliest = Earlier(earliest, message.At);

            foreach (MessageVariant variant in message.Variants)
            {
                if (variant.Tail is not null)
                    earliest = Earlier(earliest, EarliestMessage(variant.Tail));
            }
        }

        return earliest;
    }

    private static bool KeptUnlinked(GenerationRecord record, int index, int lastKeptIndex, DateTimeOffset earliestDropped)
        => earliestDropped < DateTimeOffset.MaxValue && record.Started > DateTimeOffset.MinValue
            ? record.Started < earliestDropped
            : index <= lastKeptIndex;

    /// <summary>Takes a message out of the chat with every message after it.</summary>
    public static Chat DeleteFrom(Chat chat, string messageId)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var at = IndexOf(chat.Messages, messageId);

        if (at < 0)
            return chat;

        IReadOnlyList<ChatMessage> after = [.. chat.Messages.Take(at)];

        return chat with { Messages = after, Records = ChatRecords.OnRolledBack(chat.Messages, chat.Records, after) };
    }
}
