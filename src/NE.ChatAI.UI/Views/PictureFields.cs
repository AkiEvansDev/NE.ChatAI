using System;
using System.Linq.Expressions;
using NE.ChatAI.Controllers;

namespace NE.ChatAI.Views;

/// <summary>A picture picked for behind the chats — a character's or the application's — with a live chat preview once loaded and its remove under it.</summary>
internal static class PictureFields
{
    /// <summary>The largest picture taken: a photograph, not a scan.</summary>
    public const long MaxBackgroundBytes = 16 * 1024 * 1024;

    private const double PreviewHeight = 180;
    private const double PreviewColumnWidth = 480;
    private const double PreviewAvatarSize = 40;

    /// <summary>The field over the controller's <see cref="PictureSlot"/> at <paramref name="slot"/>, its upload kept by <paramref name="save"/>; a dimmed preview with a sample message once loaded, and the remove beneath.</summary>
    public static StackPanelComponent Background<TSource>(string title, string hint, Expression<Func<TSource, PictureSlot>> slot, string save, string remove, Expression<Func<TSource, object?>>? author = null, Expression<Func<TSource, object?>>? avatar = null, string? dimSliderId = null)
    {
        RecursivePath path = RecursivePath.FromExpression(slot);
        ImageInputComponent picker = new ImageInputComponent()
            .SetVariant(UIImageInputVariant.Picture)
            .SetMaxFileSize(MaxBackgroundBytes)
            .SetHeight(UILayoutLength.Absolute(PreviewHeight))
            .BindValue(path.AppendProperty(nameof(PictureSlot.Value)))
            .BindSelectionId(path.AppendProperty(nameof(PictureSlot.Upload)))
            .OnChange(save);
        _ = picker.HiddenWhen(picker);

        return UILayout.Stack(4,
            UIForm.Field(
                UILayout.Stack(4,
                    new TextComponent().SetTitle(title).AsCaption().Muted(),
                    new ContainerComponent()
                        .AddChild(picker.SetPlacement(1, 1, 24, 1))
                        .AddChild(CreatePreview(path, picker, author, avatar, dimSliderId)
                            .ShownWhen(picker)
                            .SetPlacement(1, 1, 24, 1)
                        )
                ),
                hint
            ),
            UIButtons.Link("chatai.background.remove")
                .SetSize(UISize.Small)
                .SetHorizontalAlignment(UIAlignment.Start)
                .OnClick(remove)
                .ShownWhen(picker)
        );
    }

    private static SurfaceComponent CreatePreview<TSource>(RecursivePath path, ImageInputComponent picker, Expression<Func<TSource, object?>>? author, Expression<Func<TSource, object?>>? avatar, string? dimSliderId)
    {
        ImageComponent avatarImage = new ImageComponent()
            .SetAltText("chatai.chat.avatar")
            .SetShape(UIShape.Circle)
            .SetWidth(UILayoutLength.Absolute(PreviewAvatarSize))
            .SetMargin(UIThickness.All(0, 2, 0, 0));

        if (avatar is not null)
            _ = avatarImage.BindSource(avatar);

        TextComponent speaker = new TextComponent().AsSubtitle();
        _ = author is not null ? speaker.BindTitle(author).AsContent(TextComponent.TitleProperty) : speaker.SetTitle("chatai.settings.role.character");

        SurfaceComponent surface = new SurfaceComponent()
            .SetSurface(UISurfaceStyle.Background)
            .SetBackgroundImageFit(UIImageFit.Cover)
            .BindBackgroundImage(path.AppendProperty(nameof(PictureSlot.Value)))
            .BindBackgroundImageDim<ShellController>(static x => x.BackgroundDim)
            .SetPadding(UIThickness.Symmetric(24, 0))
            .SetBorderRadius(UICornerRadius.Uniform(8))
            .SetHeight(UILayoutLength.Absolute(PreviewHeight))
            .SetWidth(UILayoutLength.Fill())
            .SetClickable(true)
            .InteractOn(EventNames.Click, new OpenPickerEffect(picker))
            .SetContent(new ContainerComponent()
                .BindBackground<ShellController>(static x => x.ChatSurface)
                .SetPadding(UIThickness.Symmetric(16, 16))
                .SetHeight(UILayoutLength.Fill())
                .SetWidth(UILayoutLength.Fill())
                .SetMaxWidth(UILayoutLength.Absolute(PreviewColumnWidth))
                .SetHorizontalAlignment(UIAlignment.Center)
                .SetColumn(1, UIGridUnit.Auto())
                .AddChild(UILayout.Stack(4,
                        avatarImage,
                        new TextComponent()
                            .SetTitle("42")
                            .AsCaption()
                            .Muted()
                            .SetHorizontalAlignment(UIAlignment.Center)
                    )
                    .SetMargin(UIThickness.All(0, 0, 12, 0))
                    .SetVerticalAlignment(UIAlignment.Center)
                    .SetPlacement(1, 1, 1, 1)
                )
                .AddChild(UILayout.Stack(4,
                        speaker,
                        ChatView.StyleMessageMarkdown(new MarkdownDisplayComponent().BindText<ShellController>(static x => x.BackgroundPreviewText))
                    )
                    .SetVerticalAlignment(UIAlignment.Center)
                    .SetPlacement(2, 1, 23, 1)
                )
            );

        if (dimSliderId is not null)
            _ = surface.InteractCopyValue(dimSliderId, ISurfaceComponent.BackgroundImageDimProperty);

        return surface;
    }
}
