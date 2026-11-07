using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NE.ChatAI.Storage;

/// <summary>A file written whole or not at all: to a temporary name beside it, then moved over it.</summary>
public static class AtomicFile
{
    public const string TemporaryExtension = ".tmp";

    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
            _ = Directory.CreateDirectory(directory);

        var temporary = $"{path}.{Guid.NewGuid():N}{TemporaryExtension}";

        try
        {
            FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);

            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Deletes what a crash left behind under <paramref name="directory"/>: temporary files nothing will finish.</summary>
    public static void SweepTemporaries(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var temporary in Directory.EnumerateFiles(directory, "*" + TemporaryExtension, SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Being written right now: the next sweep takes it if it is still there.
            }
        }
    }
}
