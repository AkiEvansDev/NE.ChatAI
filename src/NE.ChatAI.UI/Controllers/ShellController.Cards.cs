using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Prompts;
using NE.ChatAI.Settings;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>One folded entry in a card editor's accordion list: a greeting of a character or an article of a world.</summary>
internal abstract partial class CardFoldItem : RecursiveObservable, IBindableItem
{
    private const int MaxPreviewLength = 80;

    /// <summary>The row's own key while the dialog is open; entries are kept as a list, not by id.</summary>
    [RecursiveMember(false)]
    public string Id { get; } = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);

    [RecursiveMember]
    public partial string Text { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase Header { get; set; } = new("chatai.card.untitled");

    [RecursiveMember]
    public partial bool Expanded { get; set; }

    public virtual void RefreshHeader()
        => Header = FirstLineHeader(Text);

    protected static UIPhrase FirstLineHeader(string text)
    {
        foreach (ReadOnlySpan<char> line in text.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> trimmed = line.Trim();

            if (!trimmed.IsEmpty)
                return UIPhrase.Text(trimmed.Length <= MaxPreviewLength ? trimmed.ToString() : $"{trimmed[..MaxPreviewLength].TrimEnd()}…");
        }

        return new UIPhrase("chatai.card.untitled");
    }
}

/// <summary>One of a character's first messages: the variant a new chat opens with.</summary>
internal sealed partial class GreetingItem : CardFoldItem;

/// <summary>One article of a world in its editor dialog: its title, its words, any triggers that wake it, where they go and whether they go.</summary>
internal sealed partial class ArticleItem : CardFoldItem
{
    [RecursiveMember]
    public partial string Title { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string Triggers { get; set; } = string.Empty;

    /// <summary>A <see cref="LorePosition"/>'s name.</summary>
    [RecursiveMember]
    public partial string Position { get; set; } = nameof(LorePosition.AfterStory);

    [RecursiveMember]
    public partial bool Enabled { get; set; } = true;

    [RecursiveMember]
    public partial UIPhrase? Summary { get; set; }

    [RecursiveMember]
    public partial string SearchText { get; set; } = string.Empty;

    public override void RefreshHeader()
    {
        Header = !string.IsNullOrWhiteSpace(Title) ? UIPhrase.Text(Title.Trim()) : FirstLineHeader(Text);
        Summary = BuildSummary();
        SearchText = $"{Title}\n{Triggers}\n{Text}";
    }

    private UIPhrase BuildSummary()
    {
        var triggers = Triggers.Trim();
        UIPhrase state = !Enabled
            ? new UIPhrase("chatai.stats.off")
            : triggers.Length == 0
                ? new UIPhrase("chatai.world.article.triggers.placeholder")
                : UIPhrase.Text(triggers);
        UIPhrase position = new(Position switch
        {
            nameof(LorePosition.BeforeStory) => "chatai.world.position.before",
            nameof(LorePosition.InHistory) => "chatai.world.position.history",
            _ => "chatai.world.position.after"
        });
        var tokens = ApproximateTokenCounter.Estimate(Text.TrimEnd('\r', '\n'));

        return UIPhrase.Of("chatai.world.article.summary", ("state", state), ("position", position), ("tokens", tokens));
    }
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
    public const string GalleryTagDialogKey = "chatai-gallery-tag";

    public const string GalleryReplaceAction = "replace";
    public const string GalleryTagAction = "tag";
    public const string GalleryCopyAction = "copy";
    public const string GalleryDeleteAction = "delete";

    /// <summary>The largest picture taken; the crop writes a square of <see cref="AvatarSize"/>, far under it.</summary>
    public const long MaxAvatarBytes = 8 * 1024 * 1024;

    /// <summary>The side of the square an avatar is cropped to.</summary>
    public const int AvatarSize = 512;

    /// <summary>The world field's entry for none: an id no document can have.</summary>
    public const string NoWorld = "none";

    /// <summary>The character persona field's entry for inheriting <see cref="AppSettings.Persona"/>.</summary>
    public const string InheritedPersona = "default";

    private string? _creatingCardList;
    private TagScope _editingCardScope;
    private string? _editingCardId;
    private string? _editingCardPersona;
    private string? _editingCardWorld;
    private bool _taggingGlobalGallery;
    private string? _taggingImageId;

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

    /// <summary>The persona new chats with the character start as, by id; <see cref="InheritedPersona"/> for the default persona.</summary>
    [RecursiveMember]
    public partial string CardPersonaId { get; set; } = InheritedPersona;

    /// <summary>Default persona entry, then every persona by name.</summary>
    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> CardPersonaOptions { get; } = [];

    /// <summary>The world the card is played in, by id; <see cref="NoWorld"/> for none.</summary>
    [RecursiveMember]
    public partial string CardWorldId { get; set; } = NoWorld;

    /// <summary>None, then every world by name.</summary>
    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> CardWorldOptions { get; } = [];

    [RecursiveMember]
    public partial UIPhrase? CardTokens { get; set; }

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

    /// <summary>The character's author's note, added to the global one and the chat's.</summary>
    [RecursiveMember]
    public partial string CharacterAuthorsNote { get; set; } = string.Empty;

    /// <summary>The picture behind the character's chats; none, and they take the application's.</summary>
    [RecursiveMember]
    public partial PictureSlot CharacterBackground { get; set; } = new();

    [RecursiveMember]
    public partial string? CharacterGallerySelectionId { get; set; }

    [RecursiveMember]
    public partial string GalleryTagInput { get; set; } = string.Empty;

    [RecursiveMember(false)]
    public RecursiveCollection<GalleryItem> CharacterGallery { get; } = [];

    /// <summary>Whether new chats are started as this persona; one persona at a time is.</summary>
    [RecursiveMember]
    public partial bool PersonaIsDefault { get; set; }

    [RecursiveMember]
    public partial decimal WorldScanDepth { get; set; } = World.DefaultScanDepth;

    [RecursiveMember]
    public partial UIVisibility WorldArticleSearchVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember(false)]
    public RecursiveCollection<ArticleItem> WorldArticles { get; } = [];

