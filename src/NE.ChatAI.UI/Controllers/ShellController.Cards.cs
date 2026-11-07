using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>One of a character's first messages, a variant a new chat opens with.</summary>
internal sealed partial class GreetingItem : RecursiveObservable, IBindableItem
{
    /// <summary>The row's own key while the dialog is open; greetings are kept as a list, not by id.</summary>
    [RecursiveMember(false)]
    public string Id { get; } = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);

    [RecursiveMember]
    public partial string Text { get; set; } = string.Empty;
}

/// <summary>One article of a world in its editor dialog: its title, its words, where they go and whether they go.</summary>
internal sealed partial class ArticleItem : RecursiveObservable, IBindableItem
{
    /// <summary>The row's own key while the dialog is open; articles are kept as a list, not by id.</summary>
    [RecursiveMember(false)]
    public string Id { get; } = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);

    [RecursiveMember]
    public partial string Title { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>A <see cref="LorePosition"/>'s name.</summary>
    [RecursiveMember]
    public partial string Position { get; set; } = nameof(LorePosition.AfterStory);

    [RecursiveMember]
    public partial bool Enabled { get; set; } = true;
}

/// <summary>
/// The wide card-editor dialogs hosted on the shell: creating and editing characters, personas and worlds without leaving the open page
/// or closing an unpinned chats drawer.
/// </summary>
internal abstract partial class ShellController
{
    public const string CreateCardDialogKey = "chatai-card-create";
    public const string CharacterDialogKey = "chatai-character-dialog";
    public const string PersonaDialogKey = "chatai-persona-dialog";
    public const string WorldDialogKey = "chatai-world-dialog";

    /// <summary>The largest picture taken; the crop writes a square of <see cref="AvatarSize"/>, far under it.</summary>
    public const long MaxAvatarBytes = 8 * 1024 * 1024;

    /// <summary>The side of the square an avatar is cropped to.</summary>
    public const int AvatarSize = 512;

    /// <summary>The world field's entry for none: an id no document can have.</summary>
    public const string NoWorld = "none";

    private string? _creatingCardList;
    private TagScope _editingCardScope;
    private string? _editingCardId;
    private string? _editingCardWorld;

    [RecursiveMember]
    public partial UIPhrase? CreateCardTitle { get; set; }

