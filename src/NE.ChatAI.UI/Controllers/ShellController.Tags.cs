using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>A tag in the tags dialog: its name as it is kept, its name as edited, and the colour its badge is drawn in everywhere.</summary>
internal sealed partial class TagRow : RecursiveObservable, IBindableItem, IContentItem
{
    /// <summary>The tag's name as it is kept, the row's key until a rename keeps the new one.</summary>
    [RecursiveMember(false)]
    public required string Id { get; init; }

    /// <summary>A tag is the reader's own word, never a key.</summary>
    public bool IsContent => true;

    [RecursiveMember]
    public partial string Name { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIThemeColor? Color { get; set; }
}

/// <summary>
/// A card's tags: picked in the field from every tag known; made, renamed, recoloured and deleted for every card at once in the
/// tags dialog.
/// </summary>
internal abstract partial class ShellController
{
    public const string TagsDialogKey = "chatai-card-tags";
    public const string DeleteTagDialogKey = "chatai-card-tag-delete";

    /// <summary>The tag about to be deleted, asked about first.</summary>
    private string? _deletingTag;

    [RecursiveMember]
    public partial IReadOnlyList<string> CardTags { get; set; } = [];

    /// <summary>Every tag known for this kind of card, to pick from.</summary>
    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> CardTagOptions { get; } = [];

    /// <summary>Every tag known for this kind of card, as the tags dialog edits them.</summary>
    [RecursiveMember(false)]
    public RecursiveCollection<TagRow> AllTags { get; } = [];

    /// <summary>The name of the tag about to be made in the dialog.</summary>
    [RecursiveMember]
    public partial string NewTag { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase? DeleteTagQuestion { get; set; }

    /// <summary>Every tag known for this kind of card to pick from, and the same in the dialog's list.</summary>
    private async Task ShowTagsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<KnownTag> known = await TagCatalog.ListAsync(Data, _editingCardScope, cancellationToken).ConfigureAwait(false);
        Dictionary<string, UIThemeColor> colors = TagColorsOf(known);

        RefreshCardTagOptions(known, colors);
        AllTags.Clear();

        foreach (KnownTag tag in known)
            AllTags.Add(new TagRow { Id = tag.Name, Name = tag.Name, Color = colors.TryGetValue(tag.Name, out UIThemeColor color) ? color : null });
    }

    private static Dictionary<string, UIThemeColor> TagColorsOf(IReadOnlyList<KnownTag> known)
    {
        Dictionary<string, UIThemeColor> colors = new(StringComparer.OrdinalIgnoreCase);

        foreach (KnownTag tag in known)
        {
            if (UIThemeColor.TryParse(tag.Color, out UIThemeColor color) && TagBadges.HasColor(color))
                colors[tag.Name] = color;
        }

        return colors;
    }

    private void RefreshCardTagOptions(IReadOnlyList<KnownTag> known, Dictionary<string, UIThemeColor> colors)
    {
        CardTagOptions.Clear();

        foreach (KnownTag tag in known)
            CardTagOptions.Add(TagBadges.OptionOf(tag.Name, colors));
    }

    /// <summary>Keeps the tags picked, each once whatever its case.</summary>
    [UICommand]
    public async Task SaveTagsAsync(CancellationToken cancellationToken)
    {
        if (_editingCardId is not string cardId || !Slug.IsValidId(cardId))
            return;

        CardTags = [.. CardTags.Distinct(StringComparer.OrdinalIgnoreCase)];

        switch (_editingCardScope)
        {
            case TagScope.Persona:
                if (await Data.Personas.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is Persona persona)
                    await Data.Personas.WriteAsync(cardId, persona with { Tags = CardTags }, cancellationToken).ConfigureAwait(false);

                break;

            case TagScope.World:
                if (await Data.Worlds.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is World world)
                    await Data.Worlds.WriteAsync(cardId, world with { Tags = CardTags }, cancellationToken).ConfigureAwait(false);

                break;

            case TagScope.Character:
            default:
                if (await Data.Characters.ReadAsync(cardId, cancellationToken).ConfigureAwait(false) is Character character)
                    await Data.Characters.WriteAsync(cardId, character with { Tags = CardTags }, cancellationToken).ConfigureAwait(false);

                break;
        }

        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
    }

