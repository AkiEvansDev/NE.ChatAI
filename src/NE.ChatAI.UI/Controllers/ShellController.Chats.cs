using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Hosting;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>A character, a persona or a world in the chats panel: its picture, its name and its tags as badges.</summary>
internal sealed partial class CardRow : RecursiveObservable, IBindableItem, IContentItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    /// <summary>A name and tags are the reader's own, never keys.</summary>
    public bool IsContent => true;

    [RecursiveMember]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>The card's picture; with none, the row shows the person's glyph.</summary>
    [RecursiveMember]
    public partial string? Avatar { get; set; }

    [RecursiveMember(false)]
    public RecursiveCollection<TagBadge> Tags { get; } = [];

    /// <summary>Shown on the persona new chats are started as.</summary>
    [RecursiveMember]
    public partial UIVisibility DefaultVisibility { get; set; } = UIVisibility.Collapsed;

    /// <summary>Shown on the character, persona or world active in the open chat.</summary>
    [RecursiveMember]
    public partial UIVisibility ActiveVisibility { get; set; } = UIVisibility.Collapsed;
}

/// <summary>
/// The chats panel: the characters, the personas and the worlds — each with its active one first and the rest by name, narrowed by a
/// search over their names and by their tags, and a new one made beside the search; a character picked shows its chats and opens
/// the last one — or starts one, having none — with a new chat and its editor dialog at hand; a persona or world picked opens its
/// editor dialog.
/// </summary>
internal abstract partial class ShellController
{
    /// <summary>How much of a chat's last message its row shows; the rest is the chat's own to read.</summary>
    private const int PreviewLength = 80;

    /// <summary>The panel's three lists, the keys of the switch between them.</summary>
    public const string CharactersList = "characters";
    public const string PersonasList = "personas";
    public const string WorldsList = "worlds";

    public const string DeleteCharacterDialogKey = "chatai-panel-delete-character";
    public const string DeletePersonaDialogKey = "chatai-panel-delete-persona";
    public const string DeleteWorldDialogKey = "chatai-panel-delete-world";
    public const string DeleteChatDialogKey = "chatai-panel-delete-chat";

    /// <summary>What the last read found, so the panel turns between a character and the list without reading the folder again.</summary>
    private IReadOnlyList<ChatSummary> _chats = [];
    private Dictionary<string, UIThemeColor> _characterTagColors = [with(StringComparer.OrdinalIgnoreCase)];
    private Dictionary<string, UIThemeColor> _personaTagColors = [with(StringComparer.OrdinalIgnoreCase)];
    private Dictionary<string, UIThemeColor> _worldTagColors = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The persona new chats are started as; none, and the reader is "User".</summary>
    protected string? DefaultPersonaId { get; private set; }

    /// <summary>The persona active in the open chat.</summary>
    protected string? ActivePersonaId { get; set; }

    /// <summary>The world active in the open chat.</summary>
    protected string? ActiveWorldId { get; set; }

    /// <summary>Every character, as the last read of the chats found them.</summary>
    protected IReadOnlyList<StoredDocument<Character>> KnownCharacters { get; private set; } = [];

    /// <summary>Every persona, as the last read of the chats found them.</summary>
    protected IReadOnlyList<StoredDocument<Persona>> KnownPersonas { get; private set; } = [];

    /// <summary>Every world, as the last read of the chats found them.</summary>
    protected IReadOnlyList<StoredDocument<World>> KnownWorlds { get; private set; } = [];

    /// <summary>The open chat's character, which the panel shows each time it opens.</summary>
    protected string? OpenCharacterId { get; set; }

    /// <summary>The character whose chats the panel shows; none, and it shows the characters.</summary>
    private string? _panelCharacterId;

    private string? _deletingCardId;
    private string? _deletingCardList;
    private string? _deletingChatId;

    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> TagFilterOptions { get; } = [];

