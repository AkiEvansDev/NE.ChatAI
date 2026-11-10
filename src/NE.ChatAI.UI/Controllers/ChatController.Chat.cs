using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Backends.KoboldCpp;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>
/// What the open chat is written with, from the composer's extras: its assembled prompt, its own author's note in a dialog, the
/// persona it is written as picked from a list, and the reader's quick phrases, each inserted into the composer draft.
/// </summary>
internal sealed partial class ChatController
{
    public const string NoteDialogKey = "chatai-chat-note";
    public const string PromptDialogKey = "chatai-chat-prompt";

    /// <summary>The extras' entries for the chat itself, and the prefixes their lists' entries carry.</summary>
    public const string PromptAction = "prompt";
    public const string NoteAction = "note";
    public const string PersonaAction = "persona";
    public const string PhrasesAction = "phrases";
    public const string PhrasePrefix = "phrase:";
    private const string PersonaPrefix = "persona:";

    /// <summary>A phrase's entry shows this much of it.</summary>
    private const int PhraseShown = 48;

    /// <summary>The open chat's own author's note, as its dialog edits it.</summary>
    [RecursiveMember]
    public partial string ChatNote { get; set; } = string.Empty;

    /// <summary>The picture behind this chat alone; none, and it takes the character's or the application's.</summary>
    [RecursiveMember]
    public partial PictureSlot ChatBackground { get; set; } = new();

    /// <summary>The character name shown on the sample message over the chat's background preview.</summary>
    [RecursiveMember]
    public partial string ChatBackgroundPreviewAuthor { get; set; } = AppName;

    /// <summary>The character avatar shown on the sample message over the chat's background preview.</summary>
    [RecursiveMember]
    public partial string? ChatBackgroundPreviewAvatar { get; set; }

    /// <summary>How many tokens and history messages the assembled prompt holds.</summary>
    [RecursiveMember]
    public partial UIPhrase? PromptSummary { get; set; }

    /// <summary>The open chat's assembled prompt text, markers and all.</summary>
    [RecursiveMember]
    public partial string PromptText { get; set; } = string.Empty;

    /// <summary>A page found again reads the personas, phrases, background and card presentation afresh.</summary>
    protected override async Task OnAttachedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        await base.OnAttachedAsync(navigation, cancellationToken).ConfigureAwait(false);
        await RefreshOpenChatPresentationAsync(cancellationToken).ConfigureAwait(false);
        await ShowChatListsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The extras' lists: every persona, the chat's own checked and named beside the entry (disabled unless there is another persona
    /// to switch to); every phrase. An empty list's entry stands disabled.
    /// </summary>
    private async Task ShowChatListsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> savedPhrases = (await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false)).Phrases;

        ShowPersonas();

        MenuItem phrases = ExtraOf(PhrasesAction);
        phrases.Items.Clear();

        for (var i = 0; i < savedPhrases.Count; i++)
            phrases.Items.Add(new MenuItem { Id = PhrasePrefix + i.ToString(CultureInfo.InvariantCulture), Title = Shortened(savedPhrases[i]), Value = UIPhrase.Text(savedPhrases[i]), IsContent = true });

        phrases.Enabled = phrases.Items.Count > 0 && _chat is not null;
        await ShowScenarioMenuAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ShowPersonas()
    {
        MenuItem personas = ExtraOf(PersonaAction);

        personas.Value = UIPhrase.Text(UserName);
        personas.Items.Clear();

        foreach (StoredDocument<Persona> stored in KnownPersonas.OrderBy(static stored => stored.Document.Name, StringComparer.CurrentCultureIgnoreCase))
            personas.Items.Add(new MenuItem { Id = PersonaPrefix + stored.Id, Title = stored.Document.Name, IsContent = true, Kind = UIMenuItemKind.Check, Checked = stored.Id == _personaId });

        personas.Enabled = !_generating && personas.Items.Count > 1 && _chat is not null;
    }

    private MenuItem ExtraOf(string id)
        => Extras.TryGet(id, out MenuItem? item) ? item : throw new InvalidOperationException($"Extras menu has no '{id}' entry.");

