using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Settings;

namespace NE.ChatAI.Controllers;

/// <summary>What every page has: what the two panels at its sides show.</summary>
internal abstract partial class ShellController : UIControllerBase
{
    /// <summary>The application's own name, the same in every language: the author of a chat without a character.</summary>
    public const string AppName = "NE.ChatAI";

    /// <summary>How opaque the surface behind the central chat column is when the message background setting is on.</summary>
    public const byte MessageSurfaceOpacity = 160;

    /// <summary>The semi-transparent backdrop behind the central chat column when the message background setting is on.</summary>
    private static readonly UIThemeColor MessageSurface = UIThemeColor.Create(
        UIThemeDefaults.LightPalette.Background.WithOpacity(MessageSurfaceOpacity),
        UIThemeDefaults.DarkPalette.Background.WithOpacity(MessageSurfaceOpacity)
    );

    /// <summary>The fill behind the central chat column and its preview when the message background setting is on.</summary>
    [RecursiveMember]
    public partial UIThemeColor? ChatSurface { get; set; }

    /// <summary>How far the picture behind the chat and its preview is dimmed toward the theme's ground.</summary>
    [RecursiveMember]
    public partial double BackgroundDim { get; set; } = AppSettings.DefaultBackgroundDim;

    /// <summary>The localized sample message shown over a background picture in its preview.</summary>
    [RecursiveMember]
    public partial string BackgroundPreviewText { get; set; } = string.Empty;

    /// <summary>Whether the open page is currently generating a reply or scenario on the open chat.</summary>
    protected virtual bool IsGenerating => false;

    /// <summary>
    /// The chats are read here so that a pinned chats panel paints them; the model is asked about at the attach that follows: a
    /// job of the runtime's cannot start before the runtime has.
    /// </summary>
    protected sealed override async Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        BackgroundPreviewText = Context.Translate("chatai.background.preview.text") ?? string.Empty;
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ApplyChatSurface(settings.MessageBackground);
        BackgroundDim = Math.Clamp(settings.BackgroundDim, 0, 1);
        await LoadPresetsAsync(cancellationToken).ConfigureAwait(false);
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A page found again — a tab come back, a reload — reads the settings and the chats afresh: another tab may have changed them,
    /// and a pinned chats panel shows them without being opened.
    /// </summary>
    protected override async Task OnAttachedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        BackgroundPreviewText = Context.Translate("chatai.background.preview.text") ?? string.Empty;
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ApplyChatSurface(settings.MessageBackground);
        BackgroundDim = Math.Clamp(settings.BackgroundDim, 0, 1);
        await LoadGenerationAsync(cancellationToken).ConfigureAwait(false);
        await LoadChatsAsync(cancellationToken).ConfigureAwait(false);
    }

    protected virtual void ApplyChatSurface(bool enabled)
        => ChatSurface = enabled ? MessageSurface : null;

    /// <summary>The picture a field just uploaded, read whole with its type and taken from the field; none when it holds no file.</summary>
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

    /// <summary>Reads every picture uploaded under <paramref name="selectionId"/> in order, with its original file name and content type.</summary>
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

    /// <summary>A command's answer that is only a toast.</summary>
    protected static UICommandResult Notify(string key, UIColorStyle style)
        => UICommandResult.Ok([new ShowNotificationEffect(key, style)]);

    /// <summary>A command's answer that is only a parameterized toast.</summary>
    protected static UICommandResult Notify(UIPhrase message, UIColorStyle style)
        => UICommandResult.Ok([new ShowNotificationEffect(message, style)]);
}
