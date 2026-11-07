using System;
using System.Collections.Generic;
using System.Globalization;
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

    [RecursiveMember]
    public partial UIVisibility TagsVisibility { get; set; } = UIVisibility.Collapsed;

    /// <summary>Shown on the persona new chats are started as.</summary>
    [RecursiveMember]
    public partial UIVisibility DefaultVisibility { get; set; } = UIVisibility.Collapsed;

    /// <summary>Shown on the character, persona or world active in the open chat.</summary>
    [RecursiveMember]
    public partial UIVisibility ActiveVisibility { get; set; } = UIVisibility.Collapsed;
}

/// <summary>
/// The chats panel: the characters, the one last talked to first, or the personas, the default one first — either narrowed by a
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
    private const string EmptyTagFilterId = "__none";

    /// <summary>What the last read found, so the panel turns between a character and the list without reading the folder again.</summary>
    private IReadOnlyList<ChatSummary> _chats = [];
    private IReadOnlyList<StoredDocument<Character>> _characters = [];
    private Dictionary<string, UIThemeColor> _characterTagColors = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, UIThemeColor> _personaTagColors = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, UIThemeColor> _worldTagColors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The persona new chats are started as; none, and the reader is "User".</summary>
    protected string? DefaultPersonaId { get; private set; }

    /// <summary>The persona active in the open chat.</summary>
    protected string? ActivePersonaId { get; set; }

    /// <summary>The world active in the open chat.</summary>
    protected string? ActiveWorldId { get; set; }

    /// <summary>Every persona, as the last read of the chats found them.</summary>
    protected IReadOnlyList<StoredDocument<Persona>> KnownPersonas { get; private set; } = [];

    /// <summary>Every world, as the last read of the chats found them.</summary>
    protected IReadOnlyList<StoredDocument<World>> KnownWorlds { get; private set; } = [];

    /// <summary>The open chat's character, which the panel shows each time it opens.</summary>
    private string? _openCharacterId;

    /// <summary>The character whose chats the panel shows; none, and it shows the characters.</summary>
    private string? _panelCharacterId;

    private string? _deletingCardId;
    private string? _deletingCardList;
    private string? _deletingChatId;

    [RecursiveMember]
    public partial string CharacterSearch { get; set; } = string.Empty;

    [RecursiveMember(false)]
    public RecursiveCollection<MenuItem> TagFilterItems { get; } = [];

    [RecursiveMember]
    public partial IReadOnlyList<string> TagFilter { get; set; } = [];

    /// <summary>How many tags currently narrow the list, shown on the filter button; <see langword="null"/> when none.</summary>
    [RecursiveMember]
    public partial string? TagFilterCount { get; set; }

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

    [RecursiveMember]
    public partial UIVisibility CharacterListVisibility { get; set; } = UIVisibility.Visible;

    [RecursiveMember(false)]
    public RecursiveCollection<CardRow> PersonaRows { get; } = [];

    [RecursiveMember]
    public partial UIPhrase PersonasEmpty { get; set; } = new("chatai.chats.no-personas");

    [RecursiveMember]
    public partial UIVisibility PersonaListVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember(false)]
    public RecursiveCollection<CardRow> WorldRows { get; } = [];

    [RecursiveMember]
    public partial UIPhrase WorldsEmpty { get; set; } = new("chatai.chats.no-worlds");

    [RecursiveMember]
    public partial UIVisibility WorldListVisibility { get; set; } = UIVisibility.Collapsed;

    /// <summary>The lists with their switch, search and tags; a character picked puts them away for its chats.</summary>
    [RecursiveMember]
    public partial UIVisibility CharactersVisibility { get; set; } = UIVisibility.Visible;

    [RecursiveMember]
    public partial UIVisibility CharacterChatsVisibility { get; set; } = UIVisibility.Collapsed;

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

    /// <summary>
    /// Reads the chats, the characters, the personas, the default persona and the tags' colours, and shows them as the search, the
    /// tags and the panel's character say.
    /// </summary>
    protected async Task LoadChatsAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        _chats = await Chats.ListAsync(cancellationToken).ConfigureAwait(false);
        _characters = (await Data.Characters.ListAsync(cancellationToken).ConfigureAwait(false)).Items;
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
    }

    /// <summary>The filter menu's tags for the list currently shown; each kind of card keeps its own tags.</summary>
    private void ShowTagFilter()
    {
        (IEnumerable<string> source, IReadOnlyDictionary<string, UIThemeColor> colors) = PanelList switch
        {
            PersonasList => (KnownPersonas.SelectMany(static stored => stored.Document.Tags), _personaTagColors),
            WorldsList => (KnownWorlds.SelectMany(static stored => stored.Document.Tags), _worldTagColors),
            _ => (_characters.SelectMany(static stored => stored.Document.Tags), _characterTagColors)
        };
        string[] knownTags = [.. source.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];

        if (TagFilter.Count > 0)
            TagFilter = [.. TagFilter.Where(tag => knownTags.Contains(tag, StringComparer.OrdinalIgnoreCase))];

        HashSet<string> chosen = new(TagFilter, StringComparer.OrdinalIgnoreCase);

        TagFilterItems.Clear();

        if (knownTags.Length == 0)
        {
            TagFilterItems.Add(new MenuItem { Id = EmptyTagFilterId, Title = "chatai.tags.none", Enabled = false });
        }
        else
        {
            foreach (var tag in knownTags)
                TagFilterItems.Add(TagBadges.MenuItemOf(tag, colors, chosen.Contains(tag)));
        }
    }

    /// <summary>The three lists as the search and the active list's tags narrow them.</summary>
    protected void ShowCards()
    {
        var search = CharacterSearch.Trim();
        HashSet<string> chosen = new(TagFilter, StringComparer.OrdinalIgnoreCase);
        HashSet<string> none = [];

        TagFilterCount = chosen.Count > 0 ? chosen.Count.ToString(CultureInfo.InvariantCulture) : null;

        ShowCharacters(search, PanelList == CharactersList ? chosen : none);
        ShowPersonas(search, PanelList == PersonasList ? chosen : none);
        ShowWorlds(search, PanelList == WorldsList ? chosen : none);
    }

    /// <summary>The characters let through, the one last talked to first, then the rest by name.</summary>
    private void ShowCharacters(string search, HashSet<string> chosen)
    {
        Dictionary<string, DateTimeOffset> lastAt = new(StringComparer.Ordinal);

        foreach (ChatSummary chat in _chats)
        {
            if (chat.CharacterId is string id && (!lastAt.TryGetValue(id, out DateTimeOffset at) || chat.LastAt > at))
                lastAt[id] = chat.LastAt;
        }

        CharacterRows.Clear();

        IEnumerable<StoredDocument<Character>> shown = _characters
            .Where(stored => Matches(stored.Document, search, chosen))
            .OrderByDescending(stored => lastAt.GetValueOrDefault(stored.Id, DateTimeOffset.MinValue))
            .ThenBy(static stored => stored.Document.Name, StringComparer.CurrentCultureIgnoreCase);

        foreach (StoredDocument<Character> stored in shown)
        {
            CardRow row = RowOf(stored.Id, stored.Document, Pictures.CharacterAvatarOf(stored.Id), _characterTagColors);

            row.ActiveVisibility = stored.Id == _openCharacterId ? UIVisibility.Visible : UIVisibility.Collapsed;
            CharacterRows.Add(row);
        }

        CharactersEmpty = new UIPhrase(_characters.Count == 0 ? "chatai.chats.no-characters" : "chatai.chats.no-match");
    }

    private static bool Matches(Card card, string search, HashSet<string> chosen)
        => (search.Length == 0 || card.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)) && (chosen.Count == 0 || card.Tags.Any(chosen.Contains));

    /// <summary>A card's row: its picture, its name, its tags in their colours.</summary>
    private static CardRow RowOf(string id, Card card, string? avatar, IReadOnlyDictionary<string, UIThemeColor> tagColors)
    {
        CardRow row = new() { Id = id, Name = card.Name, Avatar = avatar };

        TagBadges.Fill(row.Tags, card.Tags, tagColors);
        row.TagsVisibility = row.Tags.Count == 0 ? UIVisibility.Collapsed : UIVisibility.Visible;

        return row;
    }

    /// <summary>The personas let through, the default one first, then the rest by name.</summary>
    private void ShowPersonas(string search, HashSet<string> chosen)
    {
        PersonaRows.Clear();

        var activePersonaId = ActivePersonaId ?? DefaultPersonaId;
        IEnumerable<StoredDocument<Persona>> shown = KnownPersonas
            .Where(stored => Matches(stored.Document, search, chosen))
            .OrderByDescending(stored => stored.Id == DefaultPersonaId)
            .ThenBy(static stored => stored.Document.Name, StringComparer.CurrentCultureIgnoreCase);

        foreach (StoredDocument<Persona> stored in shown)
        {
            CardRow row = RowOf(stored.Id, stored.Document, Pictures.PersonaAvatarOf(stored.Id), _personaTagColors);

            row.DefaultVisibility = stored.Id == DefaultPersonaId ? UIVisibility.Visible : UIVisibility.Collapsed;
            row.ActiveVisibility = stored.Id == activePersonaId ? UIVisibility.Visible : UIVisibility.Collapsed;
            PersonaRows.Add(row);
        }

        PersonasEmpty = new UIPhrase(KnownPersonas.Count == 0 ? "chatai.chats.no-personas" : "chatai.chats.no-match");
    }

    /// <summary>The worlds let through, by name.</summary>
    private void ShowWorlds(string search, HashSet<string> chosen)
    {
        WorldRows.Clear();

        IEnumerable<StoredDocument<World>> shown = KnownWorlds
            .Where(stored => Matches(stored.Document, search, chosen))
            .OrderBy(static stored => stored.Document.Name, StringComparer.CurrentCultureIgnoreCase);

        foreach (StoredDocument<World> stored in shown)
        {
            CardRow row = RowOf(stored.Id, stored.Document, Pictures.WorldAvatarOf(stored.Id), _worldTagColors);

            row.ActiveVisibility = stored.Id == ActiveWorldId ? UIVisibility.Visible : UIVisibility.Collapsed;
            WorldRows.Add(row);
        }

        WorldsEmpty = new UIPhrase(KnownWorlds.Count == 0 ? "chatai.chats.no-worlds" : "chatai.chats.no-match");
    }

    /// <summary>The panel's character with its chats; a character gone, or none, and the panel shows the list instead.</summary>
    private void ShowPanelCharacter()
    {
        ChatEntries.Clear();

        if (_panelCharacterId is not string characterId || _characters.FirstOrDefault(stored => stored.Id == characterId) is not StoredDocument<Character> character)
        {
            _panelCharacterId = null;
            CharactersVisibility = UIVisibility.Visible;
            CharacterChatsVisibility = UIVisibility.Collapsed;
            return;
        }

        PanelCharacter = new CardRow { Id = characterId, Name = character.Document.Name, Avatar = Pictures.CharacterAvatarOf(characterId) };

        foreach (ChatSummary chat in _chats.Where(chat => chat.CharacterId == characterId))
            ChatEntries.Add(Entry(chat));

        CharactersVisibility = UIVisibility.Collapsed;
        CharacterChatsVisibility = UIVisibility.Visible;
    }

    /// <summary>A chat's row: when it was last written to, and its last words.</summary>
    private MenuItem Entry(ChatSummary chat)
        => new()
        {
            Id = chat.Id,
            Title = chat.LastAt == DateTimeOffset.MinValue ? UIPhrase.Text(chat.Name) : UIPhrase.Of("chatai.chats.at", ("at", new UIMoment(chat.LastAt, UITimestampFormat.Relative))),
            Description = UIPhrase.Text(Preview(chat.LastText)),
            Selected = chat.Id == OpenChatId
        };

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
        _openCharacterId = characterId;
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

    /// <summary>
    /// A chat just written to: its row takes the new last words and goes to the top, and its character's row to the top of the
    /// characters, the newest first as a read would put them; a message with no words yet — a thought cut short — leaves the row's
    /// words as they were. Called under the runtime's lock.
    /// </summary>
    protected void ShowLastWords(string chatId, string lastText)
    {
        var preview = Preview(lastText);

        if (ChatEntries.TryGet(chatId, out MenuItem? entry))
        {
            if (preview.Length > 0)
                entry.Description = UIPhrase.Text(preview);

            _ = ChatEntries.Move(chatId, 0);
        }

        if (_openCharacterId is string characterId)
            _ = CharacterRows.Move(characterId, 0);
    }

    /// <summary>The chats panel opening reads the chats as they are now and shows the open chat's character.</summary>
    [UICommand]
    public Task LoadChatsPanelAsync(CancellationToken cancellationToken)
    {
        _panelCharacterId = _openCharacterId;

        return LoadChatsAsync(cancellationToken);
    }

    [UICommand]
    public void FilterCards()
        => ShowCards();

    /// <summary>Toggles one tag in the filter menu and narrows the card lists.</summary>
    [UICommand]
    public void ToggleTagFilter(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag == EmptyTagFilterId)
            return;

        HashSet<string> chosen = new(TagFilter, StringComparer.OrdinalIgnoreCase);

        if (!chosen.Remove(tag))
            _ = chosen.Add(tag);

        TagFilter = [.. chosen];

        foreach (MenuItem item in TagFilterItems)
            item.Checked = chosen.Contains(item.Id);

        ShowCards();
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

        CharacterListVisibility = PanelList == CharactersList ? UIVisibility.Visible : UIVisibility.Collapsed;
        PersonaListVisibility = PanelList == PersonasList ? UIVisibility.Visible : UIVisibility.Collapsed;
        WorldListVisibility = PanelList == WorldsList ? UIVisibility.Visible : UIVisibility.Collapsed;
        ShowTagFilter();
        ShowCards();
    }

    /// <summary>Opens the confirmation dialog before deleting a card from its row in the panel's list.</summary>
    [UICommand]
    public UICommandResult AskDeletePanelCard(string cardId)
    {
        TagScope scope = PanelList switch
        {
            PersonasList => TagScope.Persona,
            WorldsList => TagScope.World,
            _ => TagScope.Character
        };

        return AskDeleteCardByScope(scope, cardId);
    }

    private UICommandResult AskDeleteCardByScope(TagScope scope, string cardId)
    {
        if (!Slug.IsValidId(cardId))
            return UICommandResult.Ok();

        if (IsGenerating && scope == TagScope.Character && cardId == _openCharacterId)
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
                name = _characters.FirstOrDefault(stored => stored.Id == cardId)?.Document.Name;
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

        if (IsGenerating && list == CharactersList && cardId == _openCharacterId)
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

                break;

            case WorldsList:
                _ = Data.Worlds.Delete(cardId);

                if (_editingCardWorld == cardId)
                    _editingCardWorld = null;

                break;

            default:
                _ = await Chats.DeleteCharacterAsync(cardId, cancellationToken).ConfigureAwait(false);

                if (_openCharacterId == cardId)
                    _openCharacterId = null;

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
        if (IsGenerating && characterId != _openCharacterId)
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

        _chats = await Chats.ListAsync(cancellationToken).ConfigureAwait(false);
        ShowCards();
        ShowPanelCharacter();

        return await ShowChatAsync(chatId, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Lets the open page react when a chat is deleted from the panel's chat list.</summary>
    protected virtual Task<UICommandResult> OnPanelChatDeletedAsync(string chatId, CancellationToken cancellationToken)
        => Task.FromResult(UICommandResult.Ok());

    /// <summary>A chat started with the panel's character; none where there is no such character.</summary>
    private async Task<string?> StartPanelChatAsync(CancellationToken cancellationToken)
    {
        if (_panelCharacterId is not string characterId)
            return null;

        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);

        return await Chats.StartAsync(characterId, settings.Persona, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Back from a character's chats to the characters.</summary>
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

        _chats = await Chats.ListAsync(cancellationToken).ConfigureAwait(false);
        ShowCards();
        ShowPanelCharacter();

        return await ShowChatAsync(chatId, cancellationToken).ConfigureAwait(false);
    }
}
