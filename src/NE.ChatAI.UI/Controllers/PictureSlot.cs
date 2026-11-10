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
