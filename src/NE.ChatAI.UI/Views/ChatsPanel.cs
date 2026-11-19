using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>
/// The chats panel's body: the characters, the personas or the worlds, switched between at its top — a search over their names with a new one
/// beside it, their tags to narrow by, each row its picture, its name, its tags as badges and its actions — or, a character picked, that
/// character with its chats, a new chat and its editor dialog beside its name.
/// </summary>
internal static class ChatsPanel
{
    /// <summary>Matches a card's name plus one row of tag badges (24 + 4 + 16), so rows with and without tags keep the same height.</summary>
    private const double AvatarSize = 44;
    private const double HeaderAvatarSize = 34;
    public const string SearchId = "chatai-card-search";
    public const string TagFilterId = "chatai-tag-filter";
    private const string ListSwitchId = "chatai-list-switch";

    /// <summary>The panel's heading: the card list switch, or the picked character's back button, avatar and name.</summary>
    public static StackPanelComponent CreateHeading()
        => new StackPanelComponent()
            .SetOrientation(UIOrientation.Vertical)
            .SetSpacing(0)
            .SetMinHeight(UILayoutLength.Absolute(HeaderAvatarSize))
            .SetGlyphEdge(UIAlignment.Start)
            .AddChild(CreateListSwitch())
            .AddChild(CreateCharacterTitle());

    private static ButtonGroupComponent CreateListSwitch()
        => new ButtonGroupComponent(ListSwitchId)
            .SetSize(UISize.Small)
            .SetItems([
                new ButtonItem { Id = ShellController.CharactersList, Title = "chatai.chats.characters" },
                new ButtonItem { Id = ShellController.PersonasList, Title = "chatai.chats.personas" },
                new ButtonItem { Id = ShellController.WorldsList, Title = "chatai.chats.worlds" }
            ])
            .BindSelectedKey<ShellController>(static x => x.PanelList, mode: UIBindingMode.OneWay)
            .OnItemClick(nameof(ShellController.SwitchList), UIAction.ArgCurrentItemKey("list"))
            .SetHorizontalAlignment(UIAlignment.Start)
            .BindVisibility<ShellController>(static x => x.ChatsPanes.ListVisibility);

    private static StackPanelComponent CreateCharacterTitle()
        => new StackPanelComponent()
            .SetOrientation(UIOrientation.Horizontal)
            .SetMinHeight(UILayoutLength.Absolute(HeaderAvatarSize))
            .SetGlyphEdge(UIAlignment.Start)
            .SetSpacing(8)
            .AddChild(UIIconButtons.Back().SetSize(UISize.Small).SetVerticalAlignment(UIAlignment.Center).OnClick(nameof(ShellController.ShowCharacterList)))
            .AddChild(Avatar<ShellController>(static x => x.PanelCharacter.Avatar, UIBindingScope.Root, HeaderAvatarSize))
            .AddChild(new TextComponent()
                .BindTitle<ShellController>(static x => x.PanelCharacter.Name)
                .BindTooltip<ShellController>(static x => x.PanelCharacter.Name)
                .SetTooltipPlain(true)
                .AsContent(TextComponent.TitleProperty, ITooltipComponent.TooltipProperty)
                .AsSubtitle()
                .SetVerticalAlignment(UIAlignment.Center)
            )
            .BindVisibility<ShellController>(static x => x.ChatsPanes.DetailVisibility);

    /// <summary>A card's picture in a circle, the person's glyph for one with none.</summary>
    private static ImageComponent Avatar<TSource>(Expression<Func<TSource, object?>> source, UIBindingScope scope, double size = AvatarSize)
        => new ImageComponent()
            .BindSource(source, scope)
            .SetAltText("chatai.chat.avatar")
            .SetLazy(false)
            .SetShape(UIShape.Circle)
            .SetWidth(UILayoutLength.Absolute(size))
            .SetVerticalAlignment(UIAlignment.Center);