    [RecursiveMember]
    public partial string NewCardName { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string CardName { get; set; } = string.Empty;

    /// <summary>The card's picture; none, and the field shows the person's glyph.</summary>
    [RecursiveMember]
    public partial PictureSlot CardAvatar { get; set; } = new();

    [RecursiveMember]
    public partial string CardDescription { get; set; } = string.Empty;

    /// <summary>The world the card is played in, by id; <see cref="NoWorld"/> for none.</summary>
    [RecursiveMember]
    public partial string CardWorldId { get; set; } = NoWorld;

    /// <summary>None, then every world by name.</summary>
    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> CardWorldOptions { get; } = [];

    [RecursiveMember]
    public partial string CharacterPersonality { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string CharacterScenario { get; set; } = string.Empty;

    [RecursiveMember(false)]
    public RecursiveCollection<GreetingItem> CharacterGreetings { get; } = [];

    [RecursiveMember]
    public partial string CharacterExampleMessages { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string CharacterSystemPrompt { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string CharacterPostHistoryInstructions { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string CharacterCreatorNotes { get; set; } = string.Empty;

    /// <summary>The character's author's note, added to the global one and the chat's.</summary>
    [RecursiveMember]
    public partial string CharacterAuthorsNote { get; set; } = string.Empty;

    /// <summary>The picture behind the character's chats; none, and they take the application's.</summary>
    [RecursiveMember]
    public partial PictureSlot CharacterBackground { get; set; } = new();

    /// <summary>Whether new chats are started as this persona; one persona at a time is.</summary>
    [RecursiveMember]
    public partial bool PersonaIsDefault { get; set; }

    [RecursiveMember(false)]
    public RecursiveCollection<ArticleItem> WorldArticles { get; } = [];

    /// <summary>Opens the name dialog before creating a new character, persona or world.</summary>
    [UICommand]
    public UICommandResult AskCreateCard()
    {
        _creatingCardList = PanelList;
        NewCardName = string.Empty;
        CreateCardTitle = new UIPhrase(PanelList switch
        {
            PersonasList => "chatai.persona.new-name",
            WorldsList => "chatai.world.new-name",
            _ => "chatai.character.new-name"
        });

        return UICommandResult.Ok([new OpenDialogEffect(CreateCardDialogKey)]);
    }

    /// <summary>Creates the character, persona or world under the typed name and opens its wide editor dialog.</summary>
    [UICommand]
    public async Task<UICommandResult> CreateCardAsync(CancellationToken cancellationToken)
    {
        var list = _creatingCardList ?? PanelList;
        var typed = NewCardName.Trim();
        _creatingCardList = null;

        TagScope scope;
        string cardId;

        if (list == WorldsList)
        {
            var name = typed.Length > 0 ? typed : Context.Translate("chatai.world.new-name") ?? string.Empty;
            scope = TagScope.World;
            cardId = await Data.Worlds.CreateAsync(new World { Name = name }, cancellationToken).ConfigureAwait(false);
        }
        else if (list == PersonasList)
        {
            var name = typed.Length > 0 ? typed : Context.Translate("chatai.persona.new-name") ?? string.Empty;
            scope = TagScope.Persona;
            cardId = await Data.Personas.CreateAsync(new Persona { Name = name }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var name = typed.Length > 0 ? typed : Context.Translate("chatai.character.new-name") ?? string.Empty;
            scope = TagScope.Character;
            cardId = await Data.Characters.CreateAsync(new Character { Name = name }, cancellationToken).ConfigureAwait(false);
        }

        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);

        if (await OpenCardEditorAsync(scope, cardId, cancellationToken).ConfigureAwait(false) is not string dialogKey)
            return UICommandResult.Ok([new CloseDialogEffect(CreateCardDialogKey)]);

        return UICommandResult.Ok([new CloseDialogEffect(CreateCardDialogKey), new OpenDialogEffect(dialogKey)]);
    }

    /// <summary>Lets the open page refresh its state when a card or its pictures change.</summary>
    protected virtual Task OnCardChangedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    private async Task<string?> OpenCardEditorAsync(TagScope scope, string cardId, CancellationToken cancellationToken)
    {
        if (!Slug.IsValidId(cardId))
            return null;

        Card? baseCard;
        string? avatar;

        switch (scope)
        {
            case TagScope.Persona:
                if (await Data.Personas.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is not Persona persona)
                    return null;

                baseCard = persona;
                avatar = Pictures.PersonaAvatarOf(cardId);
                PersonaIsDefault = cardId == DefaultPersonaId;
                break;

            case TagScope.World:
                if (await Data.Worlds.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is not World world)
                    return null;

                baseCard = world;
                avatar = Pictures.WorldAvatarOf(cardId);
                ShowWorldCard(world);
                break;

            case TagScope.Character:
            default:
                if (await Data.Characters.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is not Character character)
                    return null;

                baseCard = character;
                avatar = Pictures.CharacterAvatarOf(cardId);
                ShowCharacterCard(cardId, character);
                break;
        }

        _editingCardScope = scope;
        _editingCardId = cardId;
        _editingCardWorld = baseCard.World;
        CardName = baseCard.Name;
        CardAvatar.Show(avatar);
        CardTags = baseCard.Tags;
        CardDescription = baseCard.Description.TrimEnd('\r', '\n');
        ShowCardWorlds(baseCard.World);
        await ShowTagsAsync(cancellationToken).ConfigureAwait(false);

        return DialogKeyOf(scope);
    }

    private void ShowWorldCard(World card)
    {
        WorldArticles.Clear();

        foreach (WorldArticle article in card.Articles)
            WorldArticles.Add(new ArticleItem { Title = article.Title, Text = article.Text.TrimEnd('\r', '\n'), Position = article.Position.ToString(), Enabled = article.Enabled });
    }

    private void ShowCharacterCard(string cardId, Character card)
    {
        CharacterPersonality = card.Personality.TrimEnd('\r', '\n');
        CharacterScenario = card.Scenario.TrimEnd('\r', '\n');
        CharacterGreetings.Clear();

        foreach (var text in card.FirstMessages)
            CharacterGreetings.Add(new GreetingItem { Text = text.TrimEnd('\r', '\n') });

        CharacterExampleMessages = card.ExampleMessages.TrimEnd('\r', '\n');
        CharacterSystemPrompt = card.SystemPrompt?.TrimEnd('\r', '\n') ?? string.Empty;
        CharacterPostHistoryInstructions = card.PostHistoryInstructions?.TrimEnd('\r', '\n') ?? string.Empty;
        CharacterCreatorNotes = card.CreatorNotes.TrimEnd('\r', '\n');
        CharacterAuthorsNote = card.AuthorsNote?.TrimEnd('\r', '\n') ?? string.Empty;
        CharacterBackground.Show(Pictures.CharacterBackgroundOf(cardId));
    }

    /// <summary>None and every world in the field's list, the card's own chosen; a world gone reads as none.</summary>
    private void ShowCardWorlds(string? world)
    {
        CardWorldOptions.Clear();
        CardWorldOptions.Add(new OptionItem { Id = NoWorld, Title = "chatai.card.world.none" });

        foreach (StoredDocument<World> stored in KnownWorlds.OrderBy(static stored => stored.Document.Name, StringComparer.CurrentCultureIgnoreCase))
            CardWorldOptions.Add(new OptionItem { Id = stored.Id, Title = stored.Document.Name, IsContent = true });

        CardWorldId = world is not null && KnownWorlds.Any(stored => stored.Id == world) ? world : NoWorld;
    }

    private static string DialogKeyOf(TagScope scope)
        => scope switch
        {
            TagScope.Persona => PersonaDialogKey,
            TagScope.World => WorldDialogKey,
            _ => CharacterDialogKey
        };

    /// <summary>A persona picked in the panel: opens its editor dialog.</summary>
    [UICommand]
    public async Task<UICommandResult> OpenPersonaAsync(string personaId, CancellationToken cancellationToken)
        => await OpenCardEditorAsync(TagScope.Persona, personaId, cancellationToken).ConfigureAwait(false) is string dialogKey
            ? UICommandResult.Ok([new OpenDialogEffect(dialogKey)])
            : UICommandResult.Ok();

    /// <summary>A world picked in the panel: opens its editor dialog.</summary>
    [UICommand]
    public async Task<UICommandResult> OpenWorldAsync(string worldId, CancellationToken cancellationToken)
        => await OpenCardEditorAsync(TagScope.World, worldId, cancellationToken).ConfigureAwait(false) is string dialogKey
            ? UICommandResult.Ok([new OpenDialogEffect(dialogKey)])
            : UICommandResult.Ok();

    /// <summary>Opens the editor dialog of a card from its row in the panel's list.</summary>
    [UICommand]
    public async Task<UICommandResult> EditPanelCardAsync(string cardId, CancellationToken cancellationToken)
    {
        TagScope scope = PanelList switch
        {
            PersonasList => TagScope.Persona,
            WorldsList => TagScope.World,
            _ => TagScope.Character
        };

        return await OpenCardEditorAsync(scope, cardId, cancellationToken).ConfigureAwait(false) is string dialogKey
            ? UICommandResult.Ok([new OpenDialogEffect(dialogKey)])
            : UICommandResult.Ok();
    }

    /// <summary>Opens the panel character's editor dialog.</summary>
    [UICommand]
    public async Task<UICommandResult> EditCharacterAsync(CancellationToken cancellationToken)
        => _panelCharacterId is string characterId && await OpenCardEditorAsync(TagScope.Character, characterId, cancellationToken).ConfigureAwait(false) is string dialogKey
            ? UICommandResult.Ok([new OpenDialogEffect(dialogKey)])
            : UICommandResult.Ok();

    /// <summary>Keeps the card's name, description, world and kind-specific fields; updates the lists and the open chat at once.</summary>
    [UICommand]
    public async Task SaveCardAsync(CancellationToken cancellationToken)
    {
        if (_editingCardId is not string cardId || !Slug.IsValidId(cardId))
            return;

        var name = CardName.Trim();
        var description = CardDescription.TrimEnd('\r', '\n');
        _editingCardWorld = CardWorldId == NoWorld ? null : CardWorldId;

        switch (_editingCardScope)
        {
            case TagScope.Persona:
                if (await Data.Personas.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is not Persona persona)
                    return;

                await Data.Personas.WriteAsync(cardId, persona with { Name = name.Length == 0 ? persona.Name : name, Description = description, World = _editingCardWorld }, cancellationToken).ConfigureAwait(false);
                break;

            case TagScope.World:
                if (await Data.Worlds.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is not World world)
                    return;

                await Data.Worlds.WriteAsync(cardId, ApplyWorldFields(world with { Name = name.Length == 0 ? world.Name : name, Description = description }), cancellationToken).ConfigureAwait(false);
                break;

            case TagScope.Character:
            default:
                if (await Data.Characters.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is not Character character)
                    return;

                await Data.Characters.WriteAsync(cardId, ApplyCharacterFields(character with { Name = name.Length == 0 ? character.Name : name, Description = description, World = _editingCardWorld }), cancellationToken).ConfigureAwait(false);
                break;
        }

        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    private World ApplyWorldFields(World card)
        => card with
        {
            Articles =
            [
                .. WorldArticles
                    .Where(static article => !string.IsNullOrWhiteSpace(article.Title) || !string.IsNullOrWhiteSpace(article.Text))
                    .Select(static article => new WorldArticle
                    {
                        Title = article.Title.Trim(),
                        Text = article.Text.TrimEnd('\r', '\n'),
                        Position = Enum.TryParse(article.Position, out LorePosition position) ? position : LorePosition.AfterStory,
                        Enabled = article.Enabled
                    })
            ]
        };

    private Character ApplyCharacterFields(Character card)
        => card with
        {
            Personality = CharacterPersonality.TrimEnd('\r', '\n'),
            Scenario = CharacterScenario.TrimEnd('\r', '\n'),
            FirstMessages = [.. CharacterGreetings.Select(static greeting => greeting.Text.TrimEnd('\r', '\n')).Where(static text => !string.IsNullOrWhiteSpace(text))],
            ExampleMessages = CharacterExampleMessages.TrimEnd('\r', '\n'),
            SystemPrompt = string.IsNullOrWhiteSpace(CharacterSystemPrompt) ? null : CharacterSystemPrompt.TrimEnd('\r', '\n'),
            PostHistoryInstructions = string.IsNullOrWhiteSpace(CharacterPostHistoryInstructions) ? null : CharacterPostHistoryInstructions.TrimEnd('\r', '\n'),
            CreatorNotes = CharacterCreatorNotes.TrimEnd('\r', '\n'),
            AuthorsNote = string.IsNullOrWhiteSpace(CharacterAuthorsNote) ? null : CharacterAuthorsNote.TrimEnd('\r', '\n')
        };

    /// <summary>Takes in the picture the field just uploaded, already cropped by the page, as the card's avatar.</summary>
    [UICommand]
    public async Task SaveCardAvatarAsync(CancellationToken cancellationToken)
    {
        if (_editingCardId is not string id || await TakePictureAsync(CardAvatar, cancellationToken).ConfigureAwait(false) is not var (content, contentType))
            return;

        switch (_editingCardScope)
        {
            case TagScope.Persona:
                await CardPictures.WriteAsync(Data.Personas, id, CardPictures.Avatar, content, contentType, cancellationToken).ConfigureAwait(false);
                CardAvatar.Show(Pictures.PersonaAvatarOf(id));
                break;

            case TagScope.World:
                await CardPictures.WriteAsync(Data.Worlds, id, CardPictures.Avatar, content, contentType, cancellationToken).ConfigureAwait(false);
                CardAvatar.Show(Pictures.WorldAvatarOf(id));
                break;

            case TagScope.Character:
            default:
                await CardPictures.WriteAsync(Data.Characters, id, CardPictures.Avatar, content, contentType, cancellationToken).ConfigureAwait(false);
                CardAvatar.Show(Pictures.CharacterAvatarOf(id));
                break;
        }

        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes in the picture the field just uploaded as the one behind the character's chats.</summary>
    [UICommand]
    public async Task SaveCharacterBackgroundAsync(CancellationToken cancellationToken)
    {
        if (_editingCardId is not string id || _editingCardScope != TagScope.Character || await TakePictureAsync(CharacterBackground, cancellationToken).ConfigureAwait(false) is not var (content, contentType))
            return;

        await CardPictures.WriteAsync(Data.Characters, id, CardPictures.Background, content, contentType, cancellationToken).ConfigureAwait(false);
        CharacterBackground.Show(Pictures.CharacterBackgroundOf(id));
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the picture away; the character's chats stand on the application's.</summary>
    [UICommand]
    public async Task RemoveCharacterBackgroundAsync(CancellationToken cancellationToken)
    {
        if (_editingCardId is not string id || _editingCardScope != TagScope.Character)
            return;

        CardPictures.Delete(Data.Characters, id, CardPictures.Background);
        CharacterBackground.Show(Pictures.CharacterBackgroundOf(id));
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A new greeting to write; it is kept once it has words.</summary>
    [UICommand]
    public void AddGreeting()
        => CharacterGreetings.Add(new GreetingItem());

    [UICommand]
    public async Task RemoveGreetingAsync(string greetingId, CancellationToken cancellationToken)
    {
        if (!CharacterGreetings.Remove(greetingId))
            return;

        await SaveCardAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes this persona the one new chats are started as, or, switched off, leaves new chats without one.</summary>
    [UICommand]
    public async Task SavePersonaDefaultAsync(CancellationToken cancellationToken)
    {
        if (_editingCardId is not string id || _editingCardScope != TagScope.Persona)
            return;

        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var persona = PersonaIsDefault ? id : settings.Persona == id ? null : settings.Persona;

        await Data.WriteSettingsAsync(settings with { Persona = persona }, cancellationToken).ConfigureAwait(false);
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A new article to write; it is kept once it has a title or words.</summary>
    [UICommand]
    public void AddArticle()
        => WorldArticles.Add(new ArticleItem());

    [UICommand]
    public async Task RemoveArticleAsync(string articleId, CancellationToken cancellationToken)
    {
        if (!WorldArticles.Remove(articleId))
            return;

        await SaveCardAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks to delete the card currently open in the editor dialog.</summary>
    [UICommand]
    public UICommandResult AskDeleteEditingCard()
    {
        if (_editingCardId is not string cardId)
            return UICommandResult.Ok();

        return AskDeleteCardByScope(_editingCardScope, cardId);
    }
}
