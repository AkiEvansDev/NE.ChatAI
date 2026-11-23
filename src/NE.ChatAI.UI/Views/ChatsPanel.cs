using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

internal static class ChatsPanel
{
    // Matches a card's name plus one row of tag badges (24 + 4 + 16), keeping rows uniform with or without tags.
    private const double AvatarSize = 44;
    private const double HeaderAvatarSize = 34;
    public const string SearchId = "chatai-card-search";
    public const string TagFilterId = "chatai-tag-filter";
    private const string ListSwitchId = "chatai-list-switch";

    public static StackPanelComponent CreateHeading()
        => UILayout.Stack(0, CreateListSwitch(), CreateCharacterTitle())
            .SetMinHeight(UILayoutLength.Absolute(HeaderAvatarSize))
            .SetGlyphEdge(UIAlignment.Start);

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
        => UILayout.Row(8,
                UIIconButtons.Back().SetSize(UISize.Small).SetVerticalAlignment(UIAlignment.Center).OnClick(nameof(ShellController.ShowCharacterList)),
                Avatar<ShellController>(static x => x.PanelCharacter.Avatar, UIBindingScope.Root, HeaderAvatarSize),
                new TextComponent()
                    .BindTitle<ShellController>(static x => x.PanelCharacter.Name)
                    .BindTooltip<ShellController>(static x => x.PanelCharacter.Name)
                    .SetTooltipPlain(true)
                    .AsContent(TextComponent.TitleProperty, ITooltipComponent.TooltipProperty)
                    .AsSubtitle()
                    .SetVerticalAlignment(UIAlignment.Center)
            )
            .SetMinHeight(UILayoutLength.Absolute(HeaderAvatarSize))
            .SetGlyphEdge(UIAlignment.Start)
            .BindVisibility<ShellController>(static x => x.ChatsPanes.DetailVisibility);

    private static ImageComponent Avatar<TSource>(Expression<Func<TSource, object?>> source, UIBindingScope scope, double size = AvatarSize)
        => new ImageComponent()
            .BindSource(source, scope)
            .SetAltText("chatai.chat.avatar")
            .SetLazy(false)
            .SetShape(UIShape.Circle)
            .SetWidth(UILayoutLength.Absolute(size))
            .SetVerticalAlignment(UIAlignment.Center);

    public static TextInputComponent CreateSearch()
        => new TextInputComponent(SearchId)
            .SetSearch()
            .SetDebounceMilliseconds(200)
            .AddTrailingAction(CreateTagFilter())
            .AddTrailingAction(UIIconButtons.New().OnClick(nameof(ShellController.AskCreateCard)))
            .BindVisibility<ShellController>(static x => x.ChatsPanes.ListVisibility);

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

    private static ContainerComponent CreateCardRow(bool showEdit)
    {
        ButtonComponent delete = UIIconButtons.Delete()
            .SetSize(UISize.Small)
            .OnClick(nameof(ShellController.AskDeletePanelCard), UIAction.ArgCurrentItemKey("cardId"));
        StackPanelComponent actions = showEdit
            ? UILayout.Row(4,
                UIIconButtons.Edit().SetSize(UISize.Small).OnClick(nameof(ShellController.EditPanelCardAsync), UIAction.ArgCurrentItemKey("cardId")),
                delete
            )
            : UILayout.Row(4, delete);

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

    private static BadgeComponent CreateTagBadge()
        => new BadgeComponent()
            .SetVariant(UIBadgeVariant.Surface)
            .SetTitleType(UITextAppearance.Caption)
            .BindTitle<TagBadge>(static x => x.Text, UIBindingScope.Relative)
            .BindColor<TagBadge>(static x => x.Color, UIBindingScope.Relative);

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
        => UILayout.Ends(
                new TextComponent()
                    .AsBody()
                    .SetDescriptionPlain(true)
                    .BindTitle<MenuItem>(static x => x.Title, UIBindingScope.Relative)
                    .BindDescription<MenuItem>(static x => x.Description, UIBindingScope.Relative),
                UIIconButtons.Delete()
                    .SetSize(UISize.Small)
                    .OnClick(nameof(ShellController.AskDeleteChat), UIAction.ArgCurrentItemKey("chatId"))
            )
            .SetPadding(UIThickness.Symmetric(0, 2));

    public static IVisualComponent[] CreateActions()
        =>
        [
            UIButtons.Icon(AppIcons.Outline(AppIcons.Bindings), "chatai.nav.worlds")
                .SetSize(UISize.Small)
                .SetUrl(AppRoutes.Worlds)
                .BindVisibility<ShellController>(static x => x.ChatsPanes.ListVisibility),
            UIIconButtons.New()
                .SetSize(UISize.Small)
                .OnClick(nameof(ShellController.StartChatAsync))
                .BindVisibility<ShellController>(static x => x.ChatsPanes.DetailVisibility),
            UIIconButtons.Edit()
                .SetSize(UISize.Small)
                .OnClick(nameof(ShellController.EditCharacterAsync))
                .BindVisibility<ShellController>(static x => x.ChatsPanes.DetailVisibility)
        ];

    public static IReadOnlyList<UIDialog> CreateDialogs()
        =>
        [
            UIDialogs.BoundForm<ShellController>(
                ShellController.CreateCardDialogKey,
                static x => x.CreateCardTitle,
                null,
                new TextInputComponent()
                    .SetTitle("chatai.card.name")
                    .BindValue<ShellController>(static x => x.NewCardName, mode: UIBindingMode.OnSubmit),
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
                    .BindValue<ShellController>(static x => x.GalleryTagInput, mode: UIBindingMode.OnSubmit),
                UIButtons.Primary(UIStandardAction.Save).OnClick(nameof(ShellController.SaveGalleryTagAsync))
            ),
            .. CardEditor.CreateTagsDialogs(),
            UIDialogs.BoundConfirm<ShellController>(ShellController.DeleteCharacterDialogKey, "chatai.character.delete.title", static x => x.DeleteCardQuestion, UIStandardAction.Delete.Title, nameof(ShellController.DeletePanelCardAsync)),
            UIDialogs.BoundConfirm<ShellController>(ShellController.DeletePersonaDialogKey, "chatai.persona.delete.title", static x => x.DeleteCardQuestion, UIStandardAction.Delete.Title, nameof(ShellController.DeletePanelCardAsync)),
            UIDialogs.BoundConfirm<ShellController>(ShellController.DeleteWorldDialogKey, "chatai.world.delete.title", static x => x.DeleteCardQuestion, UIStandardAction.Delete.Title, nameof(ShellController.DeletePanelCardAsync)),
            UIDialogs.Confirm(ShellController.DeleteChatDialogKey, "chatai.chat.delete-chat.title", "chatai.chat.delete-chat.question", UIStandardAction.Delete.Title, nameof(ShellController.DeletePanelChatAsync))
        ];
}
