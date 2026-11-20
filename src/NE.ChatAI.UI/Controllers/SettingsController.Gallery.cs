using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Settings;

namespace NE.ChatAI.Controllers;

/// <summary>The global image gallery (<c>global.&lt;id&gt;</c>): uploading, replacing, tagging and deleting shared pictures.</summary>
internal sealed partial class SettingsController
{
    [RecursiveMember]
    public partial string? GallerySelectionId { get; set; }

    [RecursiveMember(false)]
    public RecursiveCollection<GalleryItem> GlobalGallery { get; } = [];

    protected override async Task OnGlobalGalleryChangedAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await Data.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        ShowGlobalGallery(settings.ImageTags);
    }

    private void ShowGlobalGallery(IReadOnlyDictionary<string, string>? imageTags)
        => SyncGallery(GlobalGallery, GalleryPictures.ListGlobal(Data, imageTags), Pictures.GlobalGalleryImageOf);

    /// <summary>Adds every uploaded picture to the global gallery under collision-free slugs.</summary>
    [UICommand]
    public async Task UploadGlobalGalleryAsync(CancellationToken cancellationToken)
    {
        var selectionId = GallerySelectionId;
        GallerySelectionId = null;

        List<(string FileName, byte[] Content, string? ContentType)> uploaded = await ReadUploadedPicturesAsync(selectionId, cancellationToken).ConfigureAwait(false);

        if (uploaded.Count == 0)
            return;

        for (var i = 0; i < uploaded.Count; i++)
            _ = await GalleryPictures.WriteGlobalAsync(Data, uploaded[i].FileName, uploaded[i].Content, uploaded[i].ContentType, cancellationToken).ConfigureAwait(false);

        await OnGlobalGalleryChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the picture file of <paramref name="imageId"/> in the global gallery while preserving its id and assigned tag.</summary>
    [UICommand]
    public async Task ReplaceGlobalGalleryAsync(string imageId, CancellationToken cancellationToken)
    {
        if (!GlobalGallery.TryGet(imageId, out GalleryItem? item))
            return;

        List<(string FileName, byte[] Content, string? ContentType)> uploaded = await ReadUploadedPicturesAsync(item.TakeReplaceUpload(), cancellationToken).ConfigureAwait(false);

        if (uploaded.Count == 0)
            return;

        if (await GalleryPictures.ReplaceGlobalAsync(Data, imageId, uploaded[0].Content, uploaded[0].ContentType, cancellationToken).ConfigureAwait(false) is not null)
            item.Preview = Pictures.GlobalGalleryImageOf(imageId);
    }

    /// <summary>Dispatches context menu actions on a tile in the global gallery.</summary>
    [UICommand]
    public async Task<UICommandResult> GlobalGalleryActionAsync(string action, string imageId, CancellationToken cancellationToken)
    {
        if (action == GalleryTagAction && GlobalGallery.TryGet(imageId, out GalleryItem? item))
            return OpenGalleryTagDialog(isGlobal: true, imageId, item.Tag);

        if (action == GalleryDeleteAction)
        {
            await GalleryPictures.DeleteGlobalAsync(Data, imageId, cancellationToken).ConfigureAwait(false);
            _ = GlobalGallery.Remove(imageId);
        }

        return UICommandResult.Ok();
    }
}
