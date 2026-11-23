using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

internal static class CardEditor
{
    private const double AvatarSize = 112;
    private const double SwatchSize = 40;

    // Margin on the avatar rather than grid spacing, which would apply across all 24 columns and crush mobile width.
    public static ContainerComponent CreateHead()
        => new ContainerComponent()
            .SetColumn(1, UIGridUnit.Auto())
            .AddChild(new StackPanelComponent()
                .SetAlignWithFieldBoxes(UIFieldBoxAlignment.Medium)
                .SetMargin(UIThickness.All(0, 0, 24, 0))
                .SetPlacement(1, 1, 1, 1)
                .AddChild(new ImageInputComponent()
                    .SetVariant(UIImageInputVariant.Avatar)
                    .SetCrop(UIImageCrop.Circle)
                    .SetCropSize(ShellController.AvatarSize)
                    .SetMaxFileSize(ShellController.MaxAvatarBytes)
                    .SetWidth(UILayoutLength.Absolute(AvatarSize))
                    .SetHeight(UILayoutLength.Absolute(AvatarSize))
                    .BindValue<ShellController>(static x => x.CardAvatar.Value)
                    .BindSelectionId<ShellController>(static x => x.CardAvatar.Upload)
                    .OnChange(nameof(ShellController.SaveCardAvatarAsync))
                )
            )
            .AddChild(UILayout.Stack(12,
                    new TextInputComponent()
                        .SetTitle("chatai.card.name")
                        .BindValue<ShellController>(static x => x.CardName)
                        .OnChange(nameof(ShellController.SaveCardAsync)),
                    new MultiSelectComponent()
                        .SetVariant(UIMultiSelectVariant.Badges)
                        .SetTitle("chatai.card.tags")
                        .SetPlaceholder("chatai.card.tags.placeholder")
                        .BindOptions<ShellController>(static x => x.CardTagOptions)
                        .BindValue<ShellController>(static x => x.CardTags)
                        .OnChange(nameof(ShellController.SaveTagsAsync))
                        .AddTrailingAction(UIButtons.Icon(AppIcons.Outline(AppIcons.Tags), "chatai.tags.title").InteractOn(EventNames.Click, new OpenDialogEffect(ShellController.TagsDialogKey)))
                )
                .SetPlacement(2, 1, 23, 1)
            );

    public static StackPanelComponent PersonaField(string hint)
        => UIForm.Field(new SelectComponent()
                .SetTitle("chatai.persona.title")
                .BindOptions<ShellController>(static x => x.CardPersonaOptions)
                .BindValue<ShellController>(static x => x.CardPersonaId)
                .OnChange(nameof(ShellController.SaveCardAsync)),
            hint
        );

    public static StackPanelComponent WorldField(string hint)
        => UIForm.Field(new SelectComponent()
                .SetTitle("chatai.card.world")
                .BindOptions<ShellController>(static x => x.CardWorldOptions)
                .BindValue<ShellController>(static x => x.CardWorldId)
                .OnChange(nameof(ShellController.SaveCardAsync)),
            hint
        );

    public static IVisualComponent Area<TSource>(string title, Expression<Func<TSource, object?>> property, int rows, bool expandable = false, string? hint = null)
    {
        CodeInputComponent field = SettingsEditors.MacroInput(title, rows, expandable: expandable)
            .BindValue(property)
            .OnChange(nameof(ShellController.SaveCardAsync));

        return hint is null ? field : UIForm.Field(field, hint);
    }

    public static ContainerComponent FillArea<TSource>(string title, Expression<Func<TSource, object?>> property, bool lineNumbers = false, bool expandable = false, string? hint = null)
    {
        CodeInputComponent area = SettingsEditors.MacroInput(title, 12, lineNumbers, expandable)
            .SetHeight(UILayoutLength.Fill())
            .SetMinHeight(UILayoutLength.Absolute(160))
            .SetVerticalAlignment(UIAlignment.Stretch)
            .BindValue(property)
            .OnChange(nameof(ShellController.SaveCardAsync));
        ContainerComponent container = new ContainerComponent()
            .SetHeight(UILayoutLength.Fill())
            .SetRow(1, UIGridUnit.Star())
            .AddChild(area.SetPlacement(1, 1, 24, 1));

        if (hint is null)
            return container;

        return container
            .AddRow(UIGridUnit.Auto())
            .AddChild(UIText.Note(hint)
                .SetMargin(UIThickness.All(0, 6, 0, 0))
                .SetPlacement(1, 2, 24, 1)
            );
    }

    // Bleed margin preserves focus rings and switch halos inside full-height dialog tabs.
    public static ScrollContainerComponent Scroll(IVisualComponent content)
        => new ScrollContainerComponent()
            .SetHeight(UILayoutLength.Fill())
            .SetScrollFade(true)
            .SetMargin(UIThickness.Symmetric(-8, 0))
            .SetPadding(UIThickness.Symmetric(8, 2))
            .AddChild(content);

    public static ExpanderComponent Fold<TItem>(string removeCommand, string keyName, IVisualComponent content)
        where TItem : CardFoldItem
        => new ExpanderComponent()
            .SetSurface(UISurfaceStyle.Surface)
            .ConfigureDefaultHeader(header => header
                .SetTitleType(UITextAppearance.Caption)
                .SetTitleColor(UIThemeColor.Muted)
                .SetMargin(UIThickness.All(0, 0, 36, 0))
                .BindTitle<TItem>(static x => x.Header, UIBindingScope.Relative)
            )
            .SetHeaderAction(UIIconButtons.Delete()
                .SetSize(UISize.Small)
                .OnClick(removeCommand, UIAction.ArgCurrentItemKey(keyName))
            )
            .BindExpanded<TItem>(static x => x.Expanded, UIBindingScope.Relative)
            .SetContent(content);