    /// <summary>A phrase's first line, cut to what an entry shows.</summary>
    private static string Shortened(string phrase)
    {
        ReadOnlySpan<char> line = phrase.AsSpan().Trim();
        var end = line.IndexOfAny('\r', '\n');

        if (end >= 0)
            line = line[..end];

        return line.Length <= PhraseShown ? line.ToString() : string.Concat(line[..PhraseShown].TrimEnd(), "…");
    }

    /// <summary>An extras entry for the chat itself: its statistics, its assembled prompt, its note's dialog, or a persona picked; none for any other.</summary>
    private async Task<UICommandResult?> ChatActionAsync(string action, CancellationToken cancellationToken)
    {
        if (action == StatisticsAction)
            return await OpenStatisticsAsync(cancellationToken).ConfigureAwait(false);

        if (action == PromptAction)
            return await OpenPromptAsync(cancellationToken).ConfigureAwait(false);

        if (await ScenarioActionAsync(action, cancellationToken).ConfigureAwait(false) is UICommandResult scenarioDone)
            return scenarioDone;

        if (action == NoteAction)
            return await OpenChatNoteAsync(cancellationToken).ConfigureAwait(false);

        if (action.StartsWith(PersonaPrefix, StringComparison.Ordinal))
            return await ChoosePersonaAsync(action[PersonaPrefix.Length..], cancellationToken).ConfigureAwait(false);

        return null;
    }