    [UICommand]
    public async Task<UICommandResult> OpenTagsAsync(CancellationToken cancellationToken)
    {
        NewTag = string.Empty;
        await ShowTagsAsync(cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok([new OpenDialogEffect(TagsDialogKey)]);
    }

    /// <summary>Makes the tag named in the dialog and gives it to the card.</summary>
    [UICommand]
    public async Task<UICommandResult> AddTagAsync(CancellationToken cancellationToken)
    {
        var name = NewTag.Trim();

        if (name.Length == 0)
            return UICommandResult.Ok();

        if (!await TagCatalog.AddAsync(Data, _editingCardScope, name, cancellationToken).ConfigureAwait(false))
            return Notify("chatai.tags.exists", UIColorStyle.Info);

        NewTag = string.Empty;
        CardTags = [.. CardTags, name];
        await SaveTagsAsync(cancellationToken).ConfigureAwait(false);
        await ShowTagsAsync(cancellationToken).ConfigureAwait(false);

        return UICommandResult.Ok();
    }

    /// <summary>Renames a tag on every card of this kind that carries it; an empty name puts the old one back.</summary>
    [UICommand]
    public async Task RenameTagAsync(string tag, CancellationToken cancellationToken)
    {
        if (!AllTags.TryGet(tag, out TagRow? row))
            return;

        if (await TagCatalog.RenameAsync(Data, _editingCardScope, row.Id, row.Name, cancellationToken).ConfigureAwait(false))
            await ShowRetaggedAsync(cancellationToken).ConfigureAwait(false);
        else
            row.Name = row.Id;
    }

    /// <summary>The card's tags read again after a rename or a delete touched every card of this kind, then the lists.</summary>
    private async Task ShowRetaggedAsync(CancellationToken cancellationToken)
    {
        if (_editingCardId is string cardId && Slug.IsValidId(cardId))
        {
            Card? card = _editingCardScope switch
            {
                TagScope.Persona => await Data.Personas.ReadAsync(cardId, cancellationToken).ConfigureAwait(false),
                TagScope.World => await Data.Worlds.ReadAsync(cardId, cancellationToken).ConfigureAwait(false),
                _ => await Data.Characters.ReadAsync(cardId, cancellationToken).ConfigureAwait(false)
            };

            if (card is not null)
                CardTags = card.Tags;
        }

        await ShowTagsAsync(cancellationToken).ConfigureAwait(false);
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps the colour picked for a tag, for every card of this kind that carries it; zero opacity clears it back to none.</summary>
    [UICommand]
    public async Task SaveTagColorAsync(string tag, CancellationToken cancellationToken)
    {
        if (!AllTags.TryGet(tag, out TagRow? row))
            return;

        var hasColor = TagBadges.HasColor(row.Color);
        var canonical = hasColor ? row.Color!.Value.ToCanonical() : null;

        if (!hasColor && row.Color is not null)
            row.Color = null;

        await TagCatalog.SetColorAsync(Data, _editingCardScope, row.Id, canonical, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<KnownTag> known = await TagCatalog.ListAsync(Data, _editingCardScope, cancellationToken).ConfigureAwait(false);
        RefreshCardTagOptions(known, TagColorsOf(known));
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
    }

    [UICommand]
    public UICommandResult AskDeleteTag(string tag)
    {
        _deletingTag = tag;
        var questionKey = _editingCardScope switch
        {
            TagScope.Persona => "chatai.tags.delete.persona.question",
            TagScope.World => "chatai.tags.delete.world.question",
            _ => "chatai.tags.delete.question"
        };
        DeleteTagQuestion = UIPhrase.Of(questionKey, ("name", tag));

        return UICommandResult.Ok([new OpenDialogEffect(DeleteTagDialogKey)]);
    }

    /// <summary>Takes the tag off every card of this kind that carries it and forgets its colour.</summary>
    [UICommand]
    public async Task<UICommandResult> DeleteTagAsync(CancellationToken cancellationToken)
    {
        if (_deletingTag is string tag)
        {
            _deletingTag = null;
            await TagCatalog.DeleteAsync(Data, _editingCardScope, tag, cancellationToken).ConfigureAwait(false);
            await ShowRetaggedAsync(cancellationToken).ConfigureAwait(false);
        }

        return UICommandResult.Ok([new CloseDialogEffect(DeleteTagDialogKey)]);
    }
}
