using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Chats;

namespace NE.ChatAI.Controllers;

/// <summary>Windowed message source reading the open chat from the end backwards while keeping any streaming <see cref="Live"/> row attached.</summary>
internal sealed class MessageSource : UIItemSourceBase<ChatMessageItem>
{
    private Func<IReadOnlyList<ChatMessage>> _messages = static () => [];
    private Func<ChatMessage, ChatMessageItem>? _shape;

    internal ChatMessageItem? Live { get; set; }

    public bool EndsAtLive { get; private set; }

    public void Attach(Func<IReadOnlyList<ChatMessage>> messages, Func<ChatMessage, ChatMessageItem> shape)
    {
        _messages = messages;
        _shape = shape;
        Live = null;
        EndsAtLive = false;
    }

    public void Reread(bool endsAtLive = false)
    {
        EndsAtLive = endsAtLive;
        Invalidate();
    }

    public ChatMessageItem? Held(string messageId)
        => Find(messageId);

    public void Add(ChatMessageItem item)
    {
        if (!HasMoreAfter)
            Append(item);
    }

    public bool Drop(string messageId)
        => Remove(messageId);

    protected override Task<UIItemWindow<ChatMessageItem>> GetWindowAsync(UIItemWindowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<ChatMessage> messages = _messages();

        if (_shape is null)
            return Task.FromResult(new UIItemWindow<ChatMessageItem>([]));

        var total = EndsAtLive && Live is ChatMessageItem made && ChatEditing.IndexOf(messages, made.Id) is var cut and >= 0 ? cut + 1 : messages.Count;
        var count = Math.Max(0, request.Count);
        int start;

        switch (request.Anchor.Kind)
        {
            case UIItemAnchorKind.End:
                start = total - count;
                break;

            case UIItemAnchorKind.Offset:
                start = request.Anchor.Offset >= 0 && request.Anchor.Offset < total ? request.Anchor.Offset : total - count;
                break;

            case UIItemAnchorKind.Before:
                var foundBefore = IndexWithinTotal(messages, total, request.Anchor.Key);
                var before = foundBefore >= 0 ? foundBefore : total;
                start = before - count;
                count = Math.Min(count, before);
                break;

            case UIItemAnchorKind.After:
                var foundAfter = IndexWithinTotal(messages, total, request.Anchor.Key);
                start = foundAfter >= 0 ? foundAfter + 1 : request.Anchor.Key == Live?.Id ? total : total - count;
                break;

            case UIItemAnchorKind.Start:
            default:
                start = 0;
                break;
        }

        start = Math.Clamp(start, 0, total);
        var end = Math.Clamp(start + count, start, total);
        List<ChatMessageItem> items = [with(end - start + 1)];

        for (var i = start; i < end; i++)
            items.Add(Live is ChatMessageItem live && live.Id == messages[i].Id ? live : _shape(messages[i]));

        if (end == total && Live is ChatMessageItem pending && !items.Exists(item => item.Id == pending.Id))
            items.Add(pending);

        return Task.FromResult(new UIItemWindow<ChatMessageItem>(items) { HasMoreBefore = start > 0, HasMoreAfter = end < total });
    }

    private static int IndexWithinTotal(IReadOnlyList<ChatMessage> messages, int total, string? key)
    {
        if (string.IsNullOrEmpty(key))
            return -1;

        var index = ChatEditing.IndexOf(messages, key);

        return index >= 0 && index < total ? index : -1;
    }

    protected override Task<bool> TryWriteAsync(ChatMessageItem item, string itemProperty, object? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (itemProperty != nameof(ChatMessageItem.EditDraft) || value is not string draft)
            return Task.FromResult(false);

        item.EditDraft = draft;

        return Task.FromResult(true);
    }
}
