using System;
using System.Threading;
using System.Threading.Tasks;

namespace NE.ChatAI.Plugins;

/// <summary>Stores images produced by scenario nodes so downstream visual or display pins can render and persist them.</summary>
public interface IScenarioMedia
{
    /// <summary>
    /// Saves <paramref name="content"/> into the chat's media folder during live chat runs — or an in-memory preview buffer during
    /// <c>/scenarios</c> canvas runs — and returns an address suitable for <c>[GraphOutput(Image = true)]</c> pins and <c>Set visual</c>.
    /// </summary>
    Task<string> SaveImageAsync(ReadOnlyMemory<byte> content, string? contentType = null, string? preferredName = null, CancellationToken cancellationToken = default);
}
