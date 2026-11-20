using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Characters;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Chats;

/// <summary>A chat as a list shows it: its name, its character, the character's tags, and its last message.</summary>
public sealed record ChatSummary(string Id, string Name, string? CharacterId, string CharacterName, IReadOnlyList<string> Tags, DateTimeOffset LastAt, string LastText);

/// <summary>The chats in the data folder: listed newest first, started with a character, and their messages kept.</summary>
public sealed class ChatLibrary(DataFolder data, TimeProvider time)
{
    /// <summary>Every chat, the one last written to first, with its character's name and tags.</summary>
    public async Task<IReadOnlyList<ChatSummary>> ListAsync(CancellationToken cancellationToken)
        => await ListAsync((await data.Characters.ListAsync(cancellationToken).ConfigureAwait(false)).Items, cancellationToken).ConfigureAwait(false);

    /// <summary>Every chat, the one last written to first, paired with an already-read character list.</summary>
    public async Task<IReadOnlyList<ChatSummary>> ListAsync(IReadOnlyList<StoredDocument<Character>> characters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(characters);

        DocumentListing<Chat> chats = await data.Chats.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, Character> byId = characters.ToDictionary(static stored => stored.Id, static stored => stored.Document, StringComparer.Ordinal);

        return
        [
            .. chats.Items
                .Select(stored => Summarize(stored.Id, stored.Document, stored.Document.Character is string id ? byId.GetValueOrDefault(id) : null))
                .OrderByDescending(static summary => summary.LastAt)
        ];
    }

    private static ChatSummary Summarize(string id, Chat chat, Character? character)
    {
        ChatMessage? last = chat.Messages.Count == 0 ? null : chat.Messages[^1];

        return new ChatSummary(
            id,
            chat.Name,
            chat.Character,
            character?.Name ?? string.Empty,
            character?.Tags ?? [],
            last?.At ?? chat.Created ?? DateTimeOffset.MinValue,
            last?.Current.Text ?? string.Empty
        );
    }

    /// <summary>Starts a new chat for <paramref name="characterId"/> seeded with the character's non-empty greetings as variants.</summary>
    public async Task<string?> StartAsync(string characterId, string? personaId, CancellationToken cancellationToken)
    {
        if (await data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) is not Character character)
            return null;

        DateTimeOffset now = time.GetUtcNow();
        List<ChatMessage> messages = [];
        List<MessageVariant> greetings = [.. character.FirstMessages.Where(static text => !string.IsNullOrWhiteSpace(text)).Select(static text => new MessageVariant { Text = text })];

        if (greetings.Count > 0)
            messages.Add(new ChatMessage { Id = NewMessageId(), Role = MessageRole.Character, Variants = greetings, At = now });

        Chat chat = new()
        {
            Name = $"{character.Name} {now.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}",
            Character = characterId,
            Persona = personaId,
            Created = greetings.Count == 0 ? now : null,
            Messages = messages
        };

        return await data.Chats.CreateAsync(chat, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Branches <paramref name="chat"/> up to <paramref name="messageId"/> into a new standalone chat document, copying its background and referenced media.</summary>
    public async Task<string?> BranchAsync(Chat chat, string messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);

        if (!chat.Messages.Any(message => message.Id == messageId))
            return null;

        Character? character = chat.Character is string characterId ? await data.Characters.ReadAsync(characterId, cancellationToken).ConfigureAwait(false) : null;
        var name = $"{character?.Name ?? chat.Name} {time.GetUtcNow().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
        Chat branched = ChatEditing.UpTo(chat, messageId) with { Name = name, Background = null };
        var branchedId = await data.Chats.CreateAsync(branched, cancellationToken).ConfigureAwait(false);
        var copiedBackground = ChatPictures.Copy(data, chat.Background, branchedId);
        IReadOnlyList<ChatVisual> copiedVisuals = GalleryPictures.CopyReferencedChatMedia(data, branched.Visuals, branchedId);

        if (copiedBackground is not null || !ReferenceEquals(copiedVisuals, branched.Visuals))
            await data.Chats.WriteAsync(branchedId, branched with { Background = copiedBackground, Visuals = copiedVisuals }, cancellationToken).ConfigureAwait(false);

        return branchedId;
    }

    public Task<Chat?> ReadAsync(string chatId, CancellationToken cancellationToken)
        => data.Chats.ReadAsync(chatId, cancellationToken);

    public Task WriteAsync(string chatId, Chat chat, CancellationToken cancellationToken)
        => data.Chats.WriteAsync(chatId, chat, cancellationToken);

    /// <summary>Deletes a single chat by its id.</summary>
    public bool Delete(string chatId)
    {
        if (!Slug.IsValidId(chatId) || !data.Chats.Delete(chatId))
            return false;

        ChatPictures.Delete(data, chatId);
        GalleryPictures.DeleteChatMedia(data, chatId);
        return true;
    }

    /// <summary>Deletes the character and every chat with it: a chat without its character has no place in the list to be found by.</summary>
    public async Task<bool> DeleteCharacterAsync(string characterId, CancellationToken cancellationToken)
    {
        if (!Slug.IsValidId(characterId) || !data.Characters.Exists(characterId))
            return false;

        DocumentListing<Chat> chats = await data.Chats.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (StoredDocument<Chat> chat in chats.Items)
        {
            if (chat.Document.Character == characterId)
                _ = Delete(chat.Id);
        }

        return data.Characters.Delete(characterId);
    }

    /// <summary>A message's id: sequential in time, so a chat's messages sort as they were written.</summary>
    public static string NewMessageId()
        => Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
}