    /// <summary>Opens the name dialog before creating a new character, persona or world.</summary>
    [UICommand]
    public UICommandResult AskCreateCard()
        => AskCreateCardOf(PanelList);

    protected UICommandResult AskCreateCardOf(string list)
    {
        _creatingCardList = list;
        NewCardName = string.Empty;
        CreateCardTitle = new UIPhrase(list switch
        {
            PersonasList => "chatai.persona.new-name",
            WorldsList => "chatai.world.new-name",
            _ => "chatai.character.new-name"
        });

        return UICommandResult.Ok([new OpenDialogEffect(CreateCardDialogKey)]);
    }

    [UICommand]
    public async Task<UICommandResult> CreateCardAsync(CancellationToken cancellationToken)
    {
        var list = _creatingCardList ?? PanelList;
        var typed = NewCardName.Trim();
        _creatingCardList = null;

        TagScope scope = PanelScopeOf(list);
        string cardId;

        if (scope == TagScope.World)
        {
            var name = typed.Length > 0 ? typed : Context.Translate("chatai.world.new-name") ?? string.Empty;
            cardId = await Data.Worlds.CreateAsync(new World { Name = name }, cancellationToken).ConfigureAwait(false);
        }
        else if (scope == TagScope.Persona)
        {
            var name = typed.Length > 0 ? typed : Context.Translate("chatai.persona.new-name") ?? string.Empty;
            cardId = await Data.Personas.CreateAsync(new Persona { Name = name }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var name = typed.Length > 0 ? typed : Context.Translate("chatai.character.new-name") ?? string.Empty;
            cardId = await Data.Characters.CreateAsync(new Character { Name = name }, cancellationToken).ConfigureAwait(false);
        }

        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);

        if (await OpenCardEditorAsync(scope, cardId, cancellationToken).ConfigureAwait(false) is not string dialogKey)
            return UICommandResult.Ok([new CloseDialogEffect(CreateCardDialogKey)]);

        return UICommandResult.Ok([new CloseDialogEffect(CreateCardDialogKey), new OpenDialogEffect(dialogKey)]);
    }