    /// <summary>The search field and embedded tag filter and card creation button, shown over the card lists.</summary>
    public static TextInputComponent CreateSearch()
        => new TextInputComponent(SearchId)
            .SetSearch()
            .SetDebounceMilliseconds(200)
            .AddTrailingAction(CreateTagFilter())
            .AddTrailingAction(UIIconButtons.New().OnClick(nameof(ShellController.AskCreateCard)))
            .BindVisibility<ShellController>(static x => x.ChatsPanes.ListVisibility);

    /// <summary>The tag filter menu inside the search field, showing each tag as its badge and the active count on the button.</summary>
    private static MultiSelectComponent CreateTagFilter()
        => new MultiSelectComponent(TagFilterId)
            .SetVariant(UIMultiSelectVariant.Button)
            .SetIcon(UIStandardAction.Filter.Icon)
            .SetTooltip(UIStandardAction.Filter.Title)
            .SetPopupPlacement(UIPopupPlacement.BottomEnd)
            .ConfigureDefaultEmptyTemplate(empty => empty.SetTitle("chatai.tags.none").SetTitleWrap(true))
            .BindOptions<ShellController>(static x => x.TagFilterOptions)
            .BindValue<ShellController>(static x => x.TagFilter);

    public static ContainerComponent CreateBody()
        => UILayout.Panes<ShellController>(CreateLists(), CreateCharacterChats(), static x => x.ChatsPanes, stacked: true);

    private static StackPanelComponent CreateLists()
        => UILayout.Stack(12,
            CreateList("chatai-character-list", ShellController.CharactersList, static x => x.CharacterRows, static x => x.CharactersEmpty, showEdit: true)
                .OnItemClick(nameof(ShellController.OpenCharacterAsync), UIAction.ArgCurrentItemKey("characterId")),
            CreateList("chatai-persona-list", ShellController.PersonasList, static x => x.PersonaRows, static x => x.PersonasEmpty, showEdit: false)
                .OnItemClick(nameof(ShellController.OpenPersonaAsync), UIAction.ArgCurrentItemKey("personaId")),
            CreateList("chatai-world-list", ShellController.WorldsList, static x => x.WorldRows, static x => x.WorldsEmpty, showEdit: false)
                .OnItemClick(nameof(ShellController.OpenWorldAsync), UIAction.ArgCurrentItemKey("worldId"))
        );

    private static ItemsViewComponent CreateList(string id, string listKey, Expression<Func<ShellController, object?>> rows, Expression<Func<ShellController, object?>> empty, bool showEdit)
        => new ItemsViewComponent(id)
            .SetVerticalScroll(UIScrollMode.Disabled)
            .SetRowHoverable(true)
            .SetRowBleed(true)
            .BindItems(rows)
            .FilterBy(SearchId, IInputComponent.ValueProperty, nameof(CardRow.Name))
            .FilterBy(TagFilterId, MultiSelectComponent.ValueProperty, nameof(CardRow.Tags), UIComparisonOperator.In)
            .SetSpacing(4)
            .ConfigureDefaultEmptyTemplate(template => template.BindTitle(empty).SetTitleWrap(true))
            .SetTemplate(CreateCardRow(showEdit))
            .ShownWhen(ListSwitchId, ButtonGroupComponent.SelectedKeyProperty, UIComparisonOperator.Equal, listKey);

