using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Chats;

/// <summary>A chat's own background picture kept beside its <c>.json</c> file in <c>chats/</c> under the chat's id.</summary>
public static class ChatPictures
{
    private const string ChatsFolderName = "chats";

    /// <summary>The chat's background picture file, or <c>null</c> when the chat has none or does not exist.</summary>
    public static string? PathOf(DataFolder data, string chatId)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Slug.IsValidId(chatId) && data.Chats.Exists(chatId) ? PictureFiles.Find(FolderOf(data), chatId) : null;
    }

    private static string FolderOf(DataFolder data)
        => Path.Combine(data.Root, ChatsFolderName);

    /// <summary>Keeps <paramref name="picture"/> as the chat's background picture and returns its file name.</summary>
    public static async Task<string> WriteAsync(DataFolder data, string chatId, ReadOnlyMemory<byte> picture, string? contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Slug.IsValidId(chatId))
            throw new ArgumentException($"'{chatId}' is not a chat id.", nameof(chatId));

        var folder = FolderOf(data);
        _ = Directory.CreateDirectory(folder);
        await PictureFiles.WriteAsync(folder, chatId, picture, contentType, cancellationToken).ConfigureAwait(false);

        return Path.GetFileName(PictureFiles.Find(folder, chatId)!);
    }

    /// <summary>Copies a source chat's background file to <paramref name="targetChatId"/> when present and returns the new file name.</summary>
    public static string? Copy(DataFolder data, string? sourceBackgroundFile, string targetChatId)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (string.IsNullOrWhiteSpace(sourceBackgroundFile) || !Slug.IsValidId(targetChatId))
            return null;

        var sourceId = Path.GetFileNameWithoutExtension(sourceBackgroundFile);

        if (!Slug.IsValidId(sourceId) || PictureFiles.Find(FolderOf(data), sourceId) is not string sourcePath)
            return null;

        var folder = FolderOf(data);
        var fileName = targetChatId + Path.GetExtension(sourcePath).ToLowerInvariant();
        PictureFiles.Delete(folder, targetChatId);
        AtomicFile.Copy(sourcePath, Path.Combine(folder, fileName));

        return fileName;
    }

    /// <summary>Removes the chat's background picture, whatever its extension.</summary>
    public static void Delete(DataFolder data, string chatId)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (Slug.IsValidId(chatId))
            PictureFiles.Delete(FolderOf(data), chatId);
    }
}
