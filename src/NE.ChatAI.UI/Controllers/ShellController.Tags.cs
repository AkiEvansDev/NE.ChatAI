using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>One tag row in the tag management dialog.</summary>
internal sealed partial class TagRow : RecursiveObservable, IBindableItem, IContentItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    public bool IsContent => true;

    [RecursiveMember]
    public partial string Name { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIThemeColor? Color { get; set; }
}

/// <summary>Card tag selection and tag catalog dialog commands.</summary>
internal abstract partial class ShellController
{
    public const string TagsDialogKey = "chatai-card-tags";
    public const string DeleteTagDialogKey = "chatai-card-tag-delete";

    private string? _deletingTag;

    [RecursiveMember]
    public partial IReadOnlyList<string> CardTags { get; set; } = [];

    [RecursiveMember(false)]
    public RecursiveCollection<OptionItem> CardTagOptions { get; } = [];

    [RecursiveMember(false)]
    public RecursiveCollection<TagRow> AllTags { get; } = [];

    [RecursiveMember]
    public partial string NewTag { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase? DeleteTagQuestion { get; set; }

    private async Task ShowTagsAsync(CancellationToken cancellationToken)
    {
        NewTag = string.Empty;
        IReadOnlyList<KnownTag> known = await TagCatalog.ListAsync(Data, _editingCardScope, cancellationToken).ConfigureAwait(false);
        Dictionary<string, UIThemeColor> colors = TagBadges.ColorsOf(known);

        RefreshCardTagOptions(known, colors);
        SyncAllTags(known, colors);
    }

    private void RefreshCardTagOptions(IReadOnlyList<KnownTag> known, Dictionary<string, UIThemeColor> colors)
    {
        HashSet<string> kept = [with(StringComparer.Ordinal)];

        for (var i = 0; i < known.Count; i++)
            _ = kept.Add(known[i].Name);

        for (var i = CardTagOptions.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(CardTagOptions[i].Id))
                _ = CardTagOptions.Remove(CardTagOptions[i].Id);
        }

        for (var i = 0; i < known.Count; i++)
        {
            var name = known[i].Name;

            if (CardTagOptions.TryGet(name, out OptionItem? existing))
            {
                existing.BadgeTitle = name;
                existing.BadgeColor = colors.TryGetValue(name, out UIThemeColor color) ? color : null;

                if (!string.Equals(CardTagOptions[i].Id, name, StringComparison.Ordinal))
                    _ = CardTagOptions.Move(name, i);
            }
            else
            {
                CardTagOptions.Insert(i, TagBadges.OptionOf(name, colors));
            }
        }
    }

    private void SyncAllTags(IReadOnlyList<KnownTag> known, Dictionary<string, UIThemeColor> colors)
    {
        HashSet<string> kept = [with(StringComparer.Ordinal)];

        for (var i = 0; i < known.Count; i++)
            _ = kept.Add(known[i].Name);

        for (var i = AllTags.Count - 1; i >= 0; i--)
        {
            if (!kept.Contains(AllTags[i].Id))
                _ = AllTags.Remove(AllTags[i].Id);
        }

        for (var i = 0; i < known.Count; i++)
        {
            var name = known[i].Name;
            UIThemeColor? badgeColor = colors.TryGetValue(name, out UIThemeColor color) ? color : null;

            if (AllTags.TryGet(name, out TagRow? existing))
            {
                existing.Name = name;
                existing.Color = badgeColor;

                if (!string.Equals(AllTags[i].Id, name, StringComparison.Ordinal))
                    _ = AllTags.Move(name, i);
            }
            else
            {
                AllTags.Insert(i, new TagRow { Id = name, Name = name, Color = badgeColor });
            }
        }
    }

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
        RefreshCardTagOptions(known, TagBadges.ColorsOf(known));
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
