namespace NE.ChatAI.Controllers;

/// <summary>A picture field: the picture as the data folder holds it, the upload the field just made, and its remove's visibility.</summary>
internal sealed partial class PictureSlot : RecursiveObservable
{
    /// <summary>The picture's address; none, and the field shows its empty look.</summary>
    [RecursiveMember]
    public partial string? Value { get; set; }

    /// <summary>The selection the field just uploaded, taken by <see cref="TakeUpload"/>.</summary>
    [RecursiveMember]
    public partial string? Upload { get; set; }

    [RecursiveMember]
    public partial UIVisibility PickerVisibility { get; set; } = UIVisibility.Visible;

    [RecursiveMember]
    public partial UIVisibility RemoveVisibility { get; set; } = UIVisibility.Collapsed;

    /// <summary>Shows the picture at <paramref name="address"/>, its remove with it; none, and both go.</summary>
    public void Show(string? address)
    {
        Value = address;
        PickerVisibility = address is null ? UIVisibility.Visible : UIVisibility.Collapsed;
        RemoveVisibility = address is null ? UIVisibility.Collapsed : UIVisibility.Visible;
    }

    /// <summary>The upload the field made, cleared so the next pick is a change again; none when there is none.</summary>
    public string? TakeUpload()
    {
        var upload = Upload;
        Upload = null;

        return upload is { Length: > 0 } ? upload : null;
    }
}