    protected static TagScope PanelScopeOf(string list)
        => list switch
        {
            PersonasList => TagScope.Persona,
            WorldsList => TagScope.World,
            _ => TagScope.Character
        };

    /// <summary>Lets the open page refresh its state when a card or its pictures change.</summary>
    protected virtual Task OnCardChangedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>Loads <paramref name="cardId"/> into the shell's card-editor state and returns its dialog key, or <c>null</c> when absent.</summary>
    protected async Task<string?> OpenCardEditorAsync(TagScope scope, string cardId, CancellationToken cancellationToken)
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
                _editingCardPersona = character.Persona;
                ShowCharacterCard(cardId, character);
                break;
        }

        _editingCardScope = scope;
        _editingCardId = cardId;
        if (scope != TagScope.Character)
            _editingCardPersona = null;
        _editingCardWorld = baseCard.World;
        CardName = baseCard.Name;
        CardAvatar.Show(avatar);
        CardTags = baseCard.Tags;
        CardDescription = baseCard.Description.TrimEnd('\r', '\n');
        ShowCardWorlds(baseCard.World);
        RefreshCardTokens();
        await ShowTagsAsync(cancellationToken).ConfigureAwait(false);

        return DialogKeyOf(scope);
    }

    private void ShowWorldCard(World card)
    {
        WorldScanDepth = Math.Clamp(card.ScanDepth, 1, 64);
        WorldArticles.Clear();

        foreach (WorldArticle article in card.Articles)
        {
            ArticleItem item = new()
            {
                Title = article.Title,
                Text = article.Text.TrimEnd('\r', '\n'),
                Triggers = string.Join(", ", article.Triggers),
                Position = article.Position.ToString(),
                Enabled = article.Enabled
            };
            item.RefreshHeader();
            WorldArticles.Add(item);
        }

        RefreshWorldArticles();
    }

    private void RefreshWorldArticles()
        => WorldArticleSearchVisibility = WorldArticles.Count > 1 ? UIVisibility.Visible : UIVisibility.Collapsed;

    private void ShowCharacterCard(string cardId, Character card)
    {
        CharacterPersonality = card.Personality.TrimEnd('\r', '\n');
        CharacterScenario = card.Scenario.TrimEnd('\r', '\n');
        CharacterGreetings.Clear();

        foreach (var message in card.FirstMessages)
        {
            GreetingItem item = new()
            {
                Text = message.TrimEnd('\r', '\n')
            };
            item.RefreshHeader();
            CharacterGreetings.Add(item);
        }

        CharacterExampleMessages = card.ExampleMessages.TrimEnd('\r', '\n');
        CharacterSystemPrompt = card.SystemPrompt?.TrimEnd('\r', '\n') ?? string.Empty;
        CharacterPostHistoryInstructions = card.PostHistoryInstructions?.TrimEnd('\r', '\n') ?? string.Empty;
        CharacterAuthorsNote = card.AuthorsNote?.TrimEnd('\r', '\n') ?? string.Empty;
        CharacterBackground.Show(Pictures.CharacterBackgroundOf(cardId));
        ShowCardPersonas(card.Persona);
        ShowCharacterGallery(cardId, card.ImageTags);
    }

    private void ShowCardPersonas(string? persona)
    {
        CardPersonaOptions.Clear();
        CardPersonaOptions.Add(new OptionItem { Id = InheritedPersona, Title = "chatai.character.persona.default" });

        foreach (StoredDocument<Persona> stored in KnownPersonas.OrderBy(static stored => stored.Document.Name, StringComparer.CurrentCultureIgnoreCase))
            CardPersonaOptions.Add(new OptionItem { Id = stored.Id, Title = stored.Document.Name, IsContent = true });

        CardPersonaId = persona is not null && KnownPersonas.Any(stored => stored.Id == persona) ? persona : InheritedPersona;
    }

    private void ShowCharacterGallery(string cardId, IReadOnlyDictionary<string, string>? imageTags)
        => SyncGallery(CharacterGallery, GalleryPictures.ListCharacter(Data, cardId, imageTags), imageId => Pictures.CharacterGalleryImageOf(cardId, imageId));

    protected static void SyncGallery(RecursiveCollection<GalleryItem> target, IReadOnlyList<GalleryImage> images, Func<string, string?> previewOf)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(previewOf);

        HashSet<string> kept = [with(StringComparer.Ordinal)];

        for (var i = 0; i < images.Count; i++)
            _ = kept.Add(images[i].Id);

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(target[i].Id))
                _ = target.Remove(target[i].Id);
        }

        for (var i = 0; i < images.Count; i++)
        {
            GalleryImage image = images[i];
            var preview = previewOf(image.Id);
            var tooltip = !string.IsNullOrEmpty(image.Tag) ? $"{image.Tag} · {image.Key}" : image.Key;

            if (target.TryGet(image.Id, out GalleryItem? existing))
            {
                existing.CopyText = image.CopyKey;
                existing.Tooltip = tooltip;
                existing.Tag = image.Tag;
                existing.Preview = preview;

                if (!string.Equals(target[i].Id, image.Id, StringComparison.Ordinal))
                    _ = target.Move(image.Id, i);
            }
            else
            {
                target.Insert(i, GalleryItem.From(image, preview));
            }
        }
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

    private void RefreshCardTokens()
    {
        var tokens = _editingCardScope switch
        {
            TagScope.Persona => ApproximateTokenCounter.Estimate(CardDescription),
            TagScope.World => ApproximateTokenCounter.Estimate(CardDescription)
                + WorldArticles
                    .Where(static article => article.Enabled)
                    .Sum(static article => ApproximateTokenCounter.Estimate(article.Text)),
            _ => ApproximateTokenCounter.Estimate(CardDescription)
                + ApproximateTokenCounter.Estimate(CharacterPersonality)
                + ApproximateTokenCounter.Estimate(CharacterScenario)
                + ApproximateTokenCounter.Estimate(CharacterExampleMessages)
                + ApproximateTokenCounter.Estimate(CharacterSystemPrompt)
                + ApproximateTokenCounter.Estimate(CharacterPostHistoryInstructions)
                + ApproximateTokenCounter.Estimate(CharacterAuthorsNote)
                + (CharacterGreetings.Count > 0 ? ApproximateTokenCounter.Estimate(CharacterGreetings[0].Text) : 0)
        };

        CardTokens = UIPhrase.Of("chatai.card.tokens", ("tokens", tokens));
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

    [UICommand]
    public async Task<UICommandResult> EditPanelCardAsync(string cardId, CancellationToken cancellationToken)
        => await OpenCardEditorAsync(PanelScopeOf(PanelList), cardId, cancellationToken).ConfigureAwait(false) is string dialogKey
            ? UICommandResult.Ok([new OpenDialogEffect(dialogKey)])
            : UICommandResult.Ok();

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

                var personaName = ResolveEditableCardName(name, persona.Name, allowRename: !IsGenerating || cardId != ActivePersonaId);
                var newPersonaId = await DocumentRenames.SavePersonaAsync(Data, cardId, persona with { Name = personaName, Description = description, World = _editingCardWorld }, cancellationToken).ConfigureAwait(false);

                if (!string.Equals(newPersonaId, cardId, StringComparison.Ordinal))
                {
                    _editingCardId = newPersonaId;
                    CardAvatar.Show(Pictures.PersonaAvatarOf(newPersonaId));
                }

                break;

            case TagScope.World:
                if (await Data.Worlds.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is not World world)
                    return;

                var newWorldId = await DocumentRenames.SaveWorldAsync(Data, cardId, ApplyWorldFields(world with { Name = name.Length == 0 ? world.Name : name, Description = description }), cancellationToken).ConfigureAwait(false);

                if (!string.Equals(newWorldId, cardId, StringComparison.Ordinal))
                {
                    _editingCardId = newWorldId;
                    CardAvatar.Show(Pictures.WorldAvatarOf(newWorldId));
                }

                RefreshWorldArticles();
                break;

            case TagScope.Character:
            default:
                if (await Data.Characters.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is not Character character)
                    return;

                _editingCardPersona = CardPersonaId == InheritedPersona ? null : CardPersonaId;
                var characterName = ResolveEditableCardName(name, character.Name, allowRename: !IsGenerating || cardId != OpenCharacterId);
                Character updatedCharacter = ApplyCharacterFields(character with { Name = characterName, Description = description, Persona = _editingCardPersona, World = _editingCardWorld });
                var newCharacterId = await DocumentRenames.SaveCharacterAsync(Data, cardId, updatedCharacter, cancellationToken).ConfigureAwait(false);

                if (!string.Equals(newCharacterId, cardId, StringComparison.Ordinal))
                {
                    _editingCardId = newCharacterId;

                    if (string.Equals(_panelCharacterId, cardId, StringComparison.Ordinal))
                        _panelCharacterId = newCharacterId;

                    CardAvatar.Show(Pictures.CharacterAvatarOf(newCharacterId));
                    CharacterBackground.Show(Pictures.CharacterBackgroundOf(newCharacterId));
                    ShowCharacterGallery(newCharacterId, updatedCharacter.ImageTags);
                }

                break;
        }

        RefreshCardTokens();
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    private string ResolveEditableCardName(string requested, string current, bool allowRename)
    {
        if (requested.Length == 0)
            return current;

        if (!allowRename && !string.Equals(Slug.Of(requested), Slug.Of(current), StringComparison.Ordinal))
        {
            CardName = current;
            return current;
        }

        return requested;
    }

    private World ApplyWorldFields(World card)
    {
        foreach (ArticleItem article in WorldArticles)
            article.RefreshHeader();

        return card with
        {
            ScanDepth = Math.Clamp((int)WorldScanDepth, 1, 64),
            Articles =
            [
                .. WorldArticles
                    .Where(static article => !string.IsNullOrWhiteSpace(article.Title) || !string.IsNullOrWhiteSpace(article.Text))
                    .Select(static article => new WorldArticle
                    {
                        Title = article.Title.Trim(),
                        Text = article.Text.TrimEnd('\r', '\n'),
                        Triggers = [.. article.Triggers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)],
                        Position = Enum.TryParse(article.Position, out LorePosition position) ? position : LorePosition.AfterStory,
                        Enabled = article.Enabled
                    })
            ]
        };
    }

    private Character ApplyCharacterFields(Character card)
    {
        foreach (GreetingItem greeting in CharacterGreetings)
            greeting.RefreshHeader();

        return card with
        {
            Personality = CharacterPersonality.TrimEnd('\r', '\n'),
            Scenario = CharacterScenario.TrimEnd('\r', '\n'),
            FirstMessages =
            [
                .. CharacterGreetings
                    .Where(static greeting => !string.IsNullOrWhiteSpace(greeting.Text))
                    .Select(static greeting => greeting.Text.TrimEnd('\r', '\n'))
            ],
            ExampleMessages = CharacterExampleMessages.TrimEnd('\r', '\n'),
            SystemPrompt = string.IsNullOrWhiteSpace(CharacterSystemPrompt) ? null : CharacterSystemPrompt.TrimEnd('\r', '\n'),
            PostHistoryInstructions = string.IsNullOrWhiteSpace(CharacterPostHistoryInstructions) ? null : CharacterPostHistoryInstructions.TrimEnd('\r', '\n'),
            AuthorsNote = string.IsNullOrWhiteSpace(CharacterAuthorsNote) ? null : CharacterAuthorsNote.TrimEnd('\r', '\n')
        };
    }

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

    /// <summary>Adds every uploaded picture to the open character's gallery under collision-free slugs.</summary>
    [UICommand]
    public async Task UploadCharacterGalleryAsync(CancellationToken cancellationToken)
    {
        var selectionId = CharacterGallerySelectionId;
        CharacterGallerySelectionId = null;

        if (_editingCardId is not string id || _editingCardScope != TagScope.Character)
            return;

        List<(string FileName, byte[] Content, string? ContentType)> uploaded = await ReadUploadedPicturesAsync(selectionId, cancellationToken).ConfigureAwait(false);

        if (uploaded.Count == 0)
            return;

        for (var i = 0; i < uploaded.Count; i++)
            _ = await GalleryPictures.WriteCharacterAsync(Data, id, uploaded[i].FileName, uploaded[i].Content, uploaded[i].ContentType, cancellationToken).ConfigureAwait(false);

        await RefreshCharacterGalleryAsync(id, cancellationToken).ConfigureAwait(false);
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshCharacterGalleryAsync(string cardId, CancellationToken cancellationToken)
    {
        Character? character = await Data.Characters.ReadAsync(cardId, cancellationToken).ConfigureAwait(false);
        ShowCharacterGallery(cardId, character?.ImageTags);
    }

    /// <summary>Replaces the picture file of <paramref name="imageId"/> in the open character's gallery while preserving its id and assigned tag.</summary>
    [UICommand]
    public async Task ReplaceCharacterGalleryAsync(string imageId, CancellationToken cancellationToken)
    {
        if (_editingCardId is not string id || _editingCardScope != TagScope.Character || !CharacterGallery.TryGet(imageId, out GalleryItem? item))
            return;

        List<(string FileName, byte[] Content, string? ContentType)> uploaded = await ReadUploadedPicturesAsync(item.TakeReplaceUpload(), cancellationToken).ConfigureAwait(false);

        if (uploaded.Count == 0)
            return;

        if (await GalleryPictures.ReplaceCharacterAsync(Data, id, imageId, uploaded[0].Content, uploaded[0].ContentType, cancellationToken).ConfigureAwait(false) is not null)
        {
            item.Preview = Pictures.CharacterGalleryImageOf(id, imageId);
            await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Dispatches context menu actions on a tile in the open character's gallery.</summary>
    [UICommand]
    public async Task<UICommandResult> CharacterGalleryActionAsync(string action, string imageId, CancellationToken cancellationToken)
    {
        if (_editingCardId is not string id || _editingCardScope != TagScope.Character)
            return UICommandResult.Ok();

        if (action == GalleryTagAction && CharacterGallery.TryGet(imageId, out GalleryItem? item))
            return OpenGalleryTagDialog(isGlobal: false, imageId, item.Tag);

        if (action == GalleryDeleteAction)
        {
            await GalleryPictures.DeleteCharacterAsync(Data, id, imageId, cancellationToken).ConfigureAwait(false);
            _ = CharacterGallery.Remove(imageId);
            await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
        }

        return UICommandResult.Ok();
    }

    protected UICommandResult OpenGalleryTagDialog(bool isGlobal, string imageId, string? currentTag)
    {
        _taggingGlobalGallery = isGlobal;
        _taggingImageId = imageId;
        GalleryTagInput = currentTag ?? string.Empty;

        return UICommandResult.Ok([new OpenDialogEffect(GalleryTagDialogKey)]);
    }

    /// <summary>Saves or clears the alias tag edited in the gallery tag dialog, validating uniqueness across the character and global galleries.</summary>
    [UICommand]
    public async Task<UICommandResult> SaveGalleryTagAsync(CancellationToken cancellationToken)
    {
        if (_taggingImageId is not string imageId)
            return UICommandResult.Ok([new CloseDialogEffect(GalleryTagDialogKey)]);

        if (_taggingGlobalGallery)
        {
            GalleryTagResult globalResult = await GalleryPictures.SetGlobalTagAsync(Data, imageId, GalleryTagInput, cancellationToken).ConfigureAwait(false);

            if (globalResult.Conflict)
                return Notify("chatai.gallery.tag.exists", UIColorStyle.Warning);

            if (globalResult.Invalid)
                return Notify("chatai.gallery.tag.invalid", UIColorStyle.Warning);

            _taggingImageId = null;
            await OnGlobalGalleryChangedAsync(cancellationToken).ConfigureAwait(false);
            await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);

            return UICommandResult.Ok([new CloseDialogEffect(GalleryTagDialogKey)]);
        }

        if (_editingCardId is not string cardId || _editingCardScope != TagScope.Character)
            return UICommandResult.Ok([new CloseDialogEffect(GalleryTagDialogKey)]);

        GalleryTagResult charResult = await GalleryPictures.SetCharacterTagAsync(Data, cardId, imageId, GalleryTagInput, cancellationToken).ConfigureAwait(false);

        if (charResult.Conflict)
            return Notify("chatai.gallery.tag.exists", UIColorStyle.Warning);

        if (charResult.Invalid)
            return Notify("chatai.gallery.tag.invalid", UIColorStyle.Warning);

        _taggingImageId = null;
        await RefreshCharacterGalleryAsync(cardId, cancellationToken).ConfigureAwait(false);
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok([new CloseDialogEffect(GalleryTagDialogKey)]);
    }

    protected virtual Task OnGlobalGalleryChangedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    [UICommand]
    public void AddGreeting()
    {
        GreetingItem item = new();
        CharacterGreetings.Add(item);
        ExpandOnly(CharacterGreetings, item.Id);
    }

    private static void ExpandOnly<TItem>(RecursiveCollection<TItem> items, string? id)
        where TItem : CardFoldItem
    {
        foreach (TItem item in items)
            item.Expanded = item.Id == id;
    }

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

    /// <summary>A new article to write, opened on its own; it is kept once it has a title or words.</summary>
    [UICommand]
    public void AddArticle()
    {
        ArticleItem item = new();
        item.RefreshHeader();
        WorldArticles.Add(item);
        RefreshWorldArticles();
        ExpandOnly(WorldArticles, item.Id);
    }

    [UICommand]
    public async Task RemoveArticleAsync(string articleId, CancellationToken cancellationToken)
    {
        if (!WorldArticles.Remove(articleId))
            return;

        RefreshWorldArticles();
        await SaveCardAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Duplicates the card currently open in the editor dialog, including its pictures, and opens the copy.</summary>
    [UICommand]
    public async Task<UICommandResult> DuplicateEditingCardAsync(CancellationToken cancellationToken)
    {
        if (_editingCardId is not string sourceId || !Slug.IsValidId(sourceId))
            return UICommandResult.Ok();

        await SaveCardAsync(cancellationToken).ConfigureAwait(false);

        var copyFormat = Context.Translate("chatai.settings.copy-name") ?? "{name} (copy)";
        string copyId;

        switch (_editingCardScope)
        {
            case TagScope.Persona:
                if (await Data.Personas.ReadAsync(sourceId, cancellationToken).ConfigureAwait(false) is not Persona persona)
                    return UICommandResult.Ok();

                copyId = await Data.Personas.CreateAsync(persona with { Name = copyFormat.Replace("{name}", persona.Name, StringComparison.Ordinal) }, cancellationToken).ConfigureAwait(false);
                CardPictures.Copy(Data.Personas, sourceId, copyId);
                break;

            case TagScope.World:
                if (await Data.Worlds.ReadAsync(sourceId, cancellationToken).ConfigureAwait(false) is not World world)
                    return UICommandResult.Ok();

                copyId = await Data.Worlds.CreateAsync(world with { Name = copyFormat.Replace("{name}", world.Name, StringComparison.Ordinal) }, cancellationToken).ConfigureAwait(false);
                CardPictures.Copy(Data.Worlds, sourceId, copyId);
                break;

            case TagScope.Character:
            default:
                if (await Data.Characters.ReadAsync(sourceId, cancellationToken).ConfigureAwait(false) is not Character character)
                    return UICommandResult.Ok();

                copyId = await Data.Characters.CreateAsync(character with { Name = copyFormat.Replace("{name}", character.Name, StringComparison.Ordinal) }, cancellationToken).ConfigureAwait(false);
                CardPictures.Copy(Data.Characters, sourceId, copyId);

                if (_panelCharacterId == sourceId)
                    _panelCharacterId = copyId;

                break;
        }

        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
        await OnCardChangedAsync(cancellationToken).ConfigureAwait(false);
        _ = await OpenCardEditorAsync(_editingCardScope, copyId, cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
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