    /// <summary>The card's picture, its name — the default persona or active card marked beside it — under the name its tags, and its row actions at the end.</summary>
    /// <remarks>A plain row, not a <see cref="UISurfaceStyle.Background"/> surface: on the panel's own fill a framed row read worse.</remarks>
    private static ContainerComponent CreateCardRow(bool showEdit)
    {
        StackPanelComponent actions = showEdit
            ? UILayout.Row(4,
                UIIconButtons.Edit().SetSize(UISize.Small).OnClick(nameof(ShellController.EditPanelCardAsync), UIAction.ArgCurrentItemKey("cardId")),
                UIIconButtons.Delete().SetSize(UISize.Small).OnClick(nameof(ShellController.AskDeletePanelCard), UIAction.ArgCurrentItemKey("cardId"))
            )
            : UILayout.Row(4,
                UIIconButtons.Delete().SetSize(UISize.Small).OnClick(nameof(ShellController.AskDeletePanelCard), UIAction.ArgCurrentItemKey("cardId"))
            );

        return new ContainerComponent()
            .SetColumn(1, UIGridUnit.Auto())
            .SetColumn(24, UIGridUnit.Auto())
            .SetPadding(UIThickness.Symmetric(0, 2))
            .AddChild(Avatar<CardRow>(static x => x.Avatar, UIBindingScope.Relative).SetMargin(UIThickness.All(0, 0, 12, 0)).SetPlacement(1, 1, 1, 1))
            .AddChild(UILayout.Stack(4,
                    UILayout.Row(8,
                        new TextComponent().BindTitle<CardRow>(static x => x.Name, UIBindingScope.Relative).AsSubtitle(),
                        new BadgeComponent()
                            .SetTitle("chatai.chats.default")
                            .SetVariant(UIBadgeVariant.Surface)
                            .SetColor(UIThemeColor.Primary)
                            .SetTitleType(UITextAppearance.Caption)
                            .SetVerticalAlignment(UIAlignment.Center)
                            .BindVisibility<CardRow>(static x => x.DefaultVisibility, UIBindingScope.Relative),
                        new BadgeComponent()
                            .SetTitle("chatai.chats.active")
                            .SetVariant(UIBadgeVariant.Surface)
                            .SetColor(UIThemeColor.Success)
                            .SetTitleType(UITextAppearance.Caption)
                            .SetVerticalAlignment(UIAlignment.Center)
                            .BindVisibility<CardRow>(static x => x.ActiveVisibility, UIBindingScope.Relative)
                    ),
                    new ItemsViewComponent()
                        .SetVerticalScroll(UIScrollMode.Disabled)
                        .BindItems<CardRow>(static x => x.Tags, UIBindingScope.Relative)
                        .SetLayoutType(UIItemsLayoutType.Wrap)
                        .SetSpacing(4)
                        .SetShowEmptyTemplate(false)
                        .SetTemplate(CreateTagBadge())
                )
                .SetVerticalAlignment(UIAlignment.Center)
                .SetPlacement(2, 1, 22, 1)
            )
            .AddChild(actions
                .SetVerticalAlignment(UIAlignment.Center)
                .SetPlacement(24, 1, 1, 1)
            );
    }

    /// <summary>A tag in its own colour, in the body's letters rather than a label's capitals.</summary>
    private static BadgeComponent CreateTagBadge()
        => new BadgeComponent()
            .SetVariant(UIBadgeVariant.Surface)
            .SetTitleType(UITextAppearance.Caption)
            .BindTitle<TagBadge>(static x => x.Text, UIBindingScope.Relative)
            .BindColor<TagBadge>(static x => x.Color, UIBindingScope.Relative);

    /// <summary>The character's chats under the fixed panel header, each with its delete button.</summary>
    private static ItemsViewComponent CreateCharacterChats()
        => new ItemsViewComponent("chatai-chat-list")
            .SetVerticalScroll(UIScrollMode.Disabled)
            .SetRowHoverable(true)
            .SetRowBleed(true)
            .SetSelectionMode(UISelectionMode.One)
            .BindSelectedKey<ShellController>(static x => x.OpenChatId, mode: UIBindingMode.OneWay)
            .BindItems<ShellController>(static x => x.ChatEntries)
            .SetSpacing(4)
            .ConfigureDefaultEmptyTemplate(static empty => empty.SetTitle("chatai.chats.none").SetTitleWrap(true))
            .SetTemplate(CreateChatRow())
            .OnItemClick(nameof(ShellController.OpenPanelChatAsync), UIAction.ArgCurrentItemKey("chatId"));