    [RecursiveMember]
    public partial IReadOnlyList<string> TagFilter { get; set; } = [];

    /// <summary>Which list the panel shows, the switch over it writing it.</summary>
    [RecursiveMember]
    public partial string PanelList { get; set; } = CharactersList;

    [RecursiveMember]
    public partial UIPhrase? DeleteCardQuestion { get; set; }

    [RecursiveMember(false)]
    public RecursiveCollection<CardRow> CharacterRows { get; } = [];

    /// <summary>What the list says while it shows no one: that there is no one yet, or that no one matches.</summary>
    [RecursiveMember]
    public partial UIPhrase CharactersEmpty { get; set; } = new("chatai.chats.no-characters");

    [RecursiveMember(false)]
    public RecursiveCollection<CardRow> PersonaRows { get; } = [];

    [RecursiveMember]
    public partial UIPhrase PersonasEmpty { get; set; } = new("chatai.chats.no-personas");

    [RecursiveMember(false)]
    public RecursiveCollection<CardRow> WorldRows { get; } = [];

    [RecursiveMember]
    public partial UIPhrase WorldsEmpty { get; set; } = new("chatai.chats.no-worlds");

    /// <summary>Switches the panel between the card lists and the chosen character's chats in one column.</summary>
    [RecursiveMember]
    public partial UIPanesState ChatsPanes { get; set; } = new(stacked: true);

    /// <summary>The character whose chats the panel shows, drawn as its row in the list is.</summary>
    [RecursiveMember]
    public partial CardRow PanelCharacter { get; set; } = new() { Id = string.Empty };

    /// <summary>The panel character's chats, the one last written to first.</summary>
    [RecursiveMember(false)]
    public RecursiveCollection<MenuItem> ChatEntries { get; } = [];

    /// <summary>The chat the page shows, marked in the list as it changes.</summary>
    [RecursiveMember]
    public partial string? OpenChatId { get; set; }

    protected ChatLibrary Chats => Context.Services.GetRequiredService<ChatLibrary>();

    protected PictureContent Pictures => Context.Services.GetRequiredService<PictureContent>();

    protected async Task LoadChatsAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        KnownCharacters = (await Data.Characters.ListAsync(cancellationToken).ConfigureAwait(false)).Items;
        _chats = await Chats.ListAsync(KnownCharacters, cancellationToken).ConfigureAwait(false);
        KnownPersonas = (await Data.Personas.ListAsync(cancellationToken).ConfigureAwait(false)).Items;
        KnownWorlds = (await Data.Worlds.ListAsync(cancellationToken).ConfigureAwait(false)).Items;
        _characterTagColors = TagBadges.ColorsOf(settings, TagScope.Character);
        _personaTagColors = TagBadges.ColorsOf(settings, TagScope.Persona);
        _worldTagColors = TagBadges.ColorsOf(settings, TagScope.World);
        DefaultPersonaId = settings.Persona is string persona && KnownPersonas.Any(stored => stored.Id == persona) ? persona : null;

        ShowTagFilter();
        ShowCards();
        ShowPanelCharacter();

        if (_editingCardId is not null)
            ShowCardWorlds(_editingCardWorld);

