using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Chats;

namespace NE.ChatAI.Controllers;

/// <summary>
/// The open chat's messages read from its end backwards, a window at a time: a long chat paints its last messages first, and older
/// ones are read as the reader scrolls up. A message added while the reader holds the end is appended.
/// </summary>
/// <remarks>
/// The row a reply streams into is held apart (<see cref="Live"/>): a window read again while it is written — the reader scrolled
/// away and back — shows that row rather than the message as the chat last kept it, or without it while it is a new one.
/// </remarks>
internal sealed class MessageSource : UIItemSourceBase<ChatMessageItem>
{
    private Func<IReadOnlyList<ChatMessage>> _messages = static () => [];
    private Func<ChatMessage, ChatMessageItem>? _shape;

    /// <summary>The row a reply is being written into, until it ends.</summary>
    internal ChatMessageItem? Live { get; set; }

    /// <summary>Whether the messages after <see cref="Live"/> are left out: a message made again in the middle of the chat.</summary>
    public bool EndsAtLive { get; private set; }

    /// <summary>Reads the messages <paramref name="messages"/> gives as they are at each read, shaped into rows by <paramref name="shape"/>.</summary>
    public void Attach(Func<IReadOnlyList<ChatMessage>> messages, Func<ChatMessage, ChatMessageItem> shape)
    {
        _messages = messages;
        _shape = shape;
        Live = null;
        EndsAtLive = false;
    }

    /// <summary>The feed read again by the page from where it stands, ending at <see cref="Live"/> while <paramref name="endsAtLive"/>.</summary>
    public void Reread(bool endsAtLive = false)
    {
        EndsAtLive = endsAtLive;
        Invalidate();
    }

    public ChatMessageItem? Held(string messageId)
        => Find(messageId);

    /// <summary>A row for a message just added, at the window's end while the reader holds it.</summary>
    public void Add(ChatMessageItem item)
    {
        if (!HasMoreAfter)
            Append(item);
    }

    public bool Drop(string messageId)
        => Remove(messageId);

    /// <remarks>
    /// The windows carry neither their position nor the count, as a conversation's do: the feed holds only what it has read and stops
    /// at its top, a ring turning there while older messages are read, rather than standing the whole chat in empty rows.
    /// </remarks>
    protected override Task<UIItemWindow<ChatMessageItem>> GetWindowAsync(UIItemWindowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<ChatMessage> messages = _messages();

        if (_shape is null)
            return Task.FromResult(new UIItemWindow<ChatMessageItem>([]));

        var total = EndsAtLive && Live is ChatMessageItem made && ChatEditing.IndexOf(messages, made.Id) is var cut and >= 0 ? cut + 1 : messages.Count;
        var count = request.Count;
        int start;

        // A window read again after the chat was cut short under it — a message made again in its middle — reads from the end
        // where its place is gone; after a reply not yet in the chat there is nothing.
        switch (request.Anchor.Kind)
        {
            case UIItemAnchorKind.End:
                start = total - count;
                break;

            case UIItemAnchorKind.Offset:
                start = request.Anchor.Offset < total ? request.Anchor.Offset : total - count;
                break;

            // Those before the message only: near the start fewer than asked, never the message itself or those after it again.
            case UIItemAnchorKind.Before:
                var before = Math.Max(0, ChatEditing.IndexOf(messages, request.Anchor.Key));
                start = before - count;
                count = Math.Min(count, before);
                break;

            case UIItemAnchorKind.After:
                var after = ChatEditing.IndexOf(messages, request.Anchor.Key);
                start = after >= 0 ? after + 1 : request.Anchor.Key == Live?.Id ? total : total - count;
                break;

            case UIItemAnchorKind.Start:
            default:
                start = 0;
                break;
        }

        start = Math.Clamp(start, 0, total);
        var end = Math.Clamp(start + Math.Max(0, count), start, total);
        List<ChatMessageItem> items = [with(end - start + 1)];

        for (var i = start; i < end; i++)
            items.Add(Live is ChatMessageItem live && live.Id == messages[i].Id ? live : _shape(messages[i]));

        // A new reply is in no message of the chat's yet: it stands at the end, under the last one.
        if (end == total && Live is ChatMessageItem pending && !items.Exists(item => item.Id == pending.Id))
            items.Add(pending);

        return Task.FromResult(new UIItemWindow<ChatMessageItem>(items) { HasMoreBefore = start > 0, HasMoreAfter = end < total });
    }

    /// <summary>Takes what is typed into a message's editor; a row has nothing else the page writes.</summary>
    protected override Task<bool> TryWriteAsync(ChatMessageItem item, string itemProperty, object? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (itemProperty != nameof(ChatMessageItem.EditDraft) || value is not string draft)
            return Task.FromResult(false);

        item.EditDraft = draft;

        return Task.FromResult(true);
    }
}
