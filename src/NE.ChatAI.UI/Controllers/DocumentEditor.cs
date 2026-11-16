using System;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Controllers;

/// <summary>A document as its editor shows it: shown from the kept document, written back over it.</summary>
internal interface IDocumentDraft<T>
    where T : NamedDocument
{
    string Name { get; set; }

    void Show(T document);

    /// <summary>The kept document with the editor's fields over it, so what the editor does not show stays as it was.</summary>
    T Apply(T kept);
}

/// <summary>One kind's editor on the settings page, whatever the kind: what the page asks of it by the kind's name.</summary>
internal interface IDocumentEditor
{
    /// <summary>The name of the document shown; none when nothing is chosen.</summary>
    string? Name { get; }

    Task ShowAsync(CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Makes a new document — the kind's blank, or a copy of the one shown — named by <paramref name="name"/>, and chooses it.</summary>
    Task CreateAsync(bool copy, string name, CancellationToken cancellationToken);

    void Rename(string name);

    void Delete();

    /// <summary>Chooses <paramref name="id"/>, else the first document of the kind's list.</summary>
    void Choose(string? id);
}

/// <summary>
/// The editor of one kind of document on the settings page: the document chosen in its list, its fields as a draft, and whether there
/// is one to show — an empty kind shows only its New.
/// </summary>
/// <remarks>Its store, its blank and its list are the page's, handed in by <see cref="Attach"/> once the page is ready.</remarks>
internal sealed partial class DocumentEditor<T, TDraft> : RecursiveObservable, IDocumentEditor
    where T : NamedDocument
    where TDraft : RecursiveObservable, IDocumentDraft<T>, new()
{
    private DocumentStore<T>? _store;
    private Func<string, T, CancellationToken, Task<string>>? _save;
    private Func<T> _blank = static () => throw new InvalidOperationException("The editor is not attached to its store.");
    private Func<RecursiveCollection<OptionItem>> _options = static () => [];

    [RecursiveMember]
    public partial string? Id { get; set; }

    [RecursiveMember]
    public partial TDraft Draft { get; set; } = new();

    [RecursiveMember]
    public partial UIVisibility Visibility { get; set; } = UIVisibility.Collapsed;

    public string? Name => Id is null ? null : Draft.Name;

    /// <summary>The kind's store, the document New makes, the list the page chooses from, and an optional rename-aware writer.</summary>
    public void Attach(DocumentStore<T> store, Func<T> blank, Func<RecursiveCollection<OptionItem>> options, Func<string, T, CancellationToken, Task<string>>? save = null)
    {
        _store = store;
        _blank = blank;
        _options = options;
        _save = save;
    }

    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        if (Id is null || await Store().ReadAsync(Id, cancellationToken).ConfigureAwait(false) is not T document)
        {
            Visibility = UIVisibility.Collapsed;
            return;
        }

        Draft.Show(document);
        Visibility = UIVisibility.Visible;
    }

    private DocumentStore<T> Store()
        => _store ?? throw new InvalidOperationException("The editor is not attached to its store.");

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Id is not string currentId || await Store().ReadAsync(currentId, cancellationToken).ConfigureAwait(false) is not T kept)
            return;

        T updated = Draft.Apply(kept);

        if (_save is not null)
            Id = await _save(currentId, updated, cancellationToken).ConfigureAwait(false);
        else
            await Store().WriteAsync(currentId, updated, cancellationToken).ConfigureAwait(false);
    }

    /// <remarks>For a copy <paramref name="name"/>'s <c>{name}</c> is the original's name; for a new one it is dropped.</remarks>
    public async Task CreateAsync(bool copy, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        T? original = copy && Id is not null ? await Store().ReadAsync(Id, cancellationToken).ConfigureAwait(false) : null;
        T document = original is null ? _blank() with { Name = name.Replace("{name}", string.Empty, StringComparison.Ordinal).Trim() } : original with { Name = name.Replace("{name}", original.Name, StringComparison.Ordinal) };

        Id = await Store().CreateAsync(document, cancellationToken).ConfigureAwait(false);
    }

    public void Rename(string name)
        => Draft.Name = name;

    public void Delete()
    {
        if (Id is not null)
            _ = Store().Delete(Id);
    }

    public void Choose(string? id)
    {
        RecursiveCollection<OptionItem> options = _options();

        Id = id is not null && Store().Exists(id) ? id : (options.Count == 0 ? null : options[0].Id);
    }
}
