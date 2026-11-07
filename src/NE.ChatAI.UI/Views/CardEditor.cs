using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>
/// What a character's, a persona's and a world's editor dialog share: the picture, cropped to a circle as it is picked; the name;
/// the tags, picked here and made, renamed, recoloured and deleted in their own dialog; the description; and the delete at the dialog's head.
/// Every field is kept as it lets go of its value.
/// </summary>
internal static class CardEditor
{
    private const double AvatarSize = 112;

    /// <summary>A tag's swatch, square at a field's height: too narrow for its value, it shows the colour alone.</summary>
    private const double SwatchSize = 40;

    /// <summary>The picture, aligned to the field boxes of the name and the tags beside it.</summary>
    /// <remarks>
    /// The air is the picture's margin, not the grid's spacing: that stands between all twenty-four tracks and squeezed the picture's column to nothing on a phone.
    /// Uncoloured Surface badges share the filled input's <c>--ui-color-surface</c> background and read lower than coloured ones (<c>NE.Standard-private#281</c>).
    /// </remarks>
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
                        .AddTrailingAction(UIButtons.Icon(AppIcons.Outline(AppIcons.Tags), "chatai.tags.title").OnClick(nameof(ShellController.OpenTagsAsync)))
                )
                .SetPlacement(2, 1, 23, 1)
            );

    /// <summary>The world the card is played in, picked from every world; a character's wins over the persona's.</summary>
    public static IVisualComponent WorldField(string hint)
        => UIForm.Field(new SelectComponent()
                .SetTitle("chatai.card.world")
                .BindOptions<ShellController>(static x => x.CardWorldOptions)
                .BindValue<ShellController>(static x => x.CardWorldId)
                .OnChange(nameof(ShellController.SaveCardAsync)),
            hint
        );

    /// <summary>A Markdown and macro field of the card kept as it lets go, as tall as a card's words are long.</summary>
    public static IVisualComponent Area<TSource>(string title, Expression<Func<TSource, object?>> property, int rows, string? hint = null)
    {
        CodeInputComponent area = SettingsEditors.MacroInput(title, rows)
            .BindValue(property)
            .OnChange(nameof(ShellController.SaveCardAsync));

        return hint is null ? area : UIForm.Field(area, hint);
    }

    /// <summary>A group of fields that belong together, separated by a rule and a heading when titled.</summary>
    public static StackPanelComponent Section(string? title, params IVisualComponent[] fields)
        => title is null
            ? UILayout.Stack(16, fields)
            : UILayout.Stack(16, [new SeparatorComponent(), UIText.Subtitle(title), .. fields]);

    /// <summary>A wide card-editor dialog with a fixed header, a delete button in it, and a scrollable body.</summary>
    public static UIDialog CreateDialog(string key, string title, params IVisualComponent[] sections)
        => UIDialogs.Create(
            key,
            title,
            new ContainerComponent(key).AddChild(UILayout.Stack(24, sections)),
            width: UIColumnWidth.Wide,
            trailing: UIIconButtons.Delete().SetSize(UISize.Small).OnClick(nameof(ShellController.AskDeleteEditingCard))
        );

    /// <summary>The tags dialog and the question before a tag is deleted.</summary>
    public static IReadOnlyList<UIDialog> CreateTagsDialogs()
        =>
        [
            CreateTagsDialog(),
            UIDialogs.BoundConfirm<ShellController>(ShellController.DeleteTagDialogKey, "chatai.tags.delete.title", static x => x.DeleteTagQuestion, "chatai.card.delete", nameof(ShellController.DeleteTagAsync))
        ];

    /// <summary>
    /// Every tag known on a line — the swatch its colour is picked from, its name kept as it lets go with its delete inside — under
    /// the field a new one is made in; each change is every card's.
    /// </summary>
    /// <remarks>
    /// Opacity is on so setting it to zero clears a tag's colour back to the default transparent badge; the palette tab's factor value
    /// and white selection border are tracked in NE.Standard-private#280.
    /// </remarks>
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