    public static ExpanderComponent Fold(string saveCommand, string removeCommand, string keyName, params IVisualComponent[] fields)
    {
        ExpanderHeaderRegion? headerRegion = null;
        ExpanderComponent expander = new ExpanderComponent()
            .SetSurface(UISurfaceStyle.Surface)
            .ConfigureDefaultHeader(header =>
            {
                headerRegion = header;
                _ = header
                    .SetTitleType(UITextAppearance.Caption)
                    .SetTitleColor(UIThemeColor.Muted)
                    .SetDescriptionType(UITextAppearance.Caption)
                    .SetDescriptionColor(UIThemeColor.Muted)
                    .SetDescriptionPlain(true)
                    .SetMargin(UIThickness.All(0, 0, 68, 0))
                    .SetRenamable(true)
                    .BindTitle<ArticleItem>(static x => x.Header, UIBindingScope.Relative)
                    .BindDescription<ArticleItem>(static x => x.Summary, UIBindingScope.Relative)
                    .BindRenamedTitle<ArticleItem>(static x => x.Title, UIBindingScope.Relative)
                    .OnRename(saveCommand);
            })
            .BindExpanded<ArticleItem>(static x => x.Expanded, UIBindingScope.Relative)
            .SetContent(UILayout.Stack(12, fields));

        return expander.SetHeaderAction(UILayout.Row(4,
            UIIconButtons.Rename()
                .SetSize(UISize.Small)
                .InteractOn(EventNames.Click, new RenameItemEffect(headerRegion!)),
            UIIconButtons.Delete()
                .SetSize(UISize.Small)
                .OnClick(removeCommand, UIAction.ArgCurrentItemKey(keyName))
        ));
    }

    public static UIDialog CreateDialog(string key, string title, IVisualComponent body)
        => UIDialogs.Fill(
            key,
            title,
            body,
            width: UIColumnWidth.Wide,
            trailing:
            [
                TokensCaption(static x => x.CardTokens),
                UIIconButtons.Duplicate()
                    .SetSize(UISize.Small)
                    .OnClick(nameof(ShellController.DuplicateEditingCardAsync)),
                UIIconButtons.Delete()
                    .SetSize(UISize.Small)
                    .OnClick(nameof(ShellController.AskDeleteEditingCard))
            ]
        );

    private static TextComponent TokensCaption(Expression<Func<ShellController, object?>> tokens)
        => UIText.Caption()
            .Muted()
            .SetTitleWrap(false)
            .SetVerticalAlignment(UIAlignment.Center)
            .SetMargin(UIThickness.All(0, 0, 4, 0))
            .BindTitle(tokens);

    public static IReadOnlyList<UIDialog> CreateTagsDialogs()
        =>
        [
            CreateTagsDialog(),
            UIDialogs.BoundConfirm<ShellController>(ShellController.DeleteTagDialogKey, "chatai.tags.delete.title", static x => x.DeleteTagQuestion, UIStandardAction.Delete.Title, nameof(ShellController.DeleteTagAsync))
        ];

    // Color opacity allows resetting alpha to 0 to restore the default untinted badge.
    private static UIDialog CreateTagsDialog()
        => UIDialogs.Create(ShellController.TagsDialogKey, "chatai.tags.title", UILayout.Stack(16,
                new TextInputComponent()
                    .SetTitle("chatai.tags.new")
                    .BindValue<ShellController>(static x => x.NewTag)
                    .OnEnter(nameof(ShellController.AddTagAsync))
                    .AddTrailingAction(UIIconButtons.Add().OnClick(nameof(ShellController.AddTagAsync))),
                new ItemsViewComponent()
                    .SetVerticalScroll(UIScrollMode.Disabled)
                    .BindItems<ShellController>(static x => x.AllTags)
                    .SetSpacing(8)
                    .ConfigureDefaultEmptyTemplate(empty => empty.SetTitle("chatai.tags.none").SetTitleWrap(true))
                    .SetTemplate(new ContainerComponent()
                        .SetColumn(1, UIGridUnit.Auto())
                        .AddChild(new ColorInputComponent()
                            .SetShowOpacity(true)
                            .SetWidth(UILayoutLength.Absolute(SwatchSize))
                            .SetHeight(UILayoutLength.Absolute(SwatchSize))
                            .BindValue<TagRow>(static x => x.Color, UIBindingScope.Relative)
                            .OnChange(nameof(ShellController.SaveTagColorAsync), UIAction.ArgCurrentItemKey("tag"))
                            .SetVerticalAlignment(UIAlignment.Center)
                            .SetMargin(UIThickness.All(0, 0, 12, 0))
                            .SetPlacement(1, 1, 1, 1)
                        )
                        .AddChild(new TextInputComponent()
                            .BindValue<TagRow>(static x => x.Name, UIBindingScope.Relative)
                            .OnChange(nameof(ShellController.RenameTagAsync), UIAction.ArgCurrentItemKey("tag"))
                            .AddTrailingAction(UIIconButtons.Delete().OnClick(nameof(ShellController.AskDeleteTag), UIAction.ArgCurrentItemKey("tag")))
                            .SetVerticalAlignment(UIAlignment.Center)
                            .SetPlacement(2, 1, 23, 1)
                        )
                    )
            ),
            width: UIColumnWidth.Form
        );
}