    private static ContainerComponent CreateChatRow()
        => new ContainerComponent()
            .SetColumn(24, UIGridUnit.Auto())
            .SetPadding(UIThickness.Symmetric(0, 2))
            .AddChild(new TextComponent()
                .AsBody()
                .BindTitle<MenuItem>(static x => x.Title, UIBindingScope.Relative)
                .BindDescription<MenuItem>(static x => x.Description, UIBindingScope.Relative)
                .SetVerticalAlignment(UIAlignment.Center)
                .SetPlacement(1, 1, 23, 1)
            )
            .AddChild(UIIconButtons.Delete()
                .SetSize(UISize.Small)
                .OnClick(nameof(ShellController.AskDeleteChat), UIAction.ArgCurrentItemKey("chatId"))
                .SetVerticalAlignment(UIAlignment.Center)
                .SetPlacement(24, 1, 1, 1)
            );

    /// <summary>The character chat actions standing before Pin and Close in the drawer header.</summary>
    public static IVisualComponent[] CreateActions()
        =>
        [
            UIIconButtons.New()
                .SetSize(UISize.Small)
                .OnClick(nameof(ShellController.StartChatAsync))
                .BindVisibility<ShellController>(static x => x.ChatsPanes.DetailVisibility),
            UIIconButtons.Edit()
                .SetSize(UISize.Small)
                .OnClick(nameof(ShellController.EditCharacterAsync))
                .BindVisibility<ShellController>(static x => x.ChatsPanes.DetailVisibility)
        ];

    /// <summary>The card creation and editor dialogs, tag dialogs, and deletion confirmation dialogs hosted on the shell.</summary>
    public static IReadOnlyList<UIDialog> CreateDialogs()
        =>
        [
            UIDialogs.BoundForm<ShellController>(
                ShellController.CreateCardDialogKey,
                static x => x.CreateCardTitle,
                null,
                new TextInputComponent()
                    .SetTitle("chatai.card.name")
                    .BindValue<ShellController>(static x => x.NewCardName),
                UIButtons.Primary("chatai.card.create").OnClick(nameof(ShellController.CreateCardAsync))
            ),
            CharacterView.CreateDialog(),
            PersonaView.CreateDialog(),
            WorldView.CreateDialog(),
            UIDialogs.Form(
                ShellController.GalleryTagDialogKey,
                "chatai.gallery.tag.title",
                (UIPhrase?)"chatai.gallery.tag.description",
                new TextInputComponent()
                    .SetTitle("chatai.gallery.tag.field")
                    .SetPlaceholder("happy")
                    .AsContent(TextInputComponent.PlaceholderProperty)
                    .SetAutocomplete(UIAutocomplete.Off)
                    .BindValue<ShellController>(static x => x.GalleryTagInput),
                UIButtons.Primary(UIStandardAction.Save).OnClick(nameof(ShellController.SaveGalleryTagAsync))
            ),
            .. CardEditor.CreateTagsDialogs(),
            UIDialogs.BoundConfirm<ShellController>(ShellController.DeleteCharacterDialogKey, "chatai.character.delete.title", static x => x.DeleteCardQuestion, "chatai.character.delete", nameof(ShellController.DeletePanelCardAsync)),
            UIDialogs.BoundConfirm<ShellController>(ShellController.DeletePersonaDialogKey, "chatai.persona.delete.title", static x => x.DeleteCardQuestion, "chatai.persona.delete", nameof(ShellController.DeletePanelCardAsync)),
            UIDialogs.BoundConfirm<ShellController>(ShellController.DeleteWorldDialogKey, "chatai.world.delete.title", static x => x.DeleteCardQuestion, "chatai.world.delete", nameof(ShellController.DeletePanelCardAsync)),
            UIDialogs.Confirm(ShellController.DeleteChatDialogKey, "chatai.chat.delete-chat.title", (UIPhrase?)"chatai.chat.delete-chat.question", "chatai.chat.delete-chat", nameof(ShellController.DeletePanelChatAsync))
        ];
}
