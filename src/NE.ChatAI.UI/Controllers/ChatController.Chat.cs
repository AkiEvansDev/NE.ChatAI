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
using NE.ChatAI.Scenarios;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>Chat-level extras: assembled prompt viewer, chat author's note, per-chat background, persona switcher, and quick phrases.</summary>
internal sealed partial class ChatController
{
    public const string NoteDialogKey = "chatai-chat-note";
    public const string PromptDialogKey = "chatai-chat-prompt";
    public const string StageImageId = "chatai-stage-image";

    public const string PromptAction = "prompt";
    public const string StageAction = "stage";
    public const string NoteAction = "note";
    public const string PersonaAction = "persona";
    public const string PhrasesAction = "phrases";
    public const string PhrasePrefix = "phrase:";
    private const string PersonaPrefix = "persona:";
    private const int PhraseShown = 48;

    [RecursiveMember]
    public partial string ChatNote { get; set; } = string.Empty;

    [RecursiveMember]
    public partial PictureSlot ChatBackground { get; set; } = new();

    [RecursiveMember]
    public partial string ChatBackgroundPreviewAuthor { get; set; } = AppName;

    [RecursiveMember]
    public partial string? ChatBackgroundPreviewAvatar { get; set; }

    [RecursiveMember]
    public partial UIPhrase? PromptSummary { get; set; }

    [RecursiveMember]
    public partial string PromptText { get; set; } = string.Empty;

    protected override async Task OnAttachedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        await base.OnAttachedAsync(navigation, cancellationToken).ConfigureAwait(false);
        await RefreshOpenChatPresentationAsync(cancellationToken).ConfigureAwait(false);
        await ShowChatListsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ShowChatListsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> savedPhrases = (await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false)).Phrases;
        List<StoredDocument<Scenario>> orderedScenarios = ScenarioExecutor.Order((await Data.Scenarios.ListAsync(cancellationToken).ConfigureAwait(false)).Items);

        await Context.Runtime.InvokeAsync(() =>
        {
            ShowPersonas();

            MenuItem phrases = ExtraOf(PhrasesAction);
            phrases.Items.Clear();

            for (var i = 0; i < savedPhrases.Count; i++)
                phrases.Items.Add(new MenuItem { Id = PhrasePrefix + i.ToString(CultureInfo.InvariantCulture), Title = Shortened(savedPhrases[i]), Value = UIPhrase.Text(savedPhrases[i]), IsContent = true });

            phrases.Enabled = phrases.Items.Count > 0 && _chat is not null;
            _knownScenarios = orderedScenarios;
            RebuildScenarioMenu();
            RefreshRealizedRecords();
        }, cancellationToken).ConfigureAwait(false);
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

    private static string Shortened(string phrase)
    {
        ReadOnlySpan<char> line = phrase.AsSpan().Trim();
        var end = line.IndexOfAny('\r', '\n');

        if (end >= 0)
            line = line[..end];

        return line.Length <= PhraseShown ? line.ToString() : string.Concat(line[..PhraseShown].TrimEnd(), "…");
    }

    private async Task<UICommandResult?> ChatActionAsync(string action, CancellationToken cancellationToken)
    {
        if (action == StatisticsAction)
            return await OpenStatisticsAsync(cancellationToken).ConfigureAwait(false);

        if (action == PromptAction)
            return await OpenPromptAsync(cancellationToken).ConfigureAwait(false);

        if (action == StageAction)
        {
            await Context.Runtime.InvokeAsync(() =>
            {
                if (!_hasActiveStage)
                    return;

                _stageHiddenByUser = !_stageHiddenByUser;
                RefreshVisuals();
            }, cancellationToken).ConfigureAwait(false);

            return UICommandResult.Ok();
        }

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

        // Opens the dialog immediately before token counting runs.
        await Context.SendEffectsAsync([new OpenDialogEffect(PromptDialogKey)], cancellationToken).ConfigureAwait(false);

        ActiveProfile profile = await Profiles.LoadActiveAsync(Data, cancellationToken).ConfigureAwait(false)
            ?? new ActiveProfile("default", new ConnectionProfile(), ModelFormat.Gemma4, new SystemPromptPreset(), new PromptPreset(), new SamplerPreset());
        var contextLength = profile.Connection.ContextLength.Value;
        var totalHistory = ChatRecords.ProjectHistory(chat.Messages, chat.Records).Count;
        BuiltPrompt approximate = await BuildPromptAsync(profile, chat, chat.Messages, continued: null, contextLength, kobold: null, model: null, cancellationToken).ConfigureAwait(false);

        await Context.Runtime.InvokeAsync(() => ShowBuiltPrompt(approximate, contextLength, totalHistory), cancellationToken).ConfigureAwait(false);

        if (profile.Connection.Address is not null)
            Context.Runtime.Start(lifetime => RefreshExactPromptAsync(profile, chat, totalHistory, lifetime));

        return UICommandResult.Ok();
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

        KoboldCppClients clients = Context.Services.GetRequiredService<KoboldCppClients>();
        using IDisposable serverSlot = await clients.AcquireSlotAsync(address, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        KoboldCppClient kobold = clients.For(address);
        string? model;
        int contextLength;

        try
        {
            model = await KoboldCppClients.TryGetModelNameAsync(kobold, timeout.Token).ConfigureAwait(false);

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
            chatId = OpenChatId;
            _chat = changed = _chat with { AuthorsNote = string.IsNullOrWhiteSpace(note) ? null : note };
        }, cancellationToken).ConfigureAwait(false);

        if (changed is not null && chatId is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);
    }

    [UICommand]
    public async Task SaveChatBackgroundAsync(CancellationToken cancellationToken)
    {
        string? chatId = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (!_generating && _chat is not null)
                chatId = OpenChatId;
        }, cancellationToken).ConfigureAwait(false);

        if (chatId is null || await TakePictureAsync(ChatBackground, cancellationToken).ConfigureAwait(false) is not var (content, contentType))
            return;

        var fileName = await ChatPictures.WriteAsync(Data, chatId, content, contentType, cancellationToken).ConfigureAwait(false);
        Chat? changed = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (_generating || _chat is null || OpenChatId != chatId)
                return;

            _chat = changed = _chat with { Background = fileName };
            ChatBackground.Show(Pictures.ChatBackgroundOf(chatId));
            RefreshVisuals();
        }, cancellationToken).ConfigureAwait(false);

        if (changed is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);
    }

    [UICommand]
    public async Task RemoveChatBackgroundAsync(CancellationToken cancellationToken)
    {
        string? chatId = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (!_generating && _chat is not null)
                chatId = OpenChatId;
        }, cancellationToken).ConfigureAwait(false);

        if (chatId is null)
            return;

        ChatPictures.Delete(Data, chatId);
        Chat? changed = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (_generating || _chat is null || OpenChatId != chatId)
                return;

            _chat = changed = _chat with { Background = null };
            ChatBackground.Show(Pictures.ChatBackgroundOf(chatId));
            RefreshVisuals();
        }, cancellationToken).ConfigureAwait(false);

        if (changed is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);
    }

    [UICommand]
    public async Task HideStageAsync(CancellationToken cancellationToken)
        => await Context.Runtime.InvokeAsync(() =>
        {
            _stageHiddenByUser = true;
            RefreshVisuals();
        }, cancellationToken).ConfigureAwait(false);
}
