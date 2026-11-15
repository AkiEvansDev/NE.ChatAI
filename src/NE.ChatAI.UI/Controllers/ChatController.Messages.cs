using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;

namespace NE.ChatAI.Controllers;

/// <summary>
/// What is done to the messages already there: any message edited in its own place, then kept, left or deleted; from its menu a
/// reply made again or given a new variant, the chat branched into a new one or deleted from there on; a message with variants swiped between them, what
/// followed each coming back with it. The composer's extras do the same to the last message, and continue it.
/// </summary>
internal sealed partial class ChatController
{
    public const string DeleteDialogKey = "chatai-delete-message";
    public const string DeleteFromDialogKey = "chatai-delete-from";
    public const string RefreshDialogKey = "chatai-refresh-message";

    /// <summary>The menus' entries, by what they do.</summary>
    public const string RefreshAction = "refresh";
    public const string VariantAction = "variant";
    public const string ContinueAction = "continue";
    public const string DeleteFromAction = "delete-from";
    public const string BranchAction = "branch";

    /// <summary>The message whose editor is open; one at a time.</summary>
    private string? _editingId;

    /// <summary>Whether the open editor writes a new variant rather than the shown one's words.</summary>
    private bool _editingNew;

    /// <summary>The message a question is open about: deleted, deleted from there on, or made again.</summary>
    private string? _askedId;

    /// <summary>Opens a message's editor in its place, on the shown variant's words.</summary>
    [UICommand]
    public Task<UICommandResult> EditAsync(string messageId, CancellationToken cancellationToken)
        => OpenEditorAsync(messageId, asNew: false, cancellationToken);

