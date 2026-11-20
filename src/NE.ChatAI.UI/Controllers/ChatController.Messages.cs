using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;

namespace NE.ChatAI.Controllers;

/// <summary>Message-level actions: inline editing, swiping variants, regenerating/continuing, branching, and deletion.</summary>
internal sealed partial class ChatController
{
    public const string DeleteDialogKey = "chatai-delete-message";
    public const string DeleteFromDialogKey = "chatai-delete-from";
    public const string RefreshDialogKey = "chatai-refresh-message";

    public const string RefreshAction = "refresh";
    public const string VariantAction = "variant";
    public const string ContinueAction = "continue";
    public const string DeleteFromAction = "delete-from";
    public const string BranchAction = "branch";

    private string? _editingId;
    private bool _editingNew;
    private string? _askedId;

    [UICommand]
    public async Task<UICommandResult> OpenAuthorCardAsync(string messageId, CancellationToken cancellationToken)
    {
        TagScope? scope = null;
        string? cardId = null;
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (busy || _chat is null)
                return;

            MessageRole? role = _chat.Messages.FirstOrDefault(message => message.Id == messageId)?.Role
                ?? (Messages.Held(messageId) is ChatMessageItem held ? held.Role : null);

            if (role == MessageRole.Character && _character is not null && _chat.Character is string characterId)
            {
                scope = TagScope.Character;
                cardId = characterId;
            }
            else if (role == MessageRole.User && _persona is not null && _personaId is string personaId)
            {
                scope = TagScope.Persona;
                cardId = personaId;
            }
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        return scope is TagScope targetScope && cardId is not null && await OpenCardEditorAsync(targetScope, cardId, cancellationToken).ConfigureAwait(false) is string dialogKey
            ? UICommandResult.Ok([new OpenDialogEffect(dialogKey)])
            : UICommandResult.Ok();
    }

    [UICommand]
    public Task<UICommandResult> EditAsync(string messageId, CancellationToken cancellationToken)
        => OpenEditorAsync(messageId, asNew: false, cancellationToken);

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

            RefreshVisuals();
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

    private void ShowMessage(string messageId)
    {
        if (_chat?.Messages.FirstOrDefault(message => message.Id == messageId) is ChatMessage message && Messages.Held(messageId) is ChatMessageItem item)
            Show(item, message);

        if (OpenChatId is string chatId && _chat is not null)
            ShowLastWords(chatId, _chat.Messages.Count == 0 ? string.Empty : _chat.Messages[^1].Current.Text);
    }

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

    [UICommand]
    public Task<UICommandResult> AskDeleteAsync(string messageId, CancellationToken cancellationToken)
        => AskAsync(messageId, DeleteDialogKey, cancellationToken);

    private async Task<UICommandResult> AskAsync(string messageId, string dialogKey, CancellationToken cancellationToken)
    {
        var busy = false;
        var held = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;
            held = !busy && _chat?.Messages.Any(message => message.Id == messageId) == true;

            if (held)
                _askedId = messageId;
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        return held ? UICommandResult.Ok([new OpenDialogEffect(dialogKey)]) : UICommandResult.Ok();
    }

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
                IReadOnlyList<ChatVisual> keptVisuals = ChatVisuals.DropFrom(_chat.Messages, _chat.Visuals, at);

                if (!ReferenceEquals(keptRecords, _chat.Records) || !ReferenceEquals(keptVisuals, _chat.Visuals))
                {
                    _chat = savedChat = _chat with { Records = keptRecords, Visuals = keptVisuals };
                    savedChatId = chatId;
                }
            }

            job = BeginReply(profile, chatId, preTurnChat, kind, item, [.. _chat.Messages.Take(at)], kind == ReplyKind.Continuation ? message.Current.Text : null);
            RefreshVisuals();

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

    private int IndexOf(string messageId)
        => _chat is null ? -1 : ChatEditing.IndexOf(_chat.Messages, messageId);

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

    [UICommand]
    public Task<UICommandResult> NextVariantAsync(string messageId, CancellationToken cancellationToken)
        => ChooseAsync(messageId, 1, cancellationToken);

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

            RefreshVisuals();
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

    [UICommand]
    public Task<UICommandResult> DeleteMessageAsync(CancellationToken cancellationToken)
        => DeleteAsync(DeleteDialogKey, fromHere: false, cancellationToken);

    [UICommand]
    public Task<UICommandResult> DeleteFromAsync(CancellationToken cancellationToken)
        => DeleteAsync(DeleteFromDialogKey, fromHere: true, cancellationToken);

    private async Task<UICommandResult> DeleteAsync(string dialogKey, bool fromHere, CancellationToken cancellationToken)
    {
        string? chatId = null;
        Chat? changed = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            var messageId = _askedId;
            _askedId = null;

            if (_generating || _chat is null || messageId is null)
                return;

            var at = IndexOf(messageId);
            string[] gone = at < 0 ? [] : [.. _chat.Messages.Skip(at).Take(fromHere ? int.MaxValue : 1).Select(static message => message.Id)];

            chatId = OpenChatId;
            _chat = changed = fromHere ? ChatEditing.DeleteFrom(_chat, messageId) : ChatEditing.Delete(_chat, messageId);
            RefreshVisuals();
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

            if (_chat.Messages.Count > 0 && (Messages.Items.Count == 0 || (!fromHere && at == 0)))
                Messages.Reread();

            ShowMessage(messageId);
            ShowLastActions();
        }, cancellationToken).ConfigureAwait(false);

        if (changed is not null && chatId is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok([new CloseDialogEffect(dialogKey)]);
    }

    [UICommand]
    public async Task<UICommandResult> RefreshMessageAsync(CancellationToken cancellationToken)
    {
        string? messageId = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            messageId = _askedId;
            _askedId = null;
        }, cancellationToken).ConfigureAwait(false);

        UICommandResult result = messageId is null ? UICommandResult.Ok() : await ReplyAgainAsync(messageId, ReplyKind.Remake, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok([.. result.Effects, new CloseDialogEffect(RefreshDialogKey)]);
    }
}
