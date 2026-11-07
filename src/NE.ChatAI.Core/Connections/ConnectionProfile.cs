using System;
using System.Text.Json.Serialization;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Connections;

/// <summary>Which API a connection talks to.</summary>
public enum BackendKind
{
    /// <summary>koboldcpp's own API: text completion, its tokenizer, its streaming and abort.</summary>
    [JsonStringEnumMemberName("koboldcpp")]
    KoboldCpp,

    /// <summary>OpenRouter's chat completion: the provider applies the model's own turns, so no model format is read.</summary>
    [JsonStringEnumMemberName("openrouter")]
    OpenRouter
}

/// <summary>How long a context is: the server's own when it can tell, or a number.</summary>
public sealed record ContextLength
{
    /// <summary>Ask the server, before each generation, for the context it was started with.</summary>
    public bool Auto { get; init; } = true;

    /// <summary>The context in tokens when <see cref="Auto"/> is off, or the server cannot tell.</summary>
    public int Value { get; init; } = 8192;
}

/// <summary>
/// A server and, by reference, the parts a chat is generated with: switching the connection switches all of them, and one prompt
/// preset serves every model.
/// </summary>
public sealed record ConnectionProfile : NamedDocument
{
    public BackendKind Backend { get; init; } = BackendKind.KoboldCpp;

    public Uri? Address { get; init; }

    /// <summary>The name of the secret holding the API key — never the key itself.</summary>
    public string? Secret { get; init; }

    public ContextLength ContextLength { get; init; } = new();

    /// <summary>The model format's id; read only by a text-completion backend.</summary>
    public string? Instruct { get; init; }

    /// <summary>The system prompt's id.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The prompt preset's id.</summary>
    public string? Prompt { get; init; }

    /// <summary>The sampler preset's id.</summary>
    public string? Samplers { get; init; }
}
