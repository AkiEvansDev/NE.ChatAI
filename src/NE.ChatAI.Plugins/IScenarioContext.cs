namespace NE.ChatAI.Plugins;

/// <summary>Read-only metadata for the active chat and scenario exposed through <see cref="UINodeRunContext.Services"/>.</summary>
public interface IScenarioContext
{
    /// <summary>Stored identifier of the running scenario.</summary>
    string ScenarioId { get; }

    /// <summary>Display name of the running scenario.</summary>
    string ScenarioName { get; }

    /// <summary>Identifier of the open chat, or <see langword="null"/> when none is open.</summary>
    string? ChatId { get; }

    /// <summary>Identifier of the chat's character, or <see langword="null"/> when unset.</summary>
    string? CharacterId { get; }

    /// <summary>Display name of the chat's character.</summary>
    string CharacterName { get; }

    /// <summary>Display name of the active user persona.</summary>
    string UserName { get; }

    /// <summary>Whether the scenario is running as a non-committing preview on <c>/scenarios</c>.</summary>
    bool IsPreview { get; }

    /// <summary>Composer draft text as it stood when the scenario started.</summary>
    string Draft { get; }
}
