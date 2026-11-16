using System;
using NE.ChatAI.Characters;

namespace NE.ChatAI.Controllers;

/// <summary>A picture field: the picture as the data folder holds it and the upload the field just made.</summary>
internal sealed partial class PictureSlot : RecursiveObservable
{
    /// <summary>The picture's address; none, and the field shows its empty look.</summary>
    [RecursiveMember]
    public partial string? Value { get; set; }

    /// <summary>The selection the field just uploaded, taken by <see cref="TakeUpload"/>.</summary>
    [RecursiveMember]
    public partial string? Upload { get; set; }

    /// <summary>Shows the picture at <paramref name="address"/>; none, and it goes.</summary>
    public void Show(string? address)
        => Value = address;

    /// <summary>The upload the field made, cleared so the next pick is a change again; none when there is none.</summary>
    public string? TakeUpload()
    {
        var upload = Upload;
        Upload = null;

        return upload is { Length: > 0 } ? upload : null;
    }
}

/// <summary>One image tile in a global or character gallery: its slug id, its copyable key (tag or canonical key), its tooltip, its served preview address, and its pending replace upload.</summary>
internal sealed partial class GalleryItem : RecursiveObservable, IBindableItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    [RecursiveMember]
    public partial string CopyText { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string Tooltip { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string? Tag { get; set; }

    [RecursiveMember]
    public partial string? Preview { get; set; }

    [RecursiveMember]
    public partial string? ReplaceSelectionId { get; set; }

    public static GalleryItem From(GalleryImage image, string? preview)
    {
        ArgumentNullException.ThrowIfNull(image);

        return new GalleryItem
        {
            Id = image.Id,
            CopyText = image.CopyKey,
            Tooltip = !string.IsNullOrEmpty(image.Tag) ? $"{image.Tag} · {image.Key}" : image.Key,
            Tag = image.Tag,
            Preview = preview
        };
    }

    public string? TakeReplaceUpload()
    {
        var upload = ReplaceSelectionId;
        ReplaceSelectionId = null;

        return upload is { Length: > 0 } ? upload : null;
    }
}
