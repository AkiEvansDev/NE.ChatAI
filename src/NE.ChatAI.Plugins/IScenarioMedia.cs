using System;
using System.Threading;
using System.Threading.Tasks;

namespace NE.ChatAI.Plugins;

/// <summary>Stores images produced by scenario nodes so downstream visual or display pins can render and persist them.</summary>
public interface IScenarioMedia
{
    /// <summary>Saves <paramref name="content"/> into the chat's media folder (or a preview buffer on <c>/scenarios</c>) and returns its served URL.</summary>
    Task<string> SaveImageAsync(ReadOnlyMemory<byte> content, string? contentType = null, string? preferredName = null, CancellationToken cancellationToken = default);
}
