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

/// <summary>A message in the feed: who wrote it, their picture or mark, the model's thought folded over it, and its words in Markdown.</summary>
internal sealed partial class ChatMessageItem : RecursiveObservable, IBindableItem, IContentItem
{
    private bool _thinking;

    [RecursiveMember(false)]
    public required string Id { get; init; }

    /// <summary>A name and words are the chat's own, never a key.</summary>
    public bool IsContent => true;

    public MessageRole Role { get; init; }

    [RecursiveMember]
    public partial string Author { get; set; } = string.Empty;

    /// <summary>The author's picture; with none, the row shows the person's glyph.</summary>
    [RecursiveMember]
    public partial string? Avatar { get; set; }

    [RecursiveMember]
    public partial string Text { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility LoadingVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial UIVisibility TextVisibility { get; set; } = UIVisibility.Visible;

    /// <summary>The words being edited in the message's place, while it is edited.</summary>
    [RecursiveMember]
    public partial string EditDraft { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility EditorVisibility { get; set; } = UIVisibility.Collapsed;

    /// <summary>Whether Edit and the menu stand beside the author: while the message is not edited, its editor's buttons standing there then.</summary>
    [RecursiveMember]
    public partial UIVisibility ToolsVisibility { get; set; } = UIVisibility.Visible;

    /// <summary>The message's menu: made again and a new variant on a character's reply, a new variant by hand on a greeting, and the delete from here.</summary>
    [RecursiveMember(false)]
    public RecursiveCollection<MenuItem> Actions { get; } = [];

    /// <summary>Three dots in the words' place while the model reads the prompt before any words arrive.</summary>
    public void ShowLoading(bool loading)
    {
        LoadingVisibility = loading ? UIVisibility.Visible : UIVisibility.Collapsed;
        TextVisibility = loading || EditorVisibility == UIVisibility.Visible ? UIVisibility.Collapsed : UIVisibility.Visible;
    }

    /// <summary>The editor in the words' place, opened on <paramref name="draft"/>; or the words again.</summary>
    public void ShowEditor(string? draft)
    {
        EditDraft = draft ?? string.Empty;
        LoadingVisibility = UIVisibility.Collapsed;
        TextVisibility = draft is null ? UIVisibility.Visible : UIVisibility.Collapsed;
        EditorVisibility = draft is null ? UIVisibility.Collapsed : UIVisibility.Visible;
        ToolsVisibility = TextVisibility;
    }

    /// <summary>What the model thought before the words; none for a message without one.</summary>
    [RecursiveMember]
    public partial string? Thought { get; set; }

    /// <summary>Whether the thought's fold stands over the words: there is a thought, and the prompt does not hide them.</summary>
    [RecursiveMember]
    public partial UIVisibility ThoughtVisibility { get; set; } = UIVisibility.Collapsed;

    /// <summary>The fold's title: thinking while the thought is still being written, then the thought.</summary>
    [RecursiveMember]
    public partial UIPhrase ThoughtTitle { get; set; } = new("chatai.chat.thought");

    /// <summary>The shown variant's token count under the avatar.</summary>
    [RecursiveMember]
    public partial string TokenCount { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase? TokenCountTooltip { get; set; }

    [RecursiveMember]
    public partial UIVisibility TokenCountVisibility { get; set; } = UIVisibility.Collapsed;

    /// <summary>Which variant is shown of how many, as the swipe between them says it: <c>2/3</c>.</summary>
    [RecursiveMember]
    public partial string VariantLabel { get; set; } = string.Empty;

    /// <summary>Whether the swipe stands beside the words: there is more than one variant to go between.</summary>
    [RecursiveMember]
    public partial UIVisibility SwipeVisibility { get; set; } = UIVisibility.Collapsed;

    /// <summary>Whether there is a variant before the one shown, to go back to.</summary>
    [RecursiveMember]
    public partial bool HasPrevious { get; set; }

    /// <summary>Whether there is a variant after the one shown; a new one is the menu's, never the swipe's.</summary>
    [RecursiveMember]
    public partial bool HasNext { get; set; }

    /// <summary>Scenario records anchored under this message, each shown as a quiet fold.</summary>
    [RecursiveMember(false)]
    public RecursiveCollection<ChatRecordItem> Records { get; } = [];

    /// <summary>The thought as it stands, shown folded unless <paramref name="hidden"/>; <paramref name="thinking"/> while it is written.</summary>
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

/// <summary>
/// The chat open on the page — the one the address names, else the one last open — with its character: its messages kept in the
/// chat's file as they are written, and each reply generated with the active connection and streamed into the feed — a new message,
/// another variant of the last one, or the last one continued.
/// </summary>
internal sealed partial class ChatController : ShellController
{
    public const string FeedId = "chatai-feed";
    public const string ComposerId = "chatai-composer";
    public const string ComposerFormId = "chatai-composer-form";

    /// <summary>The reader's name in a chat with no persona, as SillyTavern names them.</summary>
    private const string DefaultUserName = "User";

    /// <summary>How long a finished reply waits for the server to count it before it is estimated instead.</summary>
    private static readonly TimeSpan ReplyCountPatience = TimeSpan.FromSeconds(10);

    /// <summary>How many messages the feed reads at a time, from the end back.</summary>
    public const int WindowSize = 30;

    private readonly Lock _abortLock = new();
    private bool _generating;
    private ReplyKind? _liveKind;
    private CancellationTokenSource? _generation;
    private string? _genKey;
    private KoboldCppClient? _kobold;

    private Chat? _chat;
    private Character? _character;

    /// <summary>The persona the chat is written as: its own, else the default one; none, and the reader is "User".</summary>
    private Persona? _persona;
    private string? _personaId;
    private bool _hideThoughts;

    [RecursiveMember(false)]
    public MessageSource Messages { get; } = new();

    /// <summary>
    /// The composer's extras: the last reply made again, a new variant of it or more of its words; the chat's own author's note, its
    /// persona, the quick phrases and impersonation.
    /// </summary>
    [RecursiveMember(false)]
    public RecursiveCollection<MenuItem> Extras { get; } =
    [
        Entry(RefreshAction, AppIcons.Regenerate, "chatai.chat.regenerate"),
        Entry(ContinueAction, AppIcons.Continue, "chatai.chat.continue"),
        Entry(VariantAction, AppIcons.Variant, "chatai.chat.variant"),
        Entry(ImpersonateAction, AppIcons.Impersonate, "chatai.chat.extras.impersonate"),
        new MenuItem { Id = "chat-separator", Kind = UIMenuItemKind.Separator },
        Entry(ScenariosAction, AppIcons.Scenarios, "chatai.chat.extras.scenarios"),
        new MenuItem { Id = PersonaAction, Title = "chatai.chat.extras.persona", Icon = AppIcons.Outline(AppIcons.Persona), Kind = UIMenuItemKind.Select },
        Entry(NoteAction, AppIcons.AuthorNotes, "chatai.chat.extras.author-notes"),
        new MenuItem { Id = "chat-context-separator", Kind = UIMenuItemKind.Separator },
        Entry(StatisticsAction, AppIcons.Statistics, "chatai.chat.extras.statistics"),
        Entry(PromptAction, AppIcons.Prompt, "chatai.chat.extras.prompt"),
        new MenuItem { Id = "chat-inspect-separator", Kind = UIMenuItemKind.Separator },
        Entry(PhrasesAction, AppIcons.Phrases, "chatai.chat.extras.phrases")
    ];

    /// <summary>A menu's entry; its id says what it does.</summary>
    private static MenuItem Entry(string id, string icon, string title)
        => new() { Id = id, Title = title, Icon = AppIcons.Outline(icon) };

    [RecursiveMember]
    public partial string Draft { get; set; } = string.Empty;

    /// <summary>The composer's one button in the field: Send, or Stop while a reply is written.</summary>
    [RecursiveMember]
    public partial string SendIcon { get; set; } = UIStandardAction.Send.Icon;

    [RecursiveMember]
    public partial UIPhrase SendTooltip { get; set; } = new(UIStandardAction.Send.Title);

    /// <summary>What the feed says while it has no message: to pick a chat, or to write the first message.</summary>
    [RecursiveMember]
    public partial UIPhrase? EmptyFeed { get; set; } = new UIPhrase("chatai.chat.none");

    /// <summary>The picture behind the chat: its character's, else the application's; none, and it stands on the theme.</summary>
    [RecursiveMember]
    public partial string? Background { get; set; }

    /// <summary>The fill behind the central chat column when the message background setting is on.</summary>
    [RecursiveMember]
    public partial UIThemeColor? MessageBackground { get; set; }

    /// <summary>The open chat's own seed, which One per chat generates with; none before its first reply, and none picks a new one.</summary>
    [RecursiveMember]
    public partial decimal? ChatSeed { get; set; }

    protected override bool IsGenerating => _generating;

    private string CharacterName => _character?.Name ?? AppName;

    private string UserName => string.IsNullOrWhiteSpace(_persona?.Name) ? DefaultUserName : _persona.Name;

    /// <summary>The prompt preset says whether thoughts are shown; one switched in the panel shows or hides them at once.</summary>
    protected override void OnProfileLoaded(ActiveProfile? profile)
    {
        _hideThoughts = profile?.Prompt.Thoughts.Show == ThoughtDisplay.Hidden;

        foreach (ChatMessageItem item in Messages.Items)
            item.RefreshThought(_hideThoughts);
    }

    /// <summary>The chat the address names, else the one last open; a reply being written keeps its chat until it ends.</summary>
    protected override async Task OnNavigatedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        if (_generating)
            return;

        var chatId = navigation.TryGetParameter(AppRoutes.ChatParameter, out var named) ? named : OpenChatId ?? (await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false)).LastChat;

        if (chatId is not null && chatId == OpenChatId && _chat is not null)
            return;

        if (chatId is not null && await OpenAsync(chatId, cancellationToken).ConfigureAwait(false))
            return;

        if (_chat is not null)
            await ClearOpenChatAsync(cancellationToken).ConfigureAwait(false);
        else
            Background = Pictures.BackgroundOf(null);
    }

    /// <summary>
    /// The chat opened where the page stands and its address written as a new entry, which Back leaves through <see cref="OnNavigatedAsync"/>:
    /// a page load would put the chats panel away, which a character picked keeps open.
    /// </summary>
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

    /// <summary>Refreshes the open chat's character, persona, active world, background and message authors/avatars when a card or setting changes.</summary>
    private async Task RefreshOpenChatPresentationAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ApplyChatSurface(settings.MessageBackground);
        BackgroundDim = Math.Clamp(settings.BackgroundDim, 0, 1);

        if (_chat is not null && OpenChatId is string openChatId && !Data.Chats.Exists(openChatId) && !_generating)
        {
            await ClearOpenChatAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Background = Pictures.BackgroundOf(_chat?.Character, OpenChatId);

        if (_chat is null)
        {
            OpenCharacterId = null;
            ActivePersonaId = null;
            ActiveWorldId = null;
            ShowCards();
            return;
        }

        _character = _chat.Character is string characterId ? await Data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) : null;
        _personaId = _chat.Persona is string own && Slug.IsValidId(own) && Data.Personas.Exists(own) ? own : DefaultPersonaId;
        _persona = _personaId is null ? null : await Data.Personas.ReadAsync(_personaId, cancellationToken).ConfigureAwait(false);
        ActivePersonaId = _personaId;
        ActiveWorldId = ResolveActiveWorldId();
        ShowCards();

        if (Messages.Items.Count == 0 && (_chat.Messages.Count > 0 || Messages.Live is not null))
            await Messages.LoadWindowAsync(new UIItemWindowRequest(UIItemAnchor.End, WindowSize), cancellationToken).ConfigureAwait(false);

        var characterAvatar = Pictures.CharacterAvatarOf(_chat.Character);
        var personaAvatar = Pictures.PersonaAvatarOf(_personaId);

        foreach (ChatMessageItem item in Messages.Items)
        {
            item.Author = item.Role == MessageRole.User ? UserName : CharacterName;
            item.Avatar = item.Role == MessageRole.Character ? characterAvatar : personaAvatar;
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
        _streamingRecord = null;
        _draftGenerationId = null;
        _draftContinuations.Clear();
        ChatSeed = null;
        OpenChatId = null;
        Background = Pictures.BackgroundOf(null);
        ChatBackground.Show(null);
        EmptyFeed = new UIPhrase("chatai.chat.none");
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
        _streamingRecord = null;
        _draftGenerationId = null;
        _draftContinuations.Clear();
        ChatSeed = chat.Seed;
        _character = chat.Character is string characterId ? await Data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) : null;
        _personaId = chat.Persona is string own && Slug.IsValidId(own) && Data.Personas.Exists(own) ? own : DefaultPersonaId;
        Background = Pictures.BackgroundOf(chat.Character, chatId);
        ChatBackground.Show(Pictures.ChatBackgroundOf(chatId));
        _persona = _personaId is null ? null : await Data.Personas.ReadAsync(_personaId, cancellationToken).ConfigureAwait(false);
        ActivePersonaId = _personaId;
        ActiveWorldId = ResolveActiveWorldId();
        OpenChatId = chatId;
        await FollowChatAsync(chatId, chat.Character, cancellationToken).ConfigureAwait(false);
        EmptyFeed = new UIPhrase("chatai.chat.empty");

        Messages.Attach(() => _chat?.Messages ?? [], ItemOf);
        await Messages.LoadWindowAsync(new UIItemWindowRequest(UIItemAnchor.End, WindowSize), cancellationToken).ConfigureAwait(false);
        ShowLastActions();
        RefreshLoadedStatistics();
        await ShowChatListsAsync(cancellationToken).ConfigureAwait(false);

        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ApplyChatSurface(settings.MessageBackground);

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

    /// <summary>
    /// A message's row with its menu: a reply made again or given a new variant, a greeting given one by hand — the model never
    /// writes a greeting — and any message branched into a new chat or deleted from there on.
    /// </summary>
    private ChatMessageItem RowOf(string id, MessageRole role, bool greeting)
    {
        ChatMessageItem item = new() { Id = id, Role = role, Author = role == MessageRole.User ? UserName : CharacterName, Avatar = role == MessageRole.Character ? Pictures.CharacterAvatarOf(_chat?.Character) : Pictures.PersonaAvatarOf(_personaId) };

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

    /// <summary>Whether the message is the character's greeting: the chat's first message, a character's.</summary>
    private bool IsGreeting(string messageId)
        => _chat is { Messages.Count: > 0 } chat && chat.Messages[0].Id == messageId && chat.Messages[0].Role == MessageRole.Character;

    /// <summary>A message's shown variant in its row: its words, its thought, its token count, and which of its variants it is.</summary>
    private void Show(ChatMessageItem item, ChatMessage message)
    {
        var chosen = Math.Clamp(message.Chosen, 0, Math.Max(0, message.Variants.Count - 1));
        MessageGenerationSummary? generation = _chat is null ? null : ChatStatistics.ForMessage(_chat, message.Id);
        var tokens = generation is { ReplyTokens: > 0 } ? generation.ReplyTokens : ApproximateTokenCounter.Estimate(message.Current.Text);
        TokenCounts counts = generation is { ReplyTokens: > 0 } ? generation.Counts : TokenCounts.Approximate;

        item.Text = message.Current.Text;
        item.ShowLoading(false);
        item.ToolsVisibility = !_generating && _editingId != message.Id ? UIVisibility.Visible : UIVisibility.Collapsed;
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

    /// <summary>
    /// The composer's extras as the last message allows them, none while a reply is written: a character's reply made again, given a
    /// new variant or continued; a greeting given a variant by hand; nothing on the reader's own words.
    /// </summary>
    private void ShowLastActions()
    {
        ChatMessage? last = _generating || _chat is null || _chat.Messages.Count == 0 ? null : _chat.Messages[^1];
        var reply = last?.Role == MessageRole.Character;
        var greeting = reply && IsGreeting(last!.Id);

        foreach (MenuItem entry in Extras)
        {
            entry.Enabled = entry.Id switch
            {
                RefreshAction or ContinueAction => reply && !greeting,
                VariantAction => reply,
                ImpersonateAction => last is not null,
                PersonaAction => !_generating && _chat is not null && entry.Items.Count > 1,
                NoteAction => !_generating && _chat is not null,
                StatisticsAction or PromptAction => _chat is not null,
                ScenariosAction => !_generating,
                _ => entry.Enabled
            };
        }

        RefreshScenarioMenuEnabled();
    }

    /// <summary>The composer's button: stops the reply being written, or sends the draft. Enter only sends, so a draft typed meanwhile never stops it.</summary>
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

    /// <summary>
    /// Sends the draft — or, with none, asks for the reply to the reader's last message — and starts the reply in a message of its own. The reply is a job the runtime owns, not the command's work: a
    /// command is cancelled when its connection goes, and a tablet's browser drops the connection whenever its screen sleeps.
    /// </summary>
    [UICommand]
    public async Task<UICommandResult> SendAsync(CancellationToken cancellationToken)
    {
        (ActiveProfile? profile, UICommandResult? refusal) = await ReplyProfileAsync(cancellationToken).ConfigureAwait(false);

        if (profile is null)
            return refusal!;

        ReplyJob? job = null;
        var busy = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            busy = _generating;

            if (_generating || _chat is null || OpenChatId is not string chatId)
                return;

            var text = Draft.Trim();

            // Nothing to send under a message of the reader's — its reply deleted, say — asks for the reply, as SillyTavern does.
            if (text.Length == 0 && (_chat.Messages.Count == 0 || _chat.Messages[^1].Role != MessageRole.User))
                return;

            Chat preTurnChat = _chat;
            ChatMessageItem reply = RowOf(ChatLibrary.NewMessageId(), MessageRole.Character, greeting: false);

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

                _chat = ChatEditing.Append(_chat, message, null, _chat.Seed);
                Draft = string.Empty;
                _draftGenerationId = null;
                _draftContinuations.Clear();

                Messages.Add(ItemOf(message));
                ShowLastWords(chatId, text);
            }

            Messages.Add(reply);
            job = BeginReply(profile, chatId, preTurnChat, ReplyKind.Message, reply, _chat.Messages, null);
        }, cancellationToken).ConfigureAwait(false);

        if (busy)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (job is null)
            return UICommandResult.Ok();

        await Chats.WriteAsync(job.ChatId, job.Chat, cancellationToken).ConfigureAwait(false);
        Context.Runtime.Start(lifetime => GenerateAsync(job, lifetime));

        return UICommandResult.Ok();
    }

    /// <summary>The active profile a reply is made with, or what to tell the reader when there is none — or no chat to reply in.</summary>
    private async Task<(ActiveProfile? Profile, UICommandResult? Refusal)> ReplyProfileAsync(CancellationToken cancellationToken)
    {
        if (OpenChatId is null)
            return (null, Notify("chatai.chat.none", UIColorStyle.Info));

        if (await Profiles.LoadActiveAsync(Data, cancellationToken).ConfigureAwait(false) is not ActiveProfile profile || profile.Connection.Address is null)
            return (null, Notify("chatai.connection.none", UIColorStyle.Warning));

        return (profile, null);
    }

    /// <summary>A reply about to be written, under the runtime's lock: the composer turns to Stop, its extras go.</summary>
    private ReplyJob BeginReply(ActiveProfile profile, string chatId, Chat preTurnChat, ReplyKind kind, ChatMessageItem item, IReadOnlyList<ChatMessage> history, string? continued)
    {
        _generating = true;
        _liveKind = kind;
        Messages.Live = item;
        CaptureHiddenBackupRecords(preTurnChat);
        CloseOpenEditors();
        item.ShowLoading(kind != ReplyKind.Continuation);
        RefreshRealizedMessages();
        RefreshRealizedRecords();
        ShowSendButton(stops: true);
        ShowLastActions();

        return new ReplyJob(profile, chatId, _chat!, preTurnChat, kind, item, history, continued);
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

    /// <summary>
    /// Streams the reply into the job's row, every write under the runtime's lock, until it ends, Stop, or the runtime goes
    /// (<paramref name="lifetime"/>); then keeps it in the chat with its seed and the record of how it was made.
    /// </summary>
    private async Task GenerateAsync(ReplyJob job, CancellationToken lifetime)
    {
        // Let go under the runtime's lock before it is disposed, and Stop cancels it under the same lock: a Stop arriving as the reply
        // ends finds it alive or finds nothing.
        using CancellationTokenSource generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        ActiveProfile profile = job.Profile;
        KoboldCppClient kobold = Context.Services.GetRequiredService<KoboldCppClients>().For(profile.Connection.Address!);
        var genKey = "chatai-" + ChatLibrary.NewMessageId();
        // A continuation goes on from words the model wrote after its thought: what comes is words, with no thought to open.
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
        // What the model sent as it sent it, markers and all: what its tokenizer counts for the record.
        StringBuilder sent = new();

        try
        {
            await Context.Runtime.InvokeAsync(() =>
            {
                _generation = generation;
                SetActiveGeneration(kobold, null);
            }, lifetime).ConfigureAwait(false);

            ResolvedContext context = await ContextLengths.ResolveAsync(profile.Connection, kobold, generation.Token).ConfigureAwait(false);

            // Asked once and silent: the reply would only wait for it again, twice over.
            if (context.ServerAway)
                throw new HttpRequestException("The model server did not answer for its context.");

            contextLength = context.Length;
            model = await TryGetModelAsync(kobold, generation.Token).ConfigureAwait(false);
            prompt = await BuildPromptAsync(job, context.Length, kobold, model, generation.Token).ConfigureAwait(false);
            Context.Logger.LogPrompt(prompt.Tokens, prompt.Counts, context.Length, prompt.MessagesSent, job.History.Count);
            KoboldGenerateRequest request = KoboldGenerateRequest.From(prompt.Text, profile.Samplers, context.Length, seed.Seed, profile.Format.AllStopSequences(), genKey);

            SetActiveGeneration(kobold, genKey);
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
            // Stop was pressed, or the runtime went: what came so far stays as the reply.
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

            if (!lifetime.IsCancellationRequested)
            {
                (int Tokens, TokenCounts Counts) replyCount = streaming ? await CountReplyAsync(kobold, sent.ToString(), prompt).ConfigureAwait(false) : (0, TokenCounts.Exact);
                await EndAsync(job, reader, seed, streaming ? prompt : null, model, contextLength, started, promptSeconds, clock.Elapsed, replyCount, generation, kobold, canTrigger: streaming && !stopped && !backendFailed).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string?> TryGetModelAsync(KoboldCppClient kobold, CancellationToken cancellationToken)
    {
        try
        {
            var model = (await kobold.GetModelAsync(cancellationToken).ConfigureAwait(false)).Trim();

            return model.Length == 0 ? null : model;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// The prompt for the job's history, counted by the model's own tokenizer; a server that will not count is no reason not to answer,
    /// so the prompt is then counted by an estimate and says so.
    /// </summary>
    private Task<BuiltPrompt> BuildPromptAsync(ReplyJob job, int contextLength, KoboldCppClient kobold, string? model, CancellationToken cancellationToken)
        => BuildPromptAsync(job.Profile, job.Chat, job.History, job.Continued, contextLength, kobold, model, cancellationToken);

    private async Task<BuiltPrompt> BuildPromptAsync(ActiveProfile profile, Chat chat, IReadOnlyList<ChatMessage> history, string? continued, int contextLength, KoboldCppClient? kobold, string? model, CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        World? world = await WorldInfo.OfAsync(Data, _character, _persona, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PromptMessage> projectedHistory = ChatRecords.ProjectHistory(history, chat.Records);
        PromptSpec spec = new()
        {
            SystemPrompt = profile.SystemPrompt.Content,
            Prompt = profile.Prompt,
            Character = _character,
            CharacterName = CharacterName,
            UserName = UserName,
            Persona = _persona,
            Lore = WorldInfo.ArticlesOf(world, projectedHistory, continued),
            AuthorsNotes = [settings.AuthorsNote, _character?.AuthorsNote, chat.AuthorsNote],
            NotePlacement = settings.AuthorsNotePlacement,
            History = projectedHistory,
            Continuation = continued
        };

        if (kobold is not null && profile.Connection.Address is Uri address)
        {
            try
            {
                ITokenCounter counter = Context.Services.GetRequiredService<KoboldCppClients>().CounterFor(address, kobold, model);

                return await PromptBuilder.BuildAsync(spec, profile.Format, contextLength, profile.Samplers.ReplyLength, counter, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                Context.Logger.LogBackendFailure(exception);
            }
        }

        return await PromptBuilder.BuildAsync(spec, profile.Format, contextLength, profile.Samplers.ReplyLength, ApproximateTokenCounter.Instance, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The reply's tokens, by the model's own tokenizer when the prompt was counted by it; an estimate, and said to be one, when the
    /// prompt was estimated or the server will not count now.
    /// </summary>
    private async Task<(int Tokens, TokenCounts Counts)> CountReplyAsync(KoboldCppClient kobold, string reply, BuiltPrompt? prompt)
    {
        if (prompt?.Counts == TokenCounts.Exact)
        {
            // The reply is kept whatever the count does: a server gone quiet is not waited on.
            using CancellationTokenSource patience = new(ReplyCountPatience);

            try
            {
                return (reply.Length == 0 ? 0 : await kobold.CountTokensAsync(reply, patience.Token).ConfigureAwait(false), TokenCounts.Exact);
            }
            catch (HttpRequestException exception)
            {
                Context.Logger.LogBackendFailure(exception);
            }
            catch (OperationCanceledException) when (patience.IsCancellationRequested)
            {
            }
        }

        return (ApproximateTokenCounter.Estimate(reply), TokenCounts.Approximate);
    }

    /// <summary>
    /// The reply as it ended, kept in the chat with its seed and its generation record — a thought cut short too, to be read. One that
    /// never began leaves nothing: a new message goes from the feed, where it would go into the next prompt as an empty turn, and a
    /// variant or a continuation leaves the message as it was.
    /// </summary>
    private async Task EndAsync(ReplyJob job, ReplyReader reader, SeedChoice seed, BuiltPrompt? prompt, string? model, int contextLength, DateTimeOffset started, double? promptSeconds, TimeSpan elapsed, (int Tokens, TokenCounts Counts) reply, CancellationTokenSource generation, KoboldCppClient kobold, bool canTrigger)
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
            // One label for the record: exact only when the prompt and the reply both are.
            Counts = reply.Counts == TokenCounts.Exact ? prompt.Counts : TokenCounts.Approximate,
            Started = started,
            PromptSeconds = promptSeconds,
            Seconds = elapsed.TotalSeconds
        };

        List<StoredDocument<Scenario>> candidates = canTrigger ? await TriggeredCandidatesAsync().ConfigureAwait(false) : [];
        Chat? kept = null;
        var runPostReply = false;

        await Context.Runtime.InvokeAsync(() =>
        {
            var cut = Messages.EndsAtLive;
            Messages.Live = null;
            _liveKind = null;
            SetActiveGeneration(null, null);

            // A chat is not left while its reply is written (OnNavigatedAsync waits for it), so the chat open is the job's.
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
                ShowEnded(job);
                RefreshRealizedMessages();
                RefreshRealizedRecords();
                RefreshLoadedStatistics();
            }

            // Made in the middle of the chat: what followed it is gone, or back when nothing was kept.
            if (cut)
                Messages.Reread();

            if (!runPostReply)
            {
                ShowSendButton(stops: false);
                ShowLastActions();
            }
        }, CancellationToken.None).ConfigureAwait(false);

        // A finished reply asked to go on often ends at once: the model had nothing to add, which the page would not show otherwise.
        if (kept is not null && Data.Chats.Exists(job.ChatId))
            await Chats.WriteAsync(job.ChatId, kept, CancellationToken.None).ConfigureAwait(false);
        else if (job.Kind == ReplyKind.Continuation && prompt is not null)
            await Context.SendEffectsToAllAsync([new ShowNotificationEffect("chatai.chat.continue.nothing", UIColorStyle.Info)], CancellationToken.None).ConfigureAwait(false);

        if (runPostReply)
        {
            try
            {
                await RunTriggeredScenariosAsync(job, kept!, candidates, kobold, contextLength, model, generation.Token).ConfigureAwait(false);
            }
            finally
            {
                await ReleaseGenerationAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>The chat with the reply in it, by what the job was; none when nothing came to keep.</summary>
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

    /// <summary>The job's row as the chat now holds its message, or gone when the chat has none; the chat's last words in the list.</summary>
    private void ShowEnded(ReplyJob job)
    {
        if (_chat!.Messages.FirstOrDefault(message => message.Id == job.Item.Id) is ChatMessage message)
            Show(job.Item, message);
        else
            _ = Messages.Drop(job.Item.Id);

        ShowLastWords(job.ChatId, _chat.Messages.Count == 0 ? string.Empty : _chat.Messages[^1].Current.Text);
    }

    private void SetActiveGeneration(KoboldCppClient? kobold, string? genKey)
    {
        lock (_abortLock)
        {
            _kobold = kobold;
            _genKey = genKey;
        }
    }

    /// <summary>Stops the reply or scenario being written: the server stops generating, and the stream ends where it stood.</summary>
    [UICommand]
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        string? genKey;
        KoboldCppClient? kobold;

        lock (_abortLock)
        {
            genKey = _genKey;
            kobold = _kobold;
        }

        await Context.Runtime.InvokeAsync(() => _generation?.Cancel(), cancellationToken).ConfigureAwait(false);

        if (genKey is null || kobold is null)
            return;

        try
        {
            await kobold.AbortAsync(genKey, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            Context.Logger.LogBackendFailure(exception);
        }
    }

    /// <summary>Writes the chat's own seed as the panel sets it; while a reply is written the chat is the reply's, so the field goes back.</summary>
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

    /// <summary>What a reply makes: a message of its own, another variant of a reply, the shown one made again, or more of the last one's words.</summary>
    private enum ReplyKind
    {
        Message,
        Variant,
        Remake,
        Continuation
    }

    /// <summary>A reply to write: what it is made with and for, the chat before the turn started, the row it streams into, the history the prompt holds, and the words it goes on from.</summary>
    private sealed record ReplyJob(ActiveProfile Profile, string ChatId, Chat Chat, Chat PreTurnChat, ReplyKind Kind, ChatMessageItem Item, IReadOnlyList<ChatMessage> History, string? Continued);
}

internal static partial class ChatLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The model server did not answer.")]
    public static partial void LogBackendFailure(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Prompt of {Tokens} tokens ({Counts}) in a context of {Context}, {Sent} of {Messages} messages.")]
    public static partial void LogPrompt(this ILogger logger, int tokens, TokenCounts counts, int context, int sent, int messages);
}
