namespace NE.ChatAI.Plugins;

/// <summary>Read-only snapshot of the active chat and scenario exposed through <see cref="UINodeRunContext.Services"/>.</summary>
public interface IScenarioContext
{
    /// <summary>Gets the stored identifier of the running scenario.</summary>
    string ScenarioId { get; }

    /// <summary>Gets the display name of the running scenario.</summary>
    string ScenarioName { get; }

    /// <summary>Gets the identifier of the open chat, or <see langword="null"/> when none is open.</summary>
    string? ChatId { get; }

    /// <summary>Gets the identifier of the chat's character, or <see langword="null"/> when unset.</summary>
    string? CharacterId { get; }

    /// <summary>Gets the display name of the chat's character.</summary>
    string CharacterName { get; }

    /// <summary>Gets the display name of the active user persona.</summary>
    string UserName { get; }

    /// <summary>Gets a value indicating whether the scenario is running as a non-committing preview on <c>/scenarios</c>.</summary>
    bool IsPreview { get; }

    /// <summary>Gets the composer draft text as it stood when the scenario started.</summary>
    string Draft { get; }
}