    private async Task<UICommandResult> OpenPromptAsync(CancellationToken cancellationToken)
    {
        Chat? chat = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            chat = _chat;

            if (chat is null)
                return;

            PromptSummary = null;
            PromptText = string.Empty;
        }, cancellationToken).ConfigureAwait(false);

        if (chat is null)
            return UICommandResult.Ok();

        // Send the dialog open effect before building or counting the prompt so the UI opens immediately without waiting on I/O.
        await Context.SendEffectsAsync([new OpenDialogEffect(PromptDialogKey)], cancellationToken).ConfigureAwait(false);

        ActiveProfile profile = await Profiles.LoadActiveAsync(Data, cancellationToken).ConfigureAwait(false)
            ?? new ActiveProfile("default", new ConnectionProfile(), ModelFormat.Gemma4, new SystemPromptPreset(), new PromptPreset(), new SamplerPreset());
        var contextLength = profile.Connection.ContextLength.Value;
        var totalHistory = ChatRecords.ProjectHistory(chat.Messages, chat.Records).Count;
        BuiltPrompt approximate = await BuildPromptAsync(profile, chat, chat.Messages, continued: null, contextLength, kobold: null, model: null, cancellationToken).ConfigureAwait(false);

        await Context.Runtime.InvokeAsync(() => ShowBuiltPrompt(approximate, contextLength, totalHistory), cancellationToken).ConfigureAwait(false);

        if (profile.Connection.Address is not null)
            Context.Runtime.Start(lifetime => RefreshExactPromptAsync(profile, chat, totalHistory, lifetime));

        return UICommandResult.Ok([new OpenDialogEffect(PromptDialogKey)]);
    }

    private void ShowBuiltPrompt(BuiltPrompt built, int contextLength, int totalHistory)
    {
        PromptSummary = UIPhrase.Of(
            "chatai.chat.prompt.summary",
            ("tokens", built.Tokens),
            ("context", contextLength),
            ("counts", ShortCountsPhrase(built.Counts)),
            ("sent", built.MessagesSent),
            ("total", totalHistory)
        );
        PromptText = built.Text;
    }

    private async Task RefreshExactPromptAsync(ActiveProfile profile, Chat chat, int totalHistory, CancellationToken cancellationToken)
    {
        if (profile.Connection.Address is not Uri address)
            return;

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        KoboldCppClient kobold = Context.Services.GetRequiredService<KoboldCppClients>().For(address);
        string? model;
        int contextLength;

        try
        {
            model = await TryGetModelAsync(kobold, timeout.Token).ConfigureAwait(false);

            if (model is null)
                return;

            ResolvedContext resolved = await ContextLengths.ResolveAsync(profile.Connection, kobold, timeout.Token).ConfigureAwait(false);
            contextLength = resolved.Length;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        BuiltPrompt built = await BuildPromptAsync(profile, chat, chat.Messages, continued: null, contextLength, kobold, model, cancellationToken).ConfigureAwait(false);

        await Context.Runtime.InvokeAsync(() =>
        {
            if (ReferenceEquals(_chat, chat))
                ShowBuiltPrompt(built, contextLength, totalHistory);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UICommandResult> OpenChatNoteAsync(CancellationToken cancellationToken)
    {
        var open = false;
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (busy)
                return;

            open = _chat is not null;
            ChatNote = (_chat?.AuthorsNote ?? string.Empty).TrimEnd('\r', '\n');
            ChatBackground.Show(Pictures.ChatBackgroundOf(OpenChatId));
            ChatBackgroundPreviewAuthor = CharacterName;
            ChatBackgroundPreviewAvatar = Pictures.CharacterAvatarOf(_chat?.Character);
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        return open ? UICommandResult.Ok([new OpenDialogEffect(NoteDialogKey)]) : UICommandResult.Ok();
    }

    /// <summary>Keeps the chat's own author's note as it lets go of its value; an empty one is none.</summary>
    [UICommand]
    public async Task SaveChatNoteAsync(CancellationToken cancellationToken)
    {
        string? chatId = null;
        Chat? changed = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (_chat is null)
                return;

            if (_generating)
            {
                ChatNote = (_chat.AuthorsNote ?? string.Empty).TrimEnd('\r', '\n');
                return;
            }

            var note = ChatNote.TrimEnd('\r', '\n');
            ChatNote = note;
            chatId = OpenChatId;
            _chat = changed = _chat with { AuthorsNote = string.IsNullOrWhiteSpace(note) ? null : note };
        }, cancellationToken).ConfigureAwait(false);

        if (changed is not null && chatId is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes in the picture the field just uploaded as the one behind this chat alone.</summary>
    [UICommand]
    public async Task SaveChatBackgroundAsync(CancellationToken cancellationToken)
    {
        if (OpenChatId is not string chatId || _chat is null || _generating || await TakePictureAsync(ChatBackground, cancellationToken).ConfigureAwait(false) is not var (content, contentType))
            return;

        var fileName = await ChatPictures.WriteAsync(Data, chatId, content, contentType, cancellationToken).ConfigureAwait(false);
        _chat = _chat with { Background = fileName };
        await Chats.WriteAsync(chatId, _chat, cancellationToken).ConfigureAwait(false);
        ChatBackground.Show(Pictures.ChatBackgroundOf(chatId));
        Background = Pictures.BackgroundOf(_chat.Character, chatId);
    }

    /// <summary>Takes the chat's own background away; the chat stands on the character's or the application's.</summary>
    [UICommand]
    public async Task RemoveChatBackgroundAsync(CancellationToken cancellationToken)
    {
        if (OpenChatId is not string chatId || _chat is null || _generating)
            return;

        ChatPictures.Delete(Data, chatId);
        _chat = _chat with { Background = null };
        await Chats.WriteAsync(chatId, _chat, cancellationToken).ConfigureAwait(false);
        ChatBackground.Show(Pictures.ChatBackgroundOf(chatId));
        Background = Pictures.BackgroundOf(_chat.Character, chatId);
    }

    /// <summary>The chat written as another persona from now on: the reader's messages read again under its name and picture.</summary>
    private async Task<UICommandResult> ChoosePersonaAsync(string personaId, CancellationToken cancellationToken)
    {
        Persona? persona = Slug.IsValidId(personaId) ? await Data.Personas.ReadAsync(personaId, cancellationToken).ConfigureAwait(false) : null;
        string? chatId = null;
        Chat? changed = null;
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (busy || persona is null || _chat is null)
                return;

            chatId = OpenChatId;
            _chat = changed = _chat with { Persona = personaId };
            _persona = persona;
            _personaId = personaId;
            ActivePersonaId = _personaId;
            ActiveWorldId = ResolveActiveWorldId();
            ShowCards();
            Messages.Reread();
            ShowPersonas();
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (changed is not null && chatId is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }
}
