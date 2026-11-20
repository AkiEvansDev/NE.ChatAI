using System;
using System.Collections.Generic;

namespace NE.ChatAI.Plugins;

/// <summary>Contributes custom <see cref="IGraphNode"/> or <see cref="IGraphNodeAsync"/> types from a plugin assembly.</summary>
public interface IChatPlugin
{
    /// <summary>Unique slug identifier matching <c>id</c> in <c>plugin.json</c>.</summary>
    string Id { get; }

    /// <summary>Custom node types registered on the scenario canvas.</summary>
    IReadOnlyList<Type> NodeTypes { get; }
}
