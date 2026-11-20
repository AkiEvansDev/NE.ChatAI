using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Settings;

namespace NE.ChatAI.Controllers;

/// <summary>Base controller shared by every page: side drawers for generation settings and chats/cards, plus upload helpers.</summary>
internal abstract partial class ShellController : UIControllerBase
{
    public const string AppName = "NE.ChatAI";
    public const byte MessageSurfaceOpacity = 160;

    private static readonly UIThemeColor MessageSurface = UIThemeColor.Create(
        UIThemeDefaults.LightPalette.Background.WithOpacity(MessageSurfaceOpacity),
        UIThemeDefaults.DarkPalette.Background.WithOpacity(MessageSurfaceOpacity)
    );

    [RecursiveMember]
    public partial UIThemeColor? ChatSurface { get; set; }

    [RecursiveMember]
    public partial double BackgroundDim { get; set; } = AppSettings.DefaultBackgroundDim;

    [RecursiveMember]
    public partial string BackgroundPreviewText { get; set; } = string.Empty;

    protected virtual bool IsGenerating => false;

    protected sealed override async Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        BackgroundPreviewText = Context.Translate("chatai.background.preview.text") ?? string.Empty;
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ApplyChatSurface(settings.MessageBackground);
        BackgroundDim = Math.Clamp(settings.BackgroundDim, 0, 1);
        await LoadPresetsAsync(runtimeActive: false, cancellationToken).ConfigureAwait(false);
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task OnAttachedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        BackgroundPreviewText = Context.Translate("chatai.background.preview.text") ?? string.Empty;
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ApplyChatSurface(settings.MessageBackground);
        BackgroundDim = Math.Clamp(settings.BackgroundDim, 0, 1);
        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task OnLanguageChangedAsync(string language, CancellationToken cancellationToken)
    {
        BackgroundPreviewText = Context.Translate("chatai.background.preview.text") ?? string.Empty;

        return base.OnLanguageChangedAsync(language, cancellationToken);
    }

    protected virtual void ApplyChatSurface(bool enabled)
        => ChatSurface = enabled ? MessageSurface : null;

    protected async Task<(byte[] Content, string? ContentType)?> TakePictureAsync(PictureSlot picture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(picture);

        if (picture.TakeUpload() is not string selectionId)
            return null;

        UIUploadSelection selection = await Context.Uploads.GetSelectionAsync(Context.Handle, selectionId, cancellationToken).ConfigureAwait(false);

        if (selection.Files.Length == 0)
            return null;

        UIUploadedFile file = await Context.Uploads.OpenAsync(Context.Handle, selection.Files[0].FileId, cancellationToken: cancellationToken).ConfigureAwait(false);

        await using (file.ConfigureAwait(false))
        {
            using MemoryStream content = new();

            await file.Content.CopyToAsync(content, cancellationToken).ConfigureAwait(false);

            return (content.ToArray(), file.Metadata.ContentType);
        }
    }

    protected async Task<List<(string FileName, byte[] Content, string? ContentType)>> ReadUploadedPicturesAsync(string? selectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selectionId))
            return [];

        UIUploadSelection selection = await Context.Uploads.GetSelectionAsync(Context.Handle, selectionId, cancellationToken).ConfigureAwait(false);
        List<(string FileName, byte[] Content, string? ContentType)> files = [with(selection.Files.Length)];

        for (var i = 0; i < selection.Files.Length; i++)
        {
            UIUploadFile entry = selection.Files[i];

            if (entry.Size is <= 0 or > Views.PictureFields.MaxBackgroundBytes)
                continue;

            UIUploadedFile opened = await Context.Uploads.OpenAsync(Context.Handle, entry.FileId, cancellationToken: cancellationToken).ConfigureAwait(false);

            await using (opened.ConfigureAwait(false))
            {
                using MemoryStream content = new();
                await opened.Content.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
                files.Add((entry.FileName, content.ToArray(), opened.Metadata.ContentType));
            }
        }

        return files;
    }

    protected static UICommandResult Notify(string key, UIColorStyle style)
        => UICommandResult.Ok([new ShowNotificationEffect(key, style)]);

    protected static UICommandResult Notify(UIPhrase message, UIColorStyle style)
        => UICommandResult.Ok([new ShowNotificationEffect(message, style)]);
}