    /// <summary>One editor at a time: another one open is closed, its words left as they were. A new variant opens empty.</summary>
    private async Task<UICommandResult> OpenEditorAsync(string messageId, bool asNew, CancellationToken cancellationToken)
    {
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (busy || _chat?.Messages.FirstOrDefault(message => message.Id == messageId) is not ChatMessage message)
                return;

            if (_editingId is string other && other != messageId)
                Messages.Held(other)?.ShowEditor(null);

            CloseOpenRecordEditor();
            _editingId = messageId;
            _editingNew = asNew;
            Messages.Held(messageId)?.ShowEditor(asNew ? string.Empty : message.Current.Text);
            RefreshRealizedRecords();
        }, cancellationToken).ConfigureAwait(false);

        return busy ? Notify("chatai.chat.busy", UIColorStyle.Info) : UICommandResult.Ok();
    }

    /// <summary>
    /// Keeps the editor's words — in place of the shown variant's, or as a new variant, what followed the message kept with the one
    /// shown so far — and closes it.
    /// </summary>
    [UICommand]
    public async Task<UICommandResult> SaveEditAsync(string messageId, CancellationToken cancellationToken)
    {
        string? chatId = null;
        Chat? changed = null;
        var empty = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (_generating || _chat is null || _editingId != messageId || Messages.Held(messageId) is not ChatMessageItem item)
                return;

            var text = item.EditDraft.Trim();

            if (text.Length == 0)
            {
                empty = true;
                return;
            }

            var asNew = _editingNew;
            Chat previous = _chat;

            _editingId = null;
            _editingNew = false;
            item.ShowEditor(null);

            chatId = OpenChatId;
            _chat = asNew ? ChatEditing.AddVariant(previous, messageId, new MessageVariant { Text = text }, null) : ChatEditing.Edit(previous, messageId, text);
            changed = ReferenceEquals(_chat, previous) ? null : _chat;

            if (asNew)
                Messages.Reread();

            RefreshRealizedRecords();
            RefreshLoadedStatistics();
            ShowMessage(messageId);
            ShowLastActions();
        }, cancellationToken).ConfigureAwait(false);

        if (empty)
            return Notify("chatai.chat.edit.empty", UIColorStyle.Warning);

        if (changed is not null && chatId is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }

    /// <summary>A message's row shown as the chat now holds it, and the chat's last words in the list; under the runtime's lock.</summary>
    private void ShowMessage(string messageId)
    {
        if (_chat?.Messages.FirstOrDefault(message => message.Id == messageId) is ChatMessage message && Messages.Held(messageId) is ChatMessageItem item)
            Show(item, message);

        if (OpenChatId is string chatId && _chat is not null)
            ShowLastWords(chatId, _chat.Messages.Count == 0 ? string.Empty : _chat.Messages[^1].Current.Text);
    }

    /// <summary>Closes a message's editor, its words left as they were; a new variant is not made.</summary>
    [UICommand]
    public async Task CancelEditAsync(string messageId, CancellationToken cancellationToken)
        => await Context.Runtime.InvokeAsync(() =>
        {
            if (_editingId == messageId)
            {
                _editingId = null;
                _editingNew = false;
            }

            Messages.Held(messageId)?.ShowEditor(null);
            RefreshRealizedRecords();
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>The editor's Delete: the question before this one message leaves the chat.</summary>
    [UICommand]
    public Task<UICommandResult> AskDeleteAsync(string messageId, CancellationToken cancellationToken)
        => AskAsync(messageId, DeleteDialogKey, cancellationToken);

    /// <summary>A question about a message the chat holds, opened unless a reply is being written.</summary>
    private async Task<UICommandResult> AskAsync(string messageId, string dialogKey, CancellationToken cancellationToken)
    {
        var busy = false;
        var held = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;
            held = _chat?.Messages.Any(message => message.Id == messageId) == true;
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (!held)
            return UICommandResult.Ok();

        _askedId = messageId;

        return UICommandResult.Ok([new OpenDialogEffect(dialogKey)]);
    }

    /// <summary>
    /// An entry of a message's menu: the message made again — asked first when messages follow it, as they go — a new variant of it,
    /// written by hand for a greeting, or the question before the chat is deleted from there on.
    /// </summary>
    [UICommand]
    public async Task<UICommandResult> MessageActionAsync(string action, string messageId, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case RefreshAction:
                var followed = false;

                await Context.Runtime.InvokeAsync(() => followed = _chat is not null && _chat.Messages.Count > 0 && _chat.Messages[^1].Id != messageId, cancellationToken).ConfigureAwait(false);

                return followed ? await AskAsync(messageId, RefreshDialogKey, cancellationToken).ConfigureAwait(false) : await ReplyAgainAsync(messageId, ReplyKind.Remake, cancellationToken).ConfigureAwait(false);

            case VariantAction:
                var greeting = false;

                await Context.Runtime.InvokeAsync(() => greeting = IsGreeting(messageId), cancellationToken).ConfigureAwait(false);

                return greeting ? await OpenEditorAsync(messageId, asNew: true, cancellationToken).ConfigureAwait(false) : await ReplyAgainAsync(messageId, ReplyKind.Variant, cancellationToken).ConfigureAwait(false);

            case DeleteFromAction:
                return await AskAsync(messageId, DeleteFromDialogKey, cancellationToken).ConfigureAwait(false);

            case BranchAction:
                return await BranchAsync(messageId, cancellationToken).ConfigureAwait(false);

            case InfoAction:
                return await OpenMessageInfoAsync(messageId, cancellationToken).ConfigureAwait(false);

            default:
                return UICommandResult.Ok();
        }
    }

    /// <summary>A new chat copied up to and with the message, opened in this one's place; this one stays as it is.</summary>
    private async Task<UICommandResult> BranchAsync(string messageId, CancellationToken cancellationToken)
    {
        Chat? chat = null;
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;
            chat = _chat;
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (chat is null || await Chats.BranchAsync(chat, messageId, cancellationToken).ConfigureAwait(false) is not string branchId)
            return UICommandResult.Ok();

        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);

        return await ShowChatAsync(branchId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The message written again by the model from the messages before it — a new variant, the shown one made again, or more of the
    /// last one's words; a character's reply, never its greeting. Made in the middle of the chat, the messages after it leave the
    /// feed while it is written.
    /// </summary>
    private async Task<UICommandResult> ReplyAgainAsync(string messageId, ReplyKind kind, CancellationToken cancellationToken)
    {
        (ActiveProfile? profile, UICommandResult? refusal) = await ReplyProfileAsync(cancellationToken).ConfigureAwait(false);

        if (profile is null)
            return refusal!;

        ReplyJob? job = null;
        string? savedChatId = null;
        Chat? savedChat = null;
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (busy || _chat is null || OpenChatId is not string chatId || IsGreeting(messageId))
                return;

            var at = IndexOf(messageId);

            if (at < 0 || _chat.Messages[at].Role != MessageRole.Character || (kind == ReplyKind.Continuation && at != _chat.Messages.Count - 1))
                return;

            ChatMessage message = _chat.Messages[at];
            ChatMessageItem item = Messages.Held(messageId) ?? ItemOf(message);

            // The words stream in place of the ones shown; they come back if nothing does.
            if (kind != ReplyKind.Continuation)
            {
                item.Text = string.Empty;
                item.TokenCountVisibility = UIVisibility.Collapsed;
                item.ShowThought(null, _hideThoughts, thinking: false);
            }

            Chat preTurnChat = _chat;

            if (kind == ReplyKind.Remake)
            {
                IReadOnlyList<ChatRecord> keptRecords = ChatRecords.DropInsertionsFrom(_chat.Messages, _chat.Records, at);

                if (!ReferenceEquals(keptRecords, _chat.Records))
                {
                    _chat = savedChat = _chat with { Records = keptRecords };
                    savedChatId = chatId;
                }
            }

            job = BeginReply(profile, chatId, preTurnChat, kind, item, [.. _chat.Messages.Take(at)], kind == ReplyKind.Continuation ? message.Current.Text : null);

            if (at < _chat.Messages.Count - 1)
                Messages.Reread(endsAtLive: true);
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (savedChat is not null && savedChatId is not null)
            await Chats.WriteAsync(savedChatId, savedChat, cancellationToken).ConfigureAwait(false);

        if (job is not null)
            Context.Runtime.Start(lifetime => GenerateAsync(job, lifetime));

        return UICommandResult.Ok();
    }

    /// <summary>Where the message stands in the open chat; -1 for one it does not hold.</summary>
    private int IndexOf(string messageId)
        => _chat is null ? -1 : ChatEditing.IndexOf(_chat.Messages, messageId);

    /// <summary>An entry of the composer's extras: one for the chat itself, else one on its last message — made again, a new variant, or continued.</summary>
    [UICommand]
    public async Task<UICommandResult> ExtraActionAsync(string action, CancellationToken cancellationToken)
    {
        if (await ChatActionAsync(action, cancellationToken).ConfigureAwait(false) is UICommandResult done)
            return done;

        string? lastId = null;

        await Context.Runtime.InvokeAsync(() => lastId = _chat is { Messages.Count: > 0 } chat ? chat.Messages[^1].Id : null, cancellationToken).ConfigureAwait(false);

        if (lastId is null)
            return UICommandResult.Ok();

        return action == ContinueAction
            ? await ReplyAgainAsync(lastId, ReplyKind.Continuation, cancellationToken).ConfigureAwait(false)
            : await MessageActionAsync(action, lastId, cancellationToken).ConfigureAwait(false);
    }

    [UICommand]
    public Task<UICommandResult> PreviousVariantAsync(string messageId, CancellationToken cancellationToken)
        => ChooseAsync(messageId, -1, cancellationToken);

    /// <summary>The next variant; past the last one nothing — a new one is the menu's.</summary>
    [UICommand]
    public Task<UICommandResult> NextVariantAsync(string messageId, CancellationToken cancellationToken)
        => ChooseAsync(messageId, 1, cancellationToken);

    /// <summary>
    /// The variant <paramref name="step"/> away, none past either end, where the row's arrow stands disabled; the feed read again when
    /// what follows the message changes with it.
    /// </summary>
    private async Task<UICommandResult> ChooseAsync(string messageId, int step, CancellationToken cancellationToken)
    {
        string? chatId = null;
        Chat? changed = null;
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (busy || _chat is null || _chat.Messages.FirstOrDefault(message => message.Id == messageId) is not ChatMessage message)
                return;

            var index = message.Chosen + step;

            if (index < 0 || index >= message.Variants.Count)
                return;

            var followed = _chat.Messages[^1].Id != messageId || message.Variants[index].Tail is not null;

            chatId = OpenChatId;
            _chat = changed = ChatEditing.Choose(_chat, messageId, index);

            if (followed)
                Messages.Reread();

            RefreshRealizedRecords();
            RefreshLoadedStatistics();
            ShowMessage(messageId);
            ShowLastActions();
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (changed is not null && chatId is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }

    /// <summary>The question answered Delete: the message asked about leaves the chat, those after it staying.</summary>
    [UICommand]
    public Task<UICommandResult> DeleteMessageAsync(CancellationToken cancellationToken)
        => DeleteAsync(DeleteDialogKey, fromHere: false, cancellationToken);

    /// <summary>The question answered Delete: the message asked about and every one after it leave the chat.</summary>
    [UICommand]
    public Task<UICommandResult> DeleteFromAsync(CancellationToken cancellationToken)
        => DeleteAsync(DeleteFromDialogKey, fromHere: true, cancellationToken);

    private async Task<UICommandResult> DeleteAsync(string dialogKey, bool fromHere, CancellationToken cancellationToken)
    {
        string? chatId = null;
        Chat? changed = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (_generating || _chat is null || _askedId is not string messageId)
                return;

            var at = IndexOf(messageId);
            string[] gone = at < 0 ? [] : [.. _chat.Messages.Skip(at).Take(fromHere ? int.MaxValue : 1).Select(static message => message.Id)];

            chatId = OpenChatId;
            _chat = changed = fromHere ? ChatEditing.DeleteFrom(_chat, messageId) : ChatEditing.Delete(_chat, messageId);
            RefreshRealizedRecords();
            RefreshLoadedStatistics();

            foreach (var id in gone)
            {
                _ = Messages.Drop(id);

                if (_editingId == id)
                {
                    _editingId = null;
                    _editingNew = false;
                }
            }

            ShowMessage(messageId);
            ShowLastActions();
        }, cancellationToken).ConfigureAwait(false);

        _askedId = null;

        if (changed is not null && chatId is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok([new CloseDialogEffect(dialogKey)]);
    }

    /// <summary>The question answered Refresh: the message made again, the messages after it going.</summary>
    [UICommand]
    public async Task<UICommandResult> RefreshMessageAsync(CancellationToken cancellationToken)
    {
        var messageId = _askedId;

        _askedId = null;

        UICommandResult result = messageId is null ? UICommandResult.Ok() : await ReplyAgainAsync(messageId, ReplyKind.Remake, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok([.. result.Effects, new CloseDialogEffect(RefreshDialogKey)]);
    }
}
