using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NE.ChatAI.Storage;

/// <summary>A document as it is kept: its id — its file or folder name — and its content.</summary>
public sealed record StoredDocument<T>(string Id, T Document);

/// <summary>Every document of a kind that could be read, and the ids of those that could not.</summary>
public sealed record DocumentListing<T>(IReadOnlyList<StoredDocument<T>> Items, IReadOnlyList<string> Unreadable);

/// <summary>A file of a newer format than this version writes, or one that is not a document of its kind.</summary>
public sealed class DocumentFormatException : Exception
{
    public DocumentFormatException()
    {
    }

    public DocumentFormatException(string message) : base(message)
    {
    }

    public DocumentFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public DocumentFormatException(string path, string message, Exception? innerException = null) : base($"{path}: {message}", innerException)
    {
        Path = path;
    }

    /// <summary>The file that could not be read.</summary>
    public string? Path { get; }
}

/// <summary>
/// One kind of document under the data folder: a file per document (<c>prompts/&lt;id&gt;.json</c>) or a folder per document
/// holding its file and whatever goes with it (<c>characters/&lt;id&gt;/character.json</c>, its avatar beside it).
/// </summary>
public sealed class DocumentStore<T> where T : NamedDocument
{
    private const string Extension = ".json";

    private readonly string _directory;
    private readonly string? _fileName;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writes = new(StringComparer.Ordinal);

    /// <param name="directory">The kind's folder.</param>
    /// <param name="fileName">The document's file inside its own folder, or <c>null</c> for a file per document.</param>
    public DocumentStore(string directory, string? fileName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        _directory = directory;
        _fileName = fileName;
    }

    public async Task<DocumentListing<T>> ListAsync(CancellationToken cancellationToken)
    {
        List<StoredDocument<T>> items = [];
        List<string> unreadable = [];

        if (!Directory.Exists(_directory))
            return new DocumentListing<T>(items, unreadable);

        foreach (var id in EnumerateIds())
        {
            try
            {
                if (await ReadAsync(id, cancellationToken).ConfigureAwait(false) is T document)
                    items.Add(new StoredDocument<T>(id, document));
            }
            catch (DocumentFormatException)
            {
                unreadable.Add(id);
            }
        }

        // By the names a person reads, in their language's order — not by the ids, whose hex digits mean nothing.
        items.Sort(static (left, right) => CultureInfo.CurrentCulture.CompareInfo.Compare(left.Document.Name, right.Document.Name, CompareOptions.IgnoreCase));

        return new DocumentListing<T>(items, unreadable);
    }

    private IEnumerable<string> EnumerateIds()
    {
        if (_fileName is null)
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*" + Extension))
            {
                var id = Path.GetFileNameWithoutExtension(file);

                if (Slug.IsValidId(id))
                    yield return id;
            }

            yield break;
        }

        foreach (var folder in Directory.EnumerateDirectories(_directory))
        {
            var id = Path.GetFileName(folder);

            if (Slug.IsValidId(id) && File.Exists(Path.Combine(folder, _fileName + Extension)))
                yield return id;
        }
    }

    /// <summary>The document under <paramref name="id"/>, or <c>null</c> when there is none.</summary>
    public async Task<T?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        var path = PathOf(id);

        if (!File.Exists(path))
            return null;

        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);

        await using (stream.ConfigureAwait(false))
        {
            T? document;

            try
            {
                document = await JsonSerializer.DeserializeAsync<T>(stream, DocumentJson.Options, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new DocumentFormatException(path, exception.Message, exception);
            }

            if (document is null)
                throw new DocumentFormatException(path, "the file holds no document.");

            if (document.Format > Document.CurrentFormat)
                throw new DocumentFormatException(path, $"format {document.Format} is newer than this version writes ({Document.CurrentFormat}).");

            return document;
        }
    }

    private string PathOf(string id)
        => _fileName is null ? Path.Combine(_directory, Checked(id) + Extension) : Path.Combine(FolderOf(id), _fileName + Extension);

    /// <summary>The folder a document keeps its files in (its avatar, its source card); only for a kind kept as folders.</summary>
    public string FolderOf(string id)
    {
        if (_fileName is null)
            throw new InvalidOperationException("This kind keeps a file per document, not a folder.");

        return Path.Combine(_directory, Checked(id));
    }

    private static string Checked(string id)
        => Slug.IsValidId(id) ? id : throw new ArgumentException($"'{id}' is not a document id.", nameof(id));

    /// <summary>Keeps a new document under a new id made from its name, and answers the id.</summary>
    public async Task<string> CreateAsync(T document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        string id;

        do
            id = Slug.NewId(document.Name);
        while (Exists(id));

        await WriteAsync(id, document, cancellationToken).ConfigureAwait(false);

        return id;
    }

    public bool Exists(string id)
        => File.Exists(PathOf(id));

    /// <summary>Writes <paramref name="document"/> over the one under <paramref name="id"/>, one writer at a time per document.</summary>
    public async Task WriteAsync(string id, T document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var path = PathOf(id);
        var content = JsonSerializer.SerializeToUtf8Bytes(document with { Format = Document.CurrentFormat }, DocumentJson.Options);
        SemaphoreSlim gate = _writes.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await AtomicFile.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <summary>Writes <paramref name="document"/> and renames its file or folder when its slug changes, answering the resulting id.</summary>
    public async Task<string> WriteRenamedAsync(string id, T document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var targetId = Slug.RenamedId(Checked(id), document.Name);

        if (string.Equals(targetId, id, StringComparison.Ordinal))
        {
            await WriteAsync(id, document, cancellationToken).ConfigureAwait(false);

            return id;
        }

        while (Exists(targetId))
            targetId = Slug.NewId(document.Name);

        SemaphoreSlim oldGate = _writes.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await oldGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_fileName is null)
            {
                await WriteAsync(targetId, document, cancellationToken).ConfigureAwait(false);
                var oldPath = PathOf(id);

                if (File.Exists(oldPath))
                    File.Delete(oldPath);
            }
            else
            {
                var oldFolder = FolderOf(id);
                var newFolder = FolderOf(targetId);

                if (Directory.Exists(oldFolder))
                {
                    _ = Directory.CreateDirectory(_directory);
                    Directory.Move(oldFolder, newFolder);
                }

                await WriteAsync(targetId, document, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = oldGate.Release();
            _ = _writes.TryRemove(id, out _);
        }

        return targetId;
    }

    /// <summary>Removes the document — with its folder and everything in it, for a kind kept as folders.</summary>
    public bool Delete(string id)
    {
        if (!Exists(id))
            return false;

        if (_fileName is null)
            File.Delete(PathOf(id));
        else
            Directory.Delete(FolderOf(id), recursive: true);

        _ = _writes.TryRemove(id, out _);

        return true;
    }
}
