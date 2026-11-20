using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NE.ChatAI.Backends.KoboldCpp;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Connections;
using NE.ChatAI.Hosting;
using NE.ChatAI.Prompts;
using NE.ChatAI.Samplers;
using NE.ChatAI.Scenarios;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>One message row in the chat feed: author, avatar, folded thought, Markdown text, inline editor, variants, and records.</summary>
internal sealed partial class ChatMessageItem : RecursiveObservable, IBindableItem, IContentItem
{
    private bool _thinking;

    [RecursiveMember(false)]
    public required string Id { get; init; }

    public bool IsContent => true;

    public MessageRole Role { get; init; }

    [RecursiveMember]
    public partial string Author { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string? Avatar { get; set; }

    [RecursiveMember]
    public partial bool CanOpenAuthor { get; set; }

    [RecursiveMember]
    public partial string Text { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility LoadingVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial UIVisibility TextVisibility { get; set; } = UIVisibility.Visible;

    [RecursiveMember]
    public partial string EditDraft { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility EditorVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial UIVisibility ToolsVisibility { get; set; } = UIVisibility.Visible;

    [RecursiveMember(false)]
    public RecursiveCollection<MenuItem> Actions { get; } = [];

    [RecursiveMember]
    public partial string? Thought { get; set; }

    [RecursiveMember]
    public partial UIVisibility ThoughtVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial UIPhrase ThoughtTitle { get; set; } = new("chatai.chat.thought");

    [RecursiveMember]
    public partial string TokenCount { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase? TokenCountTooltip { get; set; }

    [RecursiveMember]
    public partial UIVisibility TokenCountVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial string VariantLabel { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility SwipeVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial bool HasPrevious { get; set; }

    [RecursiveMember]
    public partial bool HasNext { get; set; }

    [RecursiveMember(false)]
    public RecursiveCollection<ChatRecordItem> Records { get; } = [];

    public void ShowLoading(bool loading)
    {
        LoadingVisibility = loading ? UIVisibility.Visible : UIVisibility.Collapsed;
        TextVisibility = loading || EditorVisibility == UIVisibility.Visible ? UIVisibility.Collapsed : UIVisibility.Visible;
    }

    public void ShowEditor(string? draft)
    {
        EditDraft = draft ?? string.Empty;
        LoadingVisibility = UIVisibility.Collapsed;
        TextVisibility = draft is null ? UIVisibility.Visible : UIVisibility.Collapsed;
        EditorVisibility = draft is null ? UIVisibility.Collapsed : UIVisibility.Visible;
        ToolsVisibility = TextVisibility;
    }

    public void ShowThought(string? thought, bool hidden, bool thinking)
    {
        Thought = string.IsNullOrEmpty(thought) ? null : thought;
        ThoughtVisibility = Thought is null || hidden ? UIVisibility.Collapsed : UIVisibility.Visible;

        if (thinking != _thinking)
        {
            _thinking = thinking;
            ThoughtTitle = new UIPhrase(thinking ? "chatai.chat.thinking" : "chatai.chat.thought");
        }
    }

    public void RefreshThought(bool hidden)
        => ShowThought(Thought, hidden, _thinking);
}

/// <summary>Home chat controller (<c>/</c>): manages the open chat, streams model replies, and coordinates triggered scenarios.</summary>
internal sealed partial class ChatController : ShellController
{
    public const string FeedId = "chatai-feed";
    public const string ComposerId = "chatai-composer";
    public const string ComposerFormId = "chatai-composer-form";
    public const string StageSplitId = "chatai-stage-split";
    public const string StageSplitterId = "chatai-stage-splitter";
    public const int WindowSize = 30;

    public static readonly UIResponsive<UIGridPlacement> FullChatPlacement = UIResponsive<UIGridPlacement>.Create(new UIGridPlacement(1, 1, 24, 3), xl: new UIGridPlacement(1, 1, 24, 1));
    public static readonly UIResponsive<UIGridPlacement> SplitChatPlacement = UIResponsive<UIGridPlacement>.Create(new UIGridPlacement(1, 3, 24, 1), xl: new UIGridPlacement(12, 1, 13, 1));

    private const string DefaultUserName = "User";

    private readonly ActiveGenerations _activeGenerations = new();
    private bool _generating;
    private ReplyKind? _liveKind;
    private CancellationTokenSource? _generation;

    private Chat? _chat;
    private Character? _character;
    private IReadOnlyDictionary<string, string>? _globalImageTags;
    private Persona? _persona;
    private string? _personaId;
    private bool _hideThoughts;
    private bool _stageHiddenByUser;
    private bool _hasActiveStage;

    [RecursiveMember(false)]
    public MessageSource Messages { get; } = new();

    [RecursiveMember(false)]
    public RecursiveCollection<MenuItem> Extras { get; } =
    [
        Entry(RefreshAction, AppIcons.Regenerate, "chatai.chat.regenerate"),
        Entry(ContinueAction, AppIcons.Continue, "chatai.chat.continue"),
        Entry(VariantAction, AppIcons.Variant, "chatai.chat.variant"),
        Entry(ImpersonateAction, AppIcons.Impersonate, "chatai.chat.extras.impersonate"),
        new MenuItem { Id = "chat-separator", Kind = UIMenuItemKind.Separator },
        Entry(ScenariosAction, AppIcons.Scenarios, "chatai.chat.extras.scenarios"),
        new MenuItem { Id = StageAction, Title = "chatai.chat.extras.stage", Icon = AppIcons.Outline(AppIcons.Stage), Kind = UIMenuItemKind.Check, Enabled = false },
        new MenuItem { Id = PersonaAction, Title = "chatai.chat.extras.persona", Icon = AppIcons.Outline(AppIcons.Persona), Kind = UIMenuItemKind.Select },
        Entry(NoteAction, AppIcons.AuthorNotes, "chatai.chat.extras.author-notes"),
        new MenuItem { Id = "chat-context-separator", Kind = UIMenuItemKind.Separator },
        Entry(StatisticsAction, AppIcons.Statistics, "chatai.chat.extras.statistics"),
        Entry(PromptAction, AppIcons.Prompt, "chatai.chat.extras.prompt"),
        new MenuItem { Id = "chat-inspect-separator", Kind = UIMenuItemKind.Separator },
        Entry(PhrasesAction, AppIcons.Phrases, "chatai.chat.extras.phrases")
    ];

    private static MenuItem Entry(string id, string icon, string title)
        => new() { Id = id, Title = title, Icon = AppIcons.Outline(icon) };

    [RecursiveMember]
    public partial string Draft { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string SendIcon { get; set; } = UIStandardAction.Send.Icon;

    [RecursiveMember]
    public partial UIPhrase SendTooltip { get; set; } = new(UIStandardAction.Send.Title);

    [RecursiveMember]
    public partial UIPhrase? EmptyFeed { get; set; } = new UIPhrase("chatai.chat.none");

    [RecursiveMember]
    public partial string? Background { get; set; }

    [RecursiveMember]
    public partial string? StageImage { get; set; }

    [RecursiveMember]
    public partial UIVisibility StageVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial UIResponsive<UIGridPlacement> ChatPlacement { get; set; } = FullChatPlacement;

    [RecursiveMember]
    public partial UIThemeColor? MessageBackground { get; set; }

    [RecursiveMember]
    public partial decimal? ChatSeed { get; set; }

    protected override bool IsGenerating => _generating;

    private string CharacterName => _character?.Name ?? AppName;

    private string UserName => string.IsNullOrWhiteSpace(_persona?.Name) ? DefaultUserName : _persona.Name;

    protected override async Task OnProfileLoadedAsync(ActiveProfile? profile, bool runtimeActive, CancellationToken cancellationToken)
    {
        var hide = profile?.Prompt.Thoughts.Show == ThoughtDisplay.Hidden;

        if (!runtimeActive)
        {
            _hideThoughts = hide;
            return;
        }

        await Context.Runtime.InvokeAsync(() =>
        {
            _hideThoughts = hide;

            foreach (ChatMessageItem item in Messages.Items)
                item.RefreshThought(_hideThoughts);
        }, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task OnNavigatedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        if (_generating)
            return;

        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        _globalImageTags = settings.ImageTags;
        var chatId = navigation.TryGetParameter(AppRoutes.ChatParameter, out var named) ? named : OpenChatId ?? settings.LastChat;

        if (chatId is not null && chatId == OpenChatId && _chat is not null)
            return;

        if (chatId is not null && await OpenAsync(chatId, cancellationToken).ConfigureAwait(false))
            return;

        if (_chat is not null)
            await ClearOpenChatAsync(cancellationToken).ConfigureAwait(false);
        else
            RefreshVisuals();
    }

    private void RefreshVisuals()
    {
        if (_chat is null)
        {
            Background = Pictures.BackgroundOf(null);
            _hasActiveStage = false;
            StageImage = null;
            StageVisibility = UIVisibility.Collapsed;
            ChatPlacement = FullChatPlacement;
            SyncStageMenuItem();
            return;
        }

        var scenarioBg = ChatVisuals.ResolveActive(_chat.Messages, _chat.Visuals, ChatVisualTarget.Background);
        Background = (scenarioBg is null ? null : Pictures.VisualAddressOf(scenarioBg, _chat.Character, _character?.ImageTags, _globalImageTags))
            ?? Pictures.BackgroundOf(_chat.Character, OpenChatId);

        var scenarioStage = ChatVisuals.ResolveActive(_chat.Messages, _chat.Visuals, ChatVisualTarget.Stage);
        var stageAddress = scenarioStage is null ? null : Pictures.VisualAddressOf(scenarioStage, _chat.Character, _character?.ImageTags, _globalImageTags);
        _hasActiveStage = stageAddress is not null;

        if (!_hasActiveStage)
            _stageHiddenByUser = false;

        var showStage = _hasActiveStage && !_stageHiddenByUser;
        StageImage = showStage ? stageAddress : null;
        StageVisibility = showStage ? UIVisibility.Visible : UIVisibility.Collapsed;
        ChatPlacement = showStage ? SplitChatPlacement : FullChatPlacement;
        SyncStageMenuItem();
    }

    private void SyncStageMenuItem()
    {
        if (!Extras.TryGet(StageAction, out MenuItem? item))
            return;

        item.Enabled = _hasActiveStage;
        item.Checked = _hasActiveStage && !_stageHiddenByUser;
    }

    protected override async Task<UICommandResult> ShowChatAsync(string chatId, CancellationToken cancellationToken)
    {
        if (chatId == OpenChatId)
            return UICommandResult.Ok();

        if (_generating)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        return await OpenAsync(chatId, cancellationToken).ConfigureAwait(false)
            ? UICommandResult.Ok([new PushAddressEffect(new Dictionary<string, object?> { [AppRoutes.ChatParameter] = chatId })])
            : Notify("chatai.chats.chat-gone", UIColorStyle.Warning);
    }

    protected override async Task OnCardChangedAsync(CancellationToken cancellationToken)
    {
        await RefreshOpenChatPresentationAsync(cancellationToken).ConfigureAwait(false);
        await ShowChatListsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshOpenChatPresentationAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        Chat? snapshotChat = null;
        string? snapshotChatId = null;
        var generating = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            snapshotChat = _chat;
            snapshotChatId = OpenChatId;
            generating = _generating;
        }, cancellationToken).ConfigureAwait(false);

        if (snapshotChat is not null && snapshotChatId is not null && !Data.Chats.Exists(snapshotChatId) && !generating)
        {
            await ClearOpenChatAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Chat? persisted = !generating && snapshotChat is not null && snapshotChatId is not null
            ? await Chats.ReadAsync(snapshotChatId, cancellationToken).ConfigureAwait(false)
            : null;
        Chat? effectiveChat = persisted ?? snapshotChat;
        Character? character = effectiveChat?.Character is string characterId ? await Data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) : null;
        var personaId = effectiveChat is null
            ? null
            : effectiveChat.Persona is string own && Slug.IsValidId(own) && Data.Personas.Exists(own) ? own : DefaultPersonaId;
        Persona? persona = personaId is null ? null : await Data.Personas.ReadAsync(personaId, cancellationToken).ConfigureAwait(false);
        var needsWindow = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            _globalImageTags = settings.ImageTags;
            ApplyChatSurface(settings.MessageBackground);
            BackgroundDim = Math.Clamp(settings.BackgroundDim, 0, 1);

            if (_chat is null)
            {
                OpenCharacterId = null;
                ActivePersonaId = null;
                ActiveWorldId = null;
                RefreshVisuals();
                ShowCards();
                return;
            }

            if (!_generating && persisted is not null && OpenChatId == snapshotChatId)
                _chat = persisted;

            OpenCharacterId = _chat.Character;
            _character = character;
            _personaId = _chat.Persona is string ownPersona && Slug.IsValidId(ownPersona) && Data.Personas.Exists(ownPersona) ? ownPersona : DefaultPersonaId;
            _persona = _personaId == personaId ? persona : _persona;
            ActivePersonaId = _personaId;
            ActiveWorldId = ResolveActiveWorldId();
            RefreshVisuals();
            ShowCards();

            needsWindow = !_generating && Messages.Items.Count == 0 && (_chat.Messages.Count > 0 || Messages.Live is not null);
            RefreshMessageAuthors();
        }, cancellationToken).ConfigureAwait(false);

        if (needsWindow)
        {
            await Messages.LoadWindowAsync(new UIItemWindowRequest(UIItemAnchor.End, WindowSize), cancellationToken).ConfigureAwait(false);
            await Context.Runtime.InvokeAsync(RefreshMessageAuthors, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RefreshMessageAuthors()
    {
        if (_chat is null)
            return;

        var characterAvatar = Pictures.CharacterAvatarOf(_chat.Character);
        var personaAvatar = Pictures.PersonaAvatarOf(_personaId);

        foreach (ChatMessageItem item in Messages.Items)
        {
            item.Author = item.Role == MessageRole.User ? UserName : CharacterName;
            item.Avatar = item.Role == MessageRole.Character ? characterAvatar : personaAvatar;
            item.CanOpenAuthor = !_generating && HasAuthorCard(item.Role);
        }
    }

    protected override void ApplyChatSurface(bool enabled)
    {
        base.ApplyChatSurface(enabled);
        MessageBackground = ChatSurface;
    }

    private string? ResolveActiveWorldId()
    {
        foreach (var id in (string?[])[_character?.World, _persona?.World])
        {
            if (id is not null && KnownWorlds.Any(stored => stored.Id == id))
                return id;
        }

        return null;
    }

    private bool HasAuthorCard(MessageRole role)
        => role == MessageRole.Character ? _character is not null : _persona is not null;

    protected override async Task<UICommandResult> OnPanelCardDeletedAsync(string list, string cardId, CancellationToken cancellationToken)
    {
        if (list == CharactersList && _chat?.Character == cardId && !_generating)
            await ClearOpenChatAsync(cancellationToken).ConfigureAwait(false);

        await ShowChatListsAsync(cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }

    protected override async Task<UICommandResult> OnPanelChatDeletedAsync(string chatId, CancellationToken cancellationToken)
    {
        if (chatId != OpenChatId || _generating)
            return UICommandResult.Ok();

        if (ChatEntries.Count > 0 && await OpenAsync(ChatEntries[0].Id, cancellationToken).ConfigureAwait(false))
            return UICommandResult.Ok([new PushAddressEffect(new Dictionary<string, object?> { [AppRoutes.ChatParameter] = ChatEntries[0].Id })]);

        await ClearOpenChatAsync(cancellationToken).ConfigureAwait(false);
        await ShowChatListsAsync(cancellationToken).ConfigureAwait(false);

        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        if (settings.LastChat == chatId)
            await Data.WriteSettingsAsync(settings with { LastChat = null }, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok([new PushAddressEffect(new Dictionary<string, object?>())]);
    }

    private async Task ClearOpenChatAsync(CancellationToken cancellationToken)
    {
        _chat = null;
        _character = null;
        _persona = null;
        _personaId = null;
        OpenCharacterId = null;
        ActivePersonaId = null;
        ActiveWorldId = null;
        _editingId = null;
        _editingNew = false;
        _liveKind = null;
        _askedId = null;
        _editingRecordId = null;
        _streamingRecords.Clear();
        _draftGenerationId = null;
        _draftContinuations.Clear();
        _stageHiddenByUser = false;
        ChatSeed = null;
        OpenChatId = null;
        ChatBackground.Show(null);
        EmptyFeed = new UIPhrase("chatai.chat.none");
        RefreshVisuals();
        ShowCards();
        Messages.Attach(static () => [], ItemOf);
        await Messages.LoadWindowAsync(new UIItemWindowRequest(UIItemAnchor.End, WindowSize), cancellationToken).ConfigureAwait(false);
        ShowLastActions();
        RefreshLoadedStatistics();
    }

    private async Task<bool> OpenAsync(string chatId, CancellationToken cancellationToken)
    {
        if (await Chats.ReadAsync(chatId, cancellationToken).ConfigureAwait(false) is not Chat chat)
            return false;

        _chat = chat;
        _editingId = null;
        _editingNew = false;
        _liveKind = null;
        _askedId = null;
        _editingRecordId = null;
        _streamingRecords.Clear();
        _draftGenerationId = null;
        _draftContinuations.Clear();
        _stageHiddenByUser = false;
        ChatSeed = chat.Seed;
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        _globalImageTags = settings.ImageTags;
        ApplyChatSurface(settings.MessageBackground);

        _character = chat.Character is string characterId ? await Data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) : null;
        _personaId = chat.Persona is string own && Slug.IsValidId(own) && Data.Personas.Exists(own) ? own : DefaultPersonaId;
        OpenChatId = chatId;
        ChatBackground.Show(Pictures.ChatBackgroundOf(chatId));
        RefreshVisuals();
        _persona = _personaId is null ? null : await Data.Personas.ReadAsync(_personaId, cancellationToken).ConfigureAwait(false);
        ActivePersonaId = _personaId;
        ActiveWorldId = ResolveActiveWorldId();
        await FollowChatAsync(chatId, chat.Character, cancellationToken).ConfigureAwait(false);
        EmptyFeed = new UIPhrase("chatai.chat.empty");

        Messages.Attach(() => _chat?.Messages ?? [], ItemOf);
        await Messages.LoadWindowAsync(new UIItemWindowRequest(UIItemAnchor.End, WindowSize), cancellationToken).ConfigureAwait(false);
        ShowLastActions();
        RefreshLoadedStatistics();
        await ShowChatListsAsync(cancellationToken).ConfigureAwait(false);

        if (settings.LastChat != chatId)
            await Data.WriteSettingsAsync(settings with { LastChat = chatId }, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private ChatMessageItem ItemOf(ChatMessage message)
    {
        ChatMessageItem item = RowOf(message.Id, message.Role, IsGreeting(message.Id));

        Show(item, message);

        return item;
    }

    private ChatMessageItem RowOf(string id, MessageRole role, bool greeting)
    {
        ChatMessageItem item = new()
        {
            Id = id,
            Role = role,
            Author = role == MessageRole.User ? UserName : CharacterName,
            Avatar = role == MessageRole.Character ? Pictures.CharacterAvatarOf(_chat?.Character) : Pictures.PersonaAvatarOf(_personaId),
            CanOpenAuthor = !_generating && HasAuthorCard(role)
        };

        if (role == MessageRole.Character && !greeting)
            item.Actions.Add(Entry(RefreshAction, AppIcons.Regenerate, "chatai.chat.regenerate"));

        if (role == MessageRole.Character)
        {
            item.Actions.Add(Entry(VariantAction, AppIcons.Variant, "chatai.chat.variant"));
            item.Actions.Add(new MenuItem { Id = "message-history-separator", Kind = UIMenuItemKind.Separator });
        }

        item.Actions.Add(Entry(BranchAction, AppIcons.Branch, "chatai.chat.branch"));
        item.Actions.Add(Entry(DeleteFromAction, AppIcons.DeleteFrom, "chatai.chat.delete-from"));

        if (role == MessageRole.Character && !greeting)
        {
            item.Actions.Add(new MenuItem { Id = "message-info-separator", Kind = UIMenuItemKind.Separator });
            item.Actions.Add(new MenuItem { Id = InfoAction, Title = "chatai.chat.info", Icon = AppIcons.Outline(AppIcons.Info), Enabled = false });
        }

        return item;
    }

    private bool IsGreeting(string messageId)
        => _chat is { Messages.Count: > 0 } chat && chat.Messages[0].Id == messageId && chat.Messages[0].Role == MessageRole.Character;

    private void Show(ChatMessageItem item, ChatMessage message)
    {
        var chosen = Math.Clamp(message.Chosen, 0, Math.Max(0, message.Variants.Count - 1));
        MessageGenerationSummary? generation = _chat is null ? null : ChatStatistics.ForMessage(_chat, message.Id);
        var tokens = generation is { ReplyTokens: > 0 } ? generation.ReplyTokens : ApproximateTokenCounter.Estimate(message.Current.Text);
        TokenCounts counts = generation is { ReplyTokens: > 0 } ? generation.Counts : TokenCounts.Approximate;

        item.Text = message.Current.Text;
        item.CanOpenAuthor = !_generating && HasAuthorCard(message.Role);

        if (!_generating && _editingId == message.Id)
        {
            item.ShowEditor(item.EditorVisibility == UIVisibility.Visible ? item.EditDraft : (_editingNew ? string.Empty : message.Current.Text));
        }
        else
        {
            if (item.EditorVisibility == UIVisibility.Visible)
                item.ShowEditor(null);

            item.ShowLoading(false);
            item.ToolsVisibility = !_generating ? UIVisibility.Visible : UIVisibility.Collapsed;
        }

        item.ShowThought(message.Current.Thought, _hideThoughts, thinking: false);
        item.TokenCount = tokens > 0 ? tokens.ToString(CultureInfo.InvariantCulture) : string.Empty;
        item.TokenCountTooltip = tokens > 0 ? UIPhrase.Of("chatai.chat.tokens.tooltip", ("tokens", tokens), ("counts", ShortCountsPhrase(counts))) : null;
        item.TokenCountVisibility = tokens > 0 ? UIVisibility.Visible : UIVisibility.Collapsed;
        item.VariantLabel = string.Create(CultureInfo.InvariantCulture, $"{chosen + 1}/{message.Variants.Count}");
        item.SwipeVisibility = message.Variants.Count > 1 ? UIVisibility.Visible : UIVisibility.Collapsed;
        item.HasPrevious = !_generating && chosen > 0;
        item.HasNext = !_generating && chosen < message.Variants.Count - 1;

        var hasInfo = generation is not null;

        if (item.Actions.TryGet(InfoAction, out MenuItem? info))
        {
            info.Enabled = hasInfo;
        }
        else if (hasInfo)
        {
            item.Actions.Add(new MenuItem { Id = "message-info-separator", Kind = UIMenuItemKind.Separator });
            item.Actions.Add(Entry(InfoAction, AppIcons.Info, "chatai.chat.info"));
        }

        SyncRecords(item);
    }

    private void ShowLastActions()
    {
        ChatMessage? last = _generating || _chat is null || _chat.Messages.Count == 0 ? null : _chat.Messages[^1];
        var reply = last?.Role == MessageRole.Character;
        var greeting = reply && IsGreeting(last!.Id);
        var canRunScenario = !_generating && _chat is not null;

        foreach (MenuItem entry in Extras)
        {
            entry.Enabled = entry.Id switch
            {
                RefreshAction or ContinueAction => reply && !greeting,
                VariantAction => reply,
                ImpersonateAction => canRunScenario,
                PersonaAction => !_generating && _chat is not null && entry.Items.Count > 1,
                NoteAction => !_generating && _chat is not null,
                StatisticsAction or PromptAction => _chat is not null,
                StageAction => _hasActiveStage,
                ScenariosAction => !_generating,
                _ => entry.Id.StartsWith(PinnedScenarioPrefix, StringComparison.Ordinal) ? canRunScenario : entry.Enabled
            };
        }

        RefreshScenarioMenuEnabled();
    }

    [UICommand]
    public async Task<UICommandResult> SendOrStopAsync(CancellationToken cancellationToken)
    {
        var generating = false;

        await Context.Runtime.InvokeAsync(() => generating = _generating, cancellationToken).ConfigureAwait(false);

        if (!generating)
            return await SendAsync(cancellationToken).ConfigureAwait(false);

        await StopAsync(cancellationToken).ConfigureAwait(false);
        return UICommandResult.Ok();
    }

    [UICommand]
    public async Task<UICommandResult> SendAsync(CancellationToken cancellationToken)
    {
        (ActiveProfile? profile, UICommandResult? refusal) = await ReplyProfileAsync(cancellationToken).ConfigureAwait(false);

        if (profile is null)
            return refusal!;

        List<StoredDocument<Scenario>> beforeSend = await CandidatesForAsync(ScenarioTriggerKind.BeforeSend, cancellationToken).ConfigureAwait(false);
        BeforeSendJob? beforeJob = null;
        ReplyJob? job = null;
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (_generating || _chat is null || OpenChatId is not string chatId)
                return;

            var text = Draft.Trim();

            // Sending an empty draft when the last turn is already the user's asks the character to answer it.
            if (text.Length == 0 && (_chat.Messages.Count == 0 || _chat.Messages[^1].Role != MessageRole.User))
                return;

            if (text.Length > 0 && beforeSend.Count > 0)
            {
                beforeJob = BeginBeforeSend(profile, chatId, beforeSend);
                return;
            }

            job = AppendUserMessageAndBeginReply(profile, chatId, text);
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (beforeJob is not null)
        {
            Context.Runtime.Start(lifetime => SendAfterScenariosAsync(beforeJob, lifetime));
            return UICommandResult.Ok();
        }

        if (job is null)
            return UICommandResult.Ok();

        await Chats.WriteAsync(job.ChatId, job.Chat, cancellationToken).ConfigureAwait(false);
        Context.Runtime.Start(lifetime => GenerateAsync(job, lifetime));

        return UICommandResult.Ok();
    }

    private async Task<(ActiveProfile? Profile, UICommandResult? Refusal)> ReplyProfileAsync(CancellationToken cancellationToken)
    {
        if (OpenChatId is null)
            return (null, Notify("chatai.chat.none", UIColorStyle.Info));

        if (await Profiles.LoadActiveAsync(Data, cancellationToken).ConfigureAwait(false) is not ActiveProfile profile || profile.Connection.Address is null)
            return (null, Notify("chatai.connection.none", UIColorStyle.Warning));

        return (profile, null);
    }

    private BeforeSendJob BeginBeforeSend(ActiveProfile profile, string chatId, IReadOnlyList<StoredDocument<Scenario>> scenarios)
    {
        EnterGeneratingState(_chat!);

        return new BeforeSendJob(profile, chatId, scenarios);
    }

    private void EnterGeneratingState(Chat baselineChat)
    {
        _generating = true;
        CaptureHiddenBackupRecords(baselineChat);
        CloseOpenEditors();
        RefreshRealizedMessages();
        RefreshRealizedRecords();
        ShowSendButton(stops: true);
        ShowLastActions();
    }

    private void CloseOpenEditors()
    {
        if (_editingId is string messageId)
        {
            Messages.Held(messageId)?.ShowEditor(null);
            _editingId = null;
            _editingNew = false;
        }

        CloseOpenRecordEditor();
        _askedId = null;
    }

    private void RefreshRealizedMessages()
    {
        foreach (ChatMessageItem item in Messages.Items)
        {
            if (ReferenceEquals(item, Messages.Live))
            {
                item.CanOpenAuthor = false;
                item.ToolsVisibility = UIVisibility.Collapsed;
                item.HasPrevious = false;
                item.HasNext = false;

                if (_chat?.Messages.FirstOrDefault(message => message.Id == item.Id) is ChatMessage liveMessage)
                {
                    var total = _liveKind == ReplyKind.Variant ? liveMessage.Variants.Count + 1 : liveMessage.Variants.Count;
                    var currentIndex = _liveKind == ReplyKind.Variant ? total : Math.Clamp(liveMessage.Chosen, 0, Math.Max(0, liveMessage.Variants.Count - 1)) + 1;
                    item.VariantLabel = string.Create(CultureInfo.InvariantCulture, $"{currentIndex}/{total}");
                    item.SwipeVisibility = total > 1 ? UIVisibility.Visible : UIVisibility.Collapsed;
                }
                else
                {
                    item.SwipeVisibility = UIVisibility.Collapsed;
                }

                continue;
            }

            item.CanOpenAuthor = !_generating && HasAuthorCard(item.Role);
            item.ToolsVisibility = !_generating && _editingId != item.Id ? UIVisibility.Visible : UIVisibility.Collapsed;

            if (_chat?.Messages.FirstOrDefault(message => message.Id == item.Id) is ChatMessage current)
            {
                var chosen = Math.Clamp(current.Chosen, 0, Math.Max(0, current.Variants.Count - 1));
                item.SwipeVisibility = current.Variants.Count > 1 ? UIVisibility.Visible : UIVisibility.Collapsed;
                item.HasPrevious = !_generating && chosen > 0;
                item.HasNext = !_generating && chosen < current.Variants.Count - 1;
            }
        }
    }

    private void ShowSendButton(bool stops)
    {
        UIStandardAction action = stops ? UIStandardAction.Stop : UIStandardAction.Send;

        SendIcon = action.Icon;
        SendTooltip = new UIPhrase(action.Title);
    }

    private ReplyJob AppendUserMessageAndBeginReply(ActiveProfile profile, string chatId, string text, int? beforeSendGenerationStart = null)
    {
        Chat preTurnChat = _chat!;
        ChatMessageItem reply = RowOf(ChatLibrary.NewMessageId(), MessageRole.Character, greeting: false);
        var scrolledUp = Messages.HasMoreAfter;

        if (text.Length > 0)
        {
            var draftGeneration = _draftGenerationId;
            IReadOnlyList<string>? draftContinuations = _draftContinuations.Count > 0 ? [.. _draftContinuations] : null;
            ChatMessage message = new()
            {
                Id = ChatLibrary.NewMessageId(),
                Role = MessageRole.User,
                Variants = [new MessageVariant { Text = text, Generation = draftGeneration, Continuations = draftContinuations }],
                At = DateTimeOffset.UtcNow
            };

            Chat updated = ChatEditing.Append(_chat!, message, null, _chat!.Seed);

            if (beforeSendGenerationStart is int genStart && genStart < updated.Generations.Count)
                updated = StampAnchorOnNewGenerations(updated, genStart, message.Id);

            _chat = updated;
            Draft = string.Empty;
            _draftGenerationId = null;
            _draftContinuations.Clear();

            Messages.Add(ItemOf(message));
            ShowLastWords(chatId, text, bumpToTop: true);
        }

        Messages.Add(reply);
        ReplyJob job = BeginReply(profile, chatId, preTurnChat, ReplyKind.Message, reply, _chat!.Messages, null);

        if (scrolledUp)
            Messages.Reread();

        return job;
    }

    private static Chat StampAnchorOnNewGenerations(Chat chat, int startIndex, string anchorMessageId)
    {
        List<GenerationRecord> generations = [.. chat.Generations];

        for (var i = Math.Max(0, startIndex); i < generations.Count; i++)
            generations[i] = generations[i] with { AnchorMessage = anchorMessageId };

        return chat with { Generations = generations };
    }

    private ReplyJob BeginReply(ActiveProfile profile, string chatId, Chat preTurnChat, ReplyKind kind, ChatMessageItem item, IReadOnlyList<ChatMessage> history, string? continued)
    {
        _liveKind = kind;
        Messages.Live = item;
        item.ShowLoading(kind != ReplyKind.Continuation);
        EnterGeneratingState(preTurnChat);

        return new ReplyJob(profile, chatId, _chat!, preTurnChat, kind, item, history, continued);
    }

    private async Task SendAfterScenariosAsync(BeforeSendJob beforeJob, CancellationToken lifetime)
    {
        ReplyJob? replyJob = null;

        try
        {
            using CancellationTokenSource generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Chat? startChat = null;

            await Context.Runtime.InvokeAsync(() =>
            {
                _generation = generation;
                startChat = _chat;
            }, lifetime).ConfigureAwait(false);

            if (startChat is null)
            {
                if (!lifetime.IsCancellationRequested)
                    await ReleaseGenerationAsync().ConfigureAwait(false);

                return;
            }

            ScenarioBatchResult batch = await RunBeforeSendBatchAsync(beforeJob.ChatId, startChat, beforeJob.Scenarios, generation.Token).ConfigureAwait(false);

            if (!batch.AllSucceeded || generation.IsCancellationRequested)
            {
                if (!lifetime.IsCancellationRequested)
                    await ReleaseGenerationAsync().ConfigureAwait(false);

                return;
            }

            var startGenerations = startChat.Generations.Count;

            await Context.Runtime.InvokeAsync(() =>
            {
                _generation = null;

                if (_chat is null || OpenChatId != beforeJob.ChatId)
                    return;

                ApplyCommittedBeforeSendBatch(batch);

                var text = Draft.Trim();

                if (text.Length == 0)
                    return;

                replyJob = AppendUserMessageAndBeginReply(beforeJob.Profile, beforeJob.ChatId, text, startGenerations);
                RefreshVisuals();
                RefreshRealizedRecords();
                RefreshLoadedStatistics();
            }, lifetime).ConfigureAwait(false);
        }
        finally
        {
            if (lifetime.IsCancellationRequested)
                await _activeGenerations.AbortAllAsync(exception => Context.Logger.LogBackendFailure(exception), CancellationToken.None).ConfigureAwait(false);
        }

        if (replyJob is null)
        {
            if (!lifetime.IsCancellationRequested)
                await ReleaseGenerationAsync().ConfigureAwait(false);

            return;
        }

        await Chats.WriteAsync(replyJob.ChatId, replyJob.Chat, lifetime).ConfigureAwait(false);
        await GenerateAsync(replyJob, lifetime).ConfigureAwait(false);
    }

    private async Task GenerateAsync(ReplyJob job, CancellationToken lifetime)
    {
        using CancellationTokenSource generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        ActiveProfile profile = job.Profile;
        KoboldCppClients clients = Context.Services.GetRequiredService<KoboldCppClients>();
        Uri address = profile.Connection.Address!;
        KoboldCppClient kobold = clients.For(address);
        var genKey = "chatai-" + ChatLibrary.NewMessageId();
        ReplyReader reader = new(job.Kind == ReplyKind.Continuation ? new ThoughtFormat() : profile.Format.Thought, job.Kind == ReplyKind.Continuation ? null : CharacterName);
        var before = job.Continued ?? string.Empty;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Stopwatch clock = new();
        double? promptSeconds = null;
        var streaming = false;
        var stopped = false;
        var backendFailed = false;
        SeedChoice seed = Seeds.Pick(profile.Samplers.Seed, job.Chat.Seed, regenerate: job.Kind != ReplyKind.Message);
        BuiltPrompt? prompt = null;
        string? model = null;
        var contextLength = 0;
        StringBuilder sent = new();
        IDisposable? serverSlot = null;

        try
        {
            await Context.Runtime.InvokeAsync(() => _generation = generation, lifetime).ConfigureAwait(false);

            if (job.Kind == ReplyKind.Message)
            {
                List<StoredDocument<Scenario>> beforeReply = await CandidatesForAsync(ScenarioTriggerKind.BeforeReply, generation.Token).ConfigureAwait(false);

                if (beforeReply.Count > 0)
                {
                    ScenarioBatchResult beforeResult = await RunScenarioBatchOnOpenChatAsync(job.ChatId, job.Chat, beforeReply, bypassGates: false, commitImmediately: true, job.PreTurnChat, generation.Token).ConfigureAwait(false);
                    generation.Token.ThrowIfCancellationRequested();

                    if (!beforeResult.AllSucceeded)
                    {
                        backendFailed = true;
                        return;
                    }

                    job = job with { Chat = beforeResult.Chat, History = beforeResult.Chat.Messages };
                }
            }

            serverSlot = await clients.AcquireSlotAsync(address, generation.Token).ConfigureAwait(false);
            ResolvedContext context = await ContextLengths.ResolveAsync(profile.Connection, kobold, generation.Token).ConfigureAwait(false);

            if (context.ServerAway)
                throw new HttpRequestException("The model server did not answer for its context.");

            contextLength = context.Length;
            model = await KoboldCppClients.TryGetModelNameAsync(kobold, generation.Token).ConfigureAwait(false);
            prompt = await BuildPromptAsync(job, context.Length, kobold, model, generation.Token).ConfigureAwait(false);
            Context.Logger.LogPrompt(prompt.Tokens, prompt.Counts, context.Length, prompt.MessagesSent, job.History.Count);
            KoboldGenerateRequest request = KoboldGenerateRequest.From(prompt.Text, profile.Samplers, context.Length, seed.Seed, profile.Format.AllStopSequences(), genKey);

            using IDisposable tracked = _activeGenerations.Track(kobold, genKey);
            generation.Token.ThrowIfCancellationRequested();

            started = DateTimeOffset.UtcNow;
            clock.Start();
            streaming = true;

            await foreach (var piece in kobold.StreamAsync(request, generation.Token).ConfigureAwait(false))
            {
                if (piece.Length > 0)
                    promptSeconds ??= clock.Elapsed.TotalSeconds;

                _ = sent.Append(piece);

                if (!reader.Append(piece))
                    continue;

                await Context.Runtime.InvokeAsync(() =>
                {
                    job.Item.Text = before + reader.Text;

                    if (job.Item.Text.Length > 0)
                        job.Item.ShowLoading(false);

                    if (job.Kind != ReplyKind.Continuation)
                        job.Item.ShowThought(reader.Thought, _hideThoughts, thinking: reader.Text.Length == 0);
                }, lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (generation.IsCancellationRequested)
        {
            stopped = true;
        }
        catch (HttpRequestException exception)
        {
            backendFailed = true;
            Context.Logger.LogBackendFailure(exception);
            await Context.SendEffectsToAllAsync([new ShowNotificationEffect("chatai.backend.unreachable", UIColorStyle.Danger)], CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = reader.Complete();
            clock.Stop();

            if (lifetime.IsCancellationRequested)
                await _activeGenerations.AbortAllAsync(exception => Context.Logger.LogBackendFailure(exception), CancellationToken.None).ConfigureAwait(false);

            (int Tokens, TokenCounts Counts) replyCount = (0, TokenCounts.Exact);

            try
            {
                if (!lifetime.IsCancellationRequested && streaming)
                    replyCount = await KoboldCppClients.CountReplyAsync(kobold, sent.ToString(), prompt, exception => Context.Logger.LogBackendFailure(exception)).ConfigureAwait(false);
            }
            finally
            {
                serverSlot?.Dispose();
            }

            if (!lifetime.IsCancellationRequested)
                await EndAsync(job, reader, seed, streaming ? prompt : null, model, contextLength, started, promptSeconds, clock.Elapsed, replyCount, generation, canTrigger: streaming && !stopped && !backendFailed).ConfigureAwait(false);
        }
    }

    private Task<BuiltPrompt> BuildPromptAsync(ReplyJob job, int contextLength, KoboldCppClient kobold, string? model, CancellationToken cancellationToken)
        => BuildPromptAsync(job.Profile, job.Chat, job.History, job.Continued, contextLength, kobold, model, cancellationToken);

    private async Task<BuiltPrompt> BuildPromptAsync(ActiveProfile profile, Chat chat, IReadOnlyList<ChatMessage> history, string? continued, int contextLength, KoboldCppClient? kobold, string? model, CancellationToken cancellationToken)
    {
        Character? character = null;
        Persona? persona = null;
        var characterName = AppName;
        var userName = DefaultUserName;

        await Context.Runtime.InvokeAsync(() =>
        {
            character = _character;
            persona = _persona;
            characterName = CharacterName;
            userName = UserName;
        }, cancellationToken).ConfigureAwait(false);

        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        World? world = await WorldInfo.OfAsync(Data, character, persona, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PromptMessage> projectedHistory = ChatRecords.ProjectHistory(history, chat.Records);
        PromptSpec spec = new()
        {
            SystemPrompt = profile.SystemPrompt.Content,
            Prompt = profile.Prompt,
            Character = character,
            CharacterName = characterName,
            UserName = userName,
            Persona = persona,
            Lore = WorldInfo.ArticlesOf(world, projectedHistory, continued),
            AuthorsNotes = [settings.AuthorsNote, character?.AuthorsNote, chat.AuthorsNote],
            NotePlacement = settings.AuthorsNotePlacement,
            History = projectedHistory,
            Continuation = continued
        };

        KoboldCppClients clients = Context.Services.GetRequiredService<KoboldCppClients>();

        return await clients.BuildPromptWithFallbackAsync(spec, profile.Format, contextLength, profile.Samplers.ReplyLength, profile.Connection.Address, kobold, model, cancellationToken, exception => Context.Logger.LogBackendFailure(exception)).ConfigureAwait(false);
    }

    private async Task EndAsync(ReplyJob job, ReplyReader reader, SeedChoice seed, BuiltPrompt? prompt, string? model, int contextLength, DateTimeOffset started, double? promptSeconds, TimeSpan elapsed, (int Tokens, TokenCounts Counts) reply, CancellationTokenSource generation, bool canTrigger)
    {
        bool? thinking = job.Profile.Format.Thought.IsDefined ? job.Kind != ReplyKind.Continuation && job.Profile.Format.Thought.Enabled : null;
        GenerationRecord? record = prompt is null ? null : new GenerationRecord
        {
            Id = ChatLibrary.NewMessageId(),
            Purpose = GenerationPurpose.Reply,
            Connection = job.Profile.ConnectionId,
            Model = model,
            ContextLength = contextLength,
            Samplers = GenerationSamplers.From(job.Profile.Samplers, seed.Seed, thinking),
            PromptTokens = prompt.Blocks,
            ReplyTokens = reply.Tokens,
            Counts = reply.Counts == TokenCounts.Exact ? prompt.Counts : TokenCounts.Approximate,
            Started = started,
            PromptSeconds = promptSeconds,
            Seconds = elapsed.TotalSeconds
        };

        List<StoredDocument<Scenario>> candidates = canTrigger ? await CandidatesForAsync(ScenarioTriggerKind.AfterReply, CancellationToken.None).ConfigureAwait(false) : [];
        Chat? kept = null;
        var runPostReply = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            var cut = Messages.EndsAtLive;
            Messages.Live = null;
            _liveKind = null;

            if (_chat is not null)
            {
                kept = Ended(job, reader, seed, record);
                _chat = kept ?? _chat;
                ChatSeed = _chat.Seed;
            }

            runPostReply = canTrigger && kept is not null && candidates.Count > 0 && !generation.IsCancellationRequested;

            if (!runPostReply)
            {
                _generating = false;
                _generation = null;
                _hiddenBackupRecordIds.Clear();
            }

            if (_chat is not null)
            {
                ShowEnded(job, addedMessage: kept is not null && job.Kind == ReplyKind.Message);
                RefreshVisuals();
                RefreshRealizedMessages();
                RefreshRealizedRecords();
                RefreshLoadedStatistics();
            }

            if (cut)
                Messages.Reread();

            if (!runPostReply)
            {
                ShowSendButton(stops: false);
                ShowLastActions();
            }
        }, CancellationToken.None).ConfigureAwait(false);

        if (kept is not null && Data.Chats.Exists(job.ChatId))
            await Chats.WriteAsync(job.ChatId, kept, CancellationToken.None).ConfigureAwait(false);
        else if (job.Kind == ReplyKind.Continuation && prompt is not null)
            await Context.SendEffectsToAllAsync([new ShowNotificationEffect("chatai.chat.continue.nothing", UIColorStyle.Info)], CancellationToken.None).ConfigureAwait(false);

        if (runPostReply)
        {
            try
            {
                await RunTriggeredScenariosAsync(job, kept!, candidates, generation.Token).ConfigureAwait(false);
            }
            finally
            {
                await ReleaseGenerationAsync().ConfigureAwait(false);
            }
        }
    }

    private Chat? Ended(ReplyJob job, ReplyReader reader, SeedChoice seed, GenerationRecord? record)
    {
        var said = reader.Text.Length > 0 || reader.Thought.Length > 0;
        MessageVariant variant = new() { Text = reader.Text, Thought = reader.Thought.Length == 0 ? null : reader.Thought, Seed = seed.Seed, Generation = record?.Id };

        return job.Kind switch
        {
            ReplyKind.Message when said => ChatEditing.Append(_chat!, new ChatMessage { Id = job.Item.Id, Role = MessageRole.Character, Variants = [variant], At = DateTimeOffset.UtcNow }, record, seed.ChatSeed),
            ReplyKind.Variant when said => ChatEditing.AddVariant(_chat!, job.Item.Id, variant, record),
            ReplyKind.Remake when said => ChatEditing.Remake(_chat!, job.Item.Id, variant, record),
            ReplyKind.Continuation when reader.Text.Length > 0 => ChatEditing.Extend(_chat!, job.Item.Id, reader.Text, record),
            _ => null
        };
    }

    private void ShowEnded(ReplyJob job, bool addedMessage)
    {
        if (_chat!.Messages.FirstOrDefault(message => message.Id == job.Item.Id) is ChatMessage message)
            Show(job.Item, message);
        else
            _ = Messages.Drop(job.Item.Id);

        ShowLastWords(job.ChatId, _chat.Messages.Count == 0 ? string.Empty : _chat.Messages[^1].Current.Text, bumpToTop: addedMessage);
    }

    [UICommand]
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Context.Runtime.InvokeAsync(() => _generation?.Cancel(), cancellationToken).ConfigureAwait(false);
        await _activeGenerations.AbortAllAsync(exception => Context.Logger.LogBackendFailure(exception), cancellationToken).ConfigureAwait(false);
    }

    [UICommand]
    public async Task SaveChatSeedAsync(CancellationToken cancellationToken)
    {
        string? chatId = null;
        Chat? changed = null;

        await Context.Runtime.InvokeAsync(() =>
        {
            if (_chat is null)
                return;

            if (_generating)
            {
                ChatSeed = _chat.Seed;
                return;
            }

            chatId = OpenChatId;
            _chat = changed = _chat with { Seed = ChatSeed is decimal seed ? (long)seed : null };
        }, cancellationToken).ConfigureAwait(false);

        if (changed is not null && chatId is not null)
            await Chats.WriteAsync(chatId, changed, cancellationToken).ConfigureAwait(false);
    }

    private enum ReplyKind
    {
        Message,
        Variant,
        Remake,
        Continuation
    }

    private sealed record ReplyJob(ActiveProfile Profile, string ChatId, Chat Chat, Chat PreTurnChat, ReplyKind Kind, ChatMessageItem Item, IReadOnlyList<ChatMessage> History, string? Continued);

    private sealed record BeforeSendJob(ActiveProfile Profile, string ChatId, IReadOnlyList<StoredDocument<Scenario>> Scenarios);
}

internal static partial class ChatLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The model server did not answer.")]
    public static partial void LogBackendFailure(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Prompt of {Tokens} tokens ({Counts}) in a context of {Context}, {Sent} of {Messages} messages.")]
    public static partial void LogPrompt(this ILogger logger, int tokens, TokenCounts counts, int context, int sent, int messages);
}
