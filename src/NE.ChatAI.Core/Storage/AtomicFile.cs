using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NE.ChatAI.Storage;

/// <summary>A file written whole or not at all: to a temporary name beside it, then moved over it.</summary>
public static class AtomicFile
{
    public const string TemporaryExtension = ".tmp";

    /// <summary>Reads <paramref name="path"/> while allowing concurrent atomic moves or deletes on Windows.</summary>
    public static async Task<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);

        await using (stream.ConfigureAwait(false))
        {
            var length = stream.Length;
            var buffer = new byte[checked((int)length)];
            var offset = 0;

            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                    return buffer[..offset];

                offset += read;
            }

            return buffer;
        }
    }

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

    /// <summary>Copies <paramref name="sourcePath"/> to <paramref name="targetPath"/> atomically via a temporary sibling file.</summary>
    public static void Copy(string sourcePath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(targetPath);

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            return;

        var directory = Path.GetDirectoryName(targetPath);

        if (!string.IsNullOrEmpty(directory))
            _ = Directory.CreateDirectory(directory);

        var temporary = $"{targetPath}.{Guid.NewGuid():N}{TemporaryExtension}";

        try
        {
            File.Copy(sourcePath, temporary, overwrite: true);
            File.Move(temporary, targetPath, overwrite: true);
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

    /// <summary>Deletes unfinished temporary files left behind under <paramref name="directory"/> after a crash.</summary>
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
