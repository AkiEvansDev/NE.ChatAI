using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using NE.ChatAI.Characters;
using NE.ChatAI.Chats;
using NE.ChatAI.Prompts;

namespace NE.ChatAI.Scenarios;

/// <summary>Whether a scenario's model call thinks before answering.</summary>
internal enum ScenarioThinking
{
    [Description("From profile")]
    FromProfile,
    On,
    Off
}

/// <summary>The chat's persona handed out on a node sheet.</summary>
internal sealed record PersonaCard(Persona Persona, string Name)
{
    public override string ToString()
        => string.IsNullOrWhiteSpace(Persona.Description) ? Name : $"{Name}\n{Persona.Description}";
}

/// <summary>The chat's character handed out on a node sheet.</summary>
internal sealed record CharacterCard(Character Character, string Name)
{
    public string CardText { get; } = BuildCardText(Character);

    private static string BuildCardText(Character character)
    {
        StringBuilder builder = new();
        AppendLine(builder, character.Description);
        AppendLine(builder, string.IsNullOrWhiteSpace(character.Personality) ? null : $"{character.Name}'s personality: {character.Personality}");
        AppendLine(builder, string.IsNullOrWhiteSpace(character.Scenario) ? null : $"Scenario: {character.Scenario}");
        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (builder.Length > 0)
            _ = builder.Append('\n');

        _ = builder.Append(text.Trim());
    }

    public override string ToString()
        => CardText.Length == 0 ? Name : CardText;
}

/// <summary>The chat's world and its enabled articles handed out on a node sheet.</summary>
internal sealed record WorldLore(World World, IReadOnlyList<WorldArticle> Articles)
{
    public override string ToString()
    {
        StringBuilder builder = new();

        for (var i = 0; i < Articles.Count; i++)
        {
            var text = Articles[i].Text.Trim();

            if (text.Length == 0)
                continue;

            if (builder.Length > 0)
                _ = builder.Append('\n');

            _ = builder.Append(text);
        }

        return builder.ToString();
    }
}

/// <summary>A slice of the chat's history handed out by <see cref="MessagesNode"/>, carrying its underlying message range.</summary>
internal sealed record MessageSlice(IReadOnlyList<PromptMessage> Messages, string? FirstMessageId, string? LastMessageId, string Text)
{
    public static MessageSlice Empty { get; } = new([], null, null, string.Empty);

    public static MessageSlice From(ChatHistorySlice slice, string characterName, string userName)
    {
        ArgumentNullException.ThrowIfNull(slice);

        if (slice.Messages.Count == 0)
            return Empty;

        StringBuilder builder = new();

        for (var i = 0; i < slice.Messages.Count; i++)
        {
            PromptMessage message = slice.Messages[i];

            if (builder.Length > 0)
                _ = builder.Append('\n');

            var speaker = message.Role switch
            {
                MessageRole.User => userName,
                MessageRole.Character => characterName,
                _ => "System"
            };

            _ = builder.Append(speaker).Append(": ").Append(message.Text);
        }

        return new MessageSlice(slice.Messages, slice.FirstMessageId, slice.LastMessageId, builder.ToString());
    }

    public override string ToString()
        => Text;
}

/// <summary>Prompt blocks assembled by <see cref="CollectorNode"/> for a downstream <see cref="ModelCallNode"/>.</summary>
internal sealed record CollectedPrompt(PromptSpec Spec)
{
    public override string ToString()
        => $"{Spec.History.Count} messages";
}
