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

/// <summary>File- or folder-backed store for one kind of <see cref="NamedDocument"/> inside <see cref="DataFolder"/>.</summary>
public sealed class DocumentStore<T> where T : NamedDocument
{
    private sealed record CachedDocument(long LastWriteTicks, long Length, T Document);

    private const string Extension = ".json";

    private readonly string _directory;
    private readonly string? _fileName;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writes = new(StringComparer.Ordinal);
    private readonly Lock _cacheLock = new();
    private readonly Lock _idReservationLock = new();
    private readonly Dictionary<string, CachedDocument> _cache = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _reservedIds = [with(StringComparer.Ordinal)];
    private long _writeGeneration;

    /// <summary>Creates a store rooted at <paramref name="directory"/>, keeping one file or one subfolder per document.</summary>
    public DocumentStore(string directory, string? fileName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        _directory = directory;
        _fileName = fileName;
    }

    /// <summary>Lists all readable documents sorted by display name alongside any unreadable document ids.</summary>
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
        FileInfo info = new(path);

        if (!info.Exists)
        {
            InvalidateCached(id);
            return null;
        }

        var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        var length = info.Length;

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(id, out CachedDocument? cached) && cached.LastWriteTicks == lastWriteTicks && cached.Length == length)
                return cached.Document;
        }

        var generation = Volatile.Read(ref _writeGeneration);
        byte[] content;

        try
        {
            content = await AtomicFile.ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            InvalidateCached(id);
            return null;
        }

        T document;

        try
        {
            document = DocumentJson.Read<T>(content, path);
        }
        catch (DocumentFormatException)
        {
            InvalidateCached(id);
            throw;
        }

        lock (_cacheLock)
        {
            if (_writeGeneration == generation && content.LongLength == length)
                _cache[id] = new CachedDocument(lastWriteTicks, length, document);
        }

        return document;
    }

    private void InvalidateCached(string id)
    {
        lock (_cacheLock)
            _ = _cache.Remove(id);
    }

    /// <summary>The folder a document keeps its files in (its avatar, its source card); only for a kind kept as folders.</summary>
    public string FolderOf(string id)
    {
        if (_fileName is null)
            throw new InvalidOperationException("This kind keeps a file per document, not a folder.");

        return Path.Combine(_directory, Checked(id));
    }

    private string PathOf(string id)
        => _fileName is null ? Path.Combine(_directory, Checked(id) + Extension) : Path.Combine(FolderOf(id), _fileName + Extension);

    private static string Checked(string id)
        => Slug.IsValidId(id) ? id : throw new ArgumentException($"'{id}' is not a document id.", nameof(id));

    /// <summary>Keeps a new document under a new id made from its name, and answers the id.</summary>
    public async Task<string> CreateAsync(T document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var id = ReserveTargetId(Slug.NewId(document.Name), document.Name);

        try
        {
            await WriteAsync(id, document, cancellationToken).ConfigureAwait(false);
            return id;
        }
        finally
        {
            ReleaseReservedId(id);
        }
    }

    private string ReserveTargetId(string preferredId, string name)
    {
        lock (_idReservationLock)
        {
            var candidate = preferredId;

            while (IsSlotOccupied(candidate) || !_reservedIds.Add(candidate))
                candidate = Slug.NewId(name);

            return candidate;
        }
    }

    private bool IsSlotOccupied(string id)
        => Exists(id) || (_fileName is not null && Directory.Exists(FolderOf(id)));

    private void ReleaseReservedId(string id)
    {
        lock (_idReservationLock)
            _ = _reservedIds.Remove(id);
    }

    /// <summary>Whether a document file exists under <paramref name="id"/>.</summary>
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
            _ = Interlocked.Increment(ref _writeGeneration);
            await AtomicFile.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);

            lock (_cacheLock)
            {
                _ = Interlocked.Increment(ref _writeGeneration);
                _ = _cache.Remove(id);
            }
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

        var preferredId = Slug.RenamedId(Checked(id), document.Name);

        if (string.Equals(preferredId, id, StringComparison.Ordinal))
        {
            await WriteAsync(id, document, cancellationToken).ConfigureAwait(false);
            return id;
        }

        var targetId = ReserveTargetId(preferredId, document.Name);
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
                    var content = JsonSerializer.SerializeToUtf8Bytes(document with { Format = Document.CurrentFormat }, DocumentJson.Options);
                    _ = Interlocked.Increment(ref _writeGeneration);
                    await AtomicFile.WriteAsync(PathOf(id), content, cancellationToken).ConfigureAwait(false);
                    Directory.Move(oldFolder, newFolder);
                }
                else
                {
                    await WriteAsync(targetId, document, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (_cacheLock)
            {
                _ = Interlocked.Increment(ref _writeGeneration);
                _ = _cache.Remove(id);
                _ = _cache.Remove(targetId);
            }

            _ = oldGate.Release();
            ReleaseReservedId(targetId);
        }

        return targetId;
    }

    /// <summary>Removes the document — with its folder and everything in it, for a kind kept as folders.</summary>
    public bool Delete(string id)
    {
        SemaphoreSlim gate = _writes.GetOrAdd(Checked(id), static _ => new SemaphoreSlim(1, 1));
        gate.Wait();

        try
        {
            var fileExists = Exists(id);
            var folderExists = _fileName is not null && Directory.Exists(FolderOf(id));

            if (!fileExists && !folderExists)
                return false;

            _ = Interlocked.Increment(ref _writeGeneration);

            if (_fileName is null)
                File.Delete(PathOf(id));
            else
                Directory.Delete(FolderOf(id), recursive: true);

            lock (_cacheLock)
            {
                _ = Interlocked.Increment(ref _writeGeneration);
                _ = _cache.Remove(id);
            }

            return fileExists;
        }
        finally
        {
            _ = gate.Release();
        }
    }

    internal void ClearCache()
    {
        lock (_cacheLock)
        {
            _ = Interlocked.Increment(ref _writeGeneration);
            _cache.Clear();
        }
    }
}