        OnCardsLoaded();
    }

    /// <summary>Notifies subclasses whenever characters, personas and worlds are reloaded.</summary>
    protected virtual void OnCardsLoaded()
    {
    }

    /// <summary>The filter menu's tags for the list currently shown; each kind of card keeps its own tags.</summary>
    private void ShowTagFilter()
    {
        (IEnumerable<string> source, IReadOnlyDictionary<string, UIThemeColor> colors) = PanelList switch
        {
            PersonasList => (KnownPersonas.SelectMany(static stored => stored.Document.Tags), _personaTagColors),
            WorldsList => (KnownWorlds.SelectMany(static stored => stored.Document.Tags), _worldTagColors),
            _ => (KnownCharacters.SelectMany(static stored => stored.Document.Tags), _characterTagColors)
        };
        string[] knownTags = [.. source.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];

        if (TagFilter.Count > 0)
        {
            string[] keptFilter = [.. TagFilter.Where(tag => knownTags.Contains(tag, StringComparer.OrdinalIgnoreCase))];

            if (keptFilter.Length != TagFilter.Count)
                TagFilter = keptFilter;
        }

        HashSet<string> keptOptions = [with(StringComparer.Ordinal), .. knownTags];

        for (var i = TagFilterOptions.Count - 1; i >= 0; i--)
        {
            if (!keptOptions.Contains(TagFilterOptions[i].Id))
                _ = TagFilterOptions.Remove(TagFilterOptions[i].Id);
        }

        for (var i = 0; i < knownTags.Length; i++)
        {
            var tag = knownTags[i];

            if (TagFilterOptions.TryGet(tag, out OptionItem? existing))
            {
                existing.BadgeTitle = tag;
                existing.BadgeColor = colors.TryGetValue(tag, out UIThemeColor color) ? color : null;

                if (!string.Equals(TagFilterOptions[i].Id, tag, StringComparison.Ordinal))
                    _ = TagFilterOptions.Move(tag, i);
            }
            else
            {
                TagFilterOptions.Insert(i, TagBadges.OptionOf(tag, colors));
            }
        }
    }

    /// <summary>The three card lists.</summary>
    protected void ShowCards()
    {
        ShowCharacters();
        ShowPersonas();
        ShowWorlds();
    }

    /// <summary>The characters, the active chat's one first, then the rest by name.</summary>
    private void ShowCharacters()
    {
        List<StoredDocument<Character>> shown =
        [
            .. KnownCharacters
                .OrderByDescending(stored => stored.Id == OpenCharacterId)
                .ThenBy(static stored => stored.Document.Name, StringComparer.CurrentCultureIgnoreCase)
        ];

        SyncCardRows(CharacterRows, shown, Pictures.CharacterAvatarOf, _characterTagColors, OpenCharacterId);
        CharactersEmpty = new UIPhrase(KnownCharacters.Count == 0 ? "chatai.chats.no-characters" : "chatai.chats.no-match");
    }

    private static void SyncCardRows<TCard>(RecursiveCollection<CardRow> rows, List<StoredDocument<TCard>> shown, Func<string, string?> avatarOf, IReadOnlyDictionary<string, UIThemeColor> tagColors, string? activeId, string? defaultId = null)
        where TCard : Card
    {
        HashSet<string> kept = [with(StringComparer.Ordinal)];

        for (var i = 0; i < shown.Count; i++)
            _ = kept.Add(shown[i].Id);

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(rows[i].Id))
                _ = rows.Remove(rows[i].Id);
        }

        for (var i = 0; i < shown.Count; i++)
        {
            StoredDocument<TCard> stored = shown[i];
            var avatar = avatarOf(stored.Id);
            UIVisibility defaultVisibility = defaultId is not null && stored.Id == defaultId ? UIVisibility.Visible : UIVisibility.Collapsed;
            UIVisibility activeVisibility = activeId is not null && stored.Id == activeId ? UIVisibility.Visible : UIVisibility.Collapsed;

            if (rows.TryGet(stored.Id, out CardRow? existing))
            {
                existing.Name = stored.Document.Name;
                existing.Avatar = avatar;
                existing.DefaultVisibility = defaultVisibility;
                existing.ActiveVisibility = activeVisibility;
                TagBadges.Fill(existing.Tags, stored.Document.Tags, tagColors);

                if (!string.Equals(rows[i].Id, stored.Id, StringComparison.Ordinal))
                    _ = rows.Move(stored.Id, i);
            }
            else
            {
                CardRow added = RowOf(stored.Id, stored.Document, avatar, tagColors);
                added.DefaultVisibility = defaultVisibility;
                added.ActiveVisibility = activeVisibility;
                rows.Insert(i, added);
            }
        }
    }

    /// <summary>A card's row: its picture, its name, its tags in their colours.</summary>
    private static CardRow RowOf(string id, Card card, string? avatar, IReadOnlyDictionary<string, UIThemeColor> tagColors)
    {
        CardRow row = new() { Id = id, Name = card.Name, Avatar = avatar };

        TagBadges.Fill(row.Tags, card.Tags, tagColors);

        return row;
    }

    /// <summary>The personas, the active one first, then the rest by name.</summary>
    private void ShowPersonas()
    {
        var activePersonaId = ActivePersonaId ?? DefaultPersonaId;
        List<StoredDocument<Persona>> shown =
        [
            .. KnownPersonas
                .OrderByDescending(stored => stored.Id == activePersonaId)
                .ThenBy(static stored => stored.Document.Name, StringComparer.CurrentCultureIgnoreCase)
        ];

        SyncCardRows(PersonaRows, shown, Pictures.PersonaAvatarOf, _personaTagColors, activePersonaId, DefaultPersonaId);
        PersonasEmpty = new UIPhrase(KnownPersonas.Count == 0 ? "chatai.chats.no-personas" : "chatai.chats.no-match");
    }

    /// <summary>The worlds, the active one first, then the rest by name.</summary>
    private void ShowWorlds()
    {
        List<StoredDocument<World>> shown =
        [
            .. KnownWorlds
                .OrderByDescending(stored => stored.Id == ActiveWorldId)
                .ThenBy(static stored => stored.Document.Name, StringComparer.CurrentCultureIgnoreCase)
        ];

        SyncCardRows(WorldRows, shown, Pictures.WorldAvatarOf, _worldTagColors, ActiveWorldId);
        WorldsEmpty = new UIPhrase(KnownWorlds.Count == 0 ? "chatai.chats.no-worlds" : "chatai.chats.no-match");
    }

    /// <summary>The panel's character with its chats; a character gone, or none, and the panel shows the list instead.</summary>
    private void ShowPanelCharacter()
    {
        if (_panelCharacterId is not string characterId || KnownCharacters.FirstOrDefault(stored => stored.Id == characterId) is not StoredDocument<Character> character)
        {
            if (ChatEntries.Count > 0)
                ChatEntries.Clear();

            _panelCharacterId = null;
            ChatsPanes.ShowList();
            return;
        }

        var avatar = Pictures.CharacterAvatarOf(characterId);

        if (string.Equals(PanelCharacter.Id, characterId, StringComparison.Ordinal))
        {
            PanelCharacter.Name = character.Document.Name;
            PanelCharacter.Avatar = avatar;
        }
        else
        {
            PanelCharacter = new CardRow { Id = characterId, Name = character.Document.Name, Avatar = avatar };
        }

        List<ChatSummary> characterChats = [.. _chats.Where(chat => chat.CharacterId == characterId)];
        SyncChatEntries(characterChats);

        ChatsPanes.ShowDetail();
    }

    private void SyncChatEntries(List<ChatSummary> chats)
    {
        HashSet<string> kept = [with(StringComparer.Ordinal)];

        for (var i = 0; i < chats.Count; i++)
            _ = kept.Add(chats[i].Id);

        for (var i = ChatEntries.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(ChatEntries[i].Id))
                _ = ChatEntries.Remove(ChatEntries[i].Id);
        }

        for (var i = 0; i < chats.Count; i++)
        {
            ChatSummary chat = chats[i];
            UIPhrase title = EntryTitle(chat);
            UIPhrase description = UIPhrase.Text(Preview(chat.LastText));
            var selected = chat.Id == OpenChatId;

            if (ChatEntries.TryGet(chat.Id, out MenuItem? existing))
            {
                existing.Title = title;
                existing.Description = description;
                existing.Selected = selected;

                if (!string.Equals(ChatEntries[i].Id, chat.Id, StringComparison.Ordinal))
                    _ = ChatEntries.Move(chat.Id, i);
            }
            else
            {
                ChatEntries.Insert(i, new MenuItem { Id = chat.Id, Title = title, Description = description, Selected = selected });
            }
        }
    }

    private static UIPhrase EntryTitle(ChatSummary chat)
        => chat.LastAt == DateTimeOffset.MinValue ? UIPhrase.Text(chat.Name) : UIPhrase.Of("chatai.chats.at", ("at", new UIMoment(chat.LastAt, UITimestampFormat.Relative)));

    /// <summary>The chat's last words on one line, with the markup a message is written in taken off.</summary>
    private static string Preview(string lastText)
    {
        var text = UIInlineMarkup.ToPlainText(lastText).ReplaceLineEndings(" ").Trim();

        return text.Length <= PreviewLength ? text : string.Concat(text.AsSpan(0, PreviewLength), "…");
    }

    /// <summary>
    /// The page opened <paramref name="chatId"/>, a chat of <paramref name="characterId"/>: the panel shows that character from now
    /// on; a chat it has not read yet — one just started — has the chats read again.
    /// </summary>
    protected async Task FollowChatAsync(string chatId, string? characterId, CancellationToken cancellationToken)
    {
        OpenCharacterId = characterId;
        _panelCharacterId = characterId;

        if (_chats.Any(chat => chat.Id == chatId))
        {
            ShowCards();
            ShowPanelCharacter();
        }
        else
        {
            await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected void ShowLastWords(string chatId, string lastText, bool bumpToTop = false)
    {
        if (!ChatEntries.TryGet(chatId, out MenuItem? entry))
            return;

        entry.Description = UIPhrase.Text(Preview(lastText));

        if (bumpToTop)
        {
            entry.Title = UIPhrase.Of("chatai.chats.at", ("at", new UIMoment(DateTimeOffset.UtcNow, UITimestampFormat.Relative)));
            _ = ChatEntries.Move(chatId, 0);
        }
    }

    /// <summary>The chats panel opening reads the chats as they are now and shows the open chat's character.</summary>
    [UICommand]
    public Task LoadChatsPanelAsync(CancellationToken cancellationToken)
    {
        _panelCharacterId = OpenCharacterId;

        return LoadChatsAsync(cancellationToken);
    }

    /// <summary>The panel turned to the characters, the personas or the worlds; the search and that list's tags narrow it.</summary>
    [UICommand]
    public void SwitchList(string list)
    {
        var next = list is PersonasList or WorldsList ? list : CharactersList;

        if (PanelList != next)
        {
            PanelList = next;
            TagFilter = [];
        }

        ShowTagFilter();
    }

    [UICommand]
    public UICommandResult AskDeletePanelCard(string cardId)
        => AskDeleteCardByScope(PanelScopeOf(PanelList), cardId);

    private UICommandResult AskDeleteCardByScope(TagScope scope, string cardId)
    {
        if (!Slug.IsValidId(cardId))
            return UICommandResult.Ok();

        if (IsGenerating && scope == TagScope.Character && cardId == OpenCharacterId)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        string? name;
        string questionKey;
        string dialogKey;
        string list;

        switch (scope)
        {
            case TagScope.Persona:
                name = KnownPersonas.FirstOrDefault(stored => stored.Id == cardId)?.Document.Name;
                questionKey = "chatai.persona.delete.question";
                dialogKey = DeletePersonaDialogKey;
                list = PersonasList;
                break;

            case TagScope.World:
                name = KnownWorlds.FirstOrDefault(stored => stored.Id == cardId)?.Document.Name;
                questionKey = "chatai.world.delete.question";
                dialogKey = DeleteWorldDialogKey;
                list = WorldsList;
                break;

            case TagScope.Character:
            default:
                name = KnownCharacters.FirstOrDefault(stored => stored.Id == cardId)?.Document.Name;
                questionKey = "chatai.character.delete.question";
                dialogKey = DeleteCharacterDialogKey;
                list = CharactersList;
                break;
        }

        if (name is null)
            return UICommandResult.Ok();

        _deletingCardId = cardId;
        _deletingCardList = list;
        DeleteCardQuestion = UIPhrase.Of(questionKey, ("name", name));

        return UICommandResult.Ok([new OpenDialogEffect(dialogKey)]);
    }

    /// <summary>Deletes the card asked about in the panel's list or editor dialog and refreshes the lists and the open page.</summary>
    [UICommand]
    public async Task<UICommandResult> DeletePanelCardAsync(CancellationToken cancellationToken)
    {
        var cardId = _deletingCardId;
        var list = _deletingCardList ?? PanelList;

        _deletingCardId = null;
        _deletingCardList = null;

        var dialogKey = list switch
        {
            PersonasList => DeletePersonaDialogKey,
            WorldsList => DeleteWorldDialogKey,
            _ => DeleteCharacterDialogKey
        };

        if (cardId is null || !Slug.IsValidId(cardId))
            return UICommandResult.Ok([new CloseDialogEffect(dialogKey)]);

        if (IsGenerating && list == CharactersList && cardId == OpenCharacterId)
            return UICommandResult.Ok([new CloseDialogEffect(dialogKey), new ShowNotificationEffect("chatai.chat.busy", UIColorStyle.Info)]);

        List<ClientEffect> effects = [new CloseDialogEffect(dialogKey)];

        if (_editingCardId == cardId)
        {
            _editingCardId = null;
            effects.Add(new CloseDialogEffect(DialogKeyOf(_editingCardScope)));
        }

        switch (list)
        {
            case PersonasList:
                _ = Data.Personas.Delete(cardId);
                AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

                if (string.Equals(settings.Persona, cardId, StringComparison.Ordinal))
                    await Data.WriteSettingsAsync(settings with { Persona = null }, cancellationToken).ConfigureAwait(false);

                if (_editingCardPersona == cardId)
                    _editingCardPersona = null;

                foreach (StoredDocument<Character> stored in KnownCharacters)
                {
                    if (string.Equals(stored.Document.Persona, cardId, StringComparison.Ordinal))
                        await Data.Characters.WriteAsync(stored.Id, stored.Document with { Persona = null }, cancellationToken).ConfigureAwait(false);
                }

                break;

            case WorldsList:
                _ = Data.Worlds.Delete(cardId);

                if (_editingCardWorld == cardId)
                    _editingCardWorld = null;

                break;

            default:
                _ = await Chats.DeleteCharacterAsync(cardId, cancellationToken).ConfigureAwait(false);

                if (OpenCharacterId == cardId)
                    OpenCharacterId = null;

                break;
        }

        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
        UICommandResult extra = await OnPanelCardDeletedAsync(list, cardId, cancellationToken).ConfigureAwait(false);
        effects.AddRange(extra.Effects);

        return UICommandResult.Ok(effects);
    }

    /// <summary>Lets the open page react when a card is deleted from the panel's list.</summary>
    protected virtual Task<UICommandResult> OnPanelCardDeletedAsync(string list, string cardId, CancellationToken cancellationToken)
        => Task.FromResult(UICommandResult.Ok());

    /// <summary>A character picked: the panel shows its chats and the last one opens; with none, a chat is started.</summary>
    [UICommand]
    public async Task<UICommandResult> OpenCharacterAsync(string characterId, CancellationToken cancellationToken)
    {
        if (IsGenerating && characterId != OpenCharacterId)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        _panelCharacterId = characterId;
        ShowPanelCharacter();

        if (_panelCharacterId is null)
            return Notify("chatai.chats.character-gone", UIColorStyle.Warning);

        if (ChatEntries.Count > 0)
            return await ShowChatAsync(ChatEntries[0].Id, cancellationToken).ConfigureAwait(false);

        if (IsGenerating)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (await StartPanelChatAsync(cancellationToken).ConfigureAwait(false) is not string chatId)
            return Notify("chatai.chats.character-gone", UIColorStyle.Warning);

        _chats = await Chats.ListAsync(KnownCharacters, cancellationToken).ConfigureAwait(false);
        ShowCards();
        ShowPanelCharacter();

        return await ShowChatAsync(chatId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> StartPanelChatAsync(CancellationToken cancellationToken)
    {
        if (_panelCharacterId is not string characterId)
            return null;

        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        Character? character = await Data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false);

        return await Chats.StartAsync(characterId, ChatLibrary.ResolvePersonaId(Data, chat: null, character, settings.Persona), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A chat row clicked in the panel's chat list.</summary>
    [UICommand]
    public Task<UICommandResult> OpenPanelChatAsync(string chatId, CancellationToken cancellationToken)
        => ShowChatAsync(chatId, cancellationToken);

    /// <summary>A chat opened from the panel with the panel left open; a page that is not the chat goes to it.</summary>
    protected virtual Task<UICommandResult> ShowChatAsync(string chatId, CancellationToken cancellationToken)
        => Task.FromResult(UICommandResult.Ok([new NavigateEffect(AppRoutes.ChatOf(chatId))]));

    /// <summary>Opens the confirmation dialog before deleting a chat from the panel's chat list.</summary>
    [UICommand]
    public UICommandResult AskDeleteChat(string chatId)
    {
        if (!Slug.IsValidId(chatId) || !_chats.Any(chat => chat.Id == chatId))
            return UICommandResult.Ok();

        if (IsGenerating && chatId == OpenChatId)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        _deletingChatId = chatId;

        return UICommandResult.Ok([new OpenDialogEffect(DeleteChatDialogKey)]);
    }

    /// <summary>Deletes the chat asked about in the panel's chat list and refreshes the list and the open chat.</summary>
    [UICommand]
    public async Task<UICommandResult> DeletePanelChatAsync(CancellationToken cancellationToken)
    {
        var chatId = _deletingChatId;
        _deletingChatId = null;

        if (IsGenerating && chatId == OpenChatId)
            return UICommandResult.Ok([new CloseDialogEffect(DeleteChatDialogKey), new ShowNotificationEffect("chatai.chat.busy", UIColorStyle.Info)]);

        if (chatId is null || !Chats.Delete(chatId))
            return UICommandResult.Ok([new CloseDialogEffect(DeleteChatDialogKey)]);

        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        UICommandResult extra = await OnPanelChatDeletedAsync(chatId, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok([new CloseDialogEffect(DeleteChatDialogKey), .. extra.Effects]);
    }

    protected virtual Task<UICommandResult> OnPanelChatDeletedAsync(string chatId, CancellationToken cancellationToken)
        => Task.FromResult(UICommandResult.Ok());

    [UICommand]
    public void ShowCharacterList()
    {
        _panelCharacterId = null;
        ShowPanelCharacter();
    }

    /// <summary>Starts a chat with the panel's character and opens it.</summary>
    [UICommand]
    public async Task<UICommandResult> StartChatAsync(CancellationToken cancellationToken)
    {
        if (_panelCharacterId is null)
            return UICommandResult.Ok();

        if (IsGenerating)
            return Notify("chatai.chat.busy", UIColorStyle.Info);

        if (await StartPanelChatAsync(cancellationToken).ConfigureAwait(false) is not string chatId)
            return Notify("chatai.chats.character-gone", UIColorStyle.Warning);

        _chats = await Chats.ListAsync(KnownCharacters, cancellationToken).ConfigureAwait(false);
        ShowCards();
        ShowPanelCharacter();

        return await ShowChatAsync(chatId, cancellationToken).ConfigureAwait(false);
    }
}
