using System;
using System.Collections.Generic;

namespace NE.ChatAI.Plugins;

/// <summary>
/// Entry point implemented by a plugin assembly to contribute custom <see cref="IGraphNode"/> or <see cref="IGraphNodeAsync"/>
/// types to the scenario catalogue.
/// </summary>
public interface IChatPlugin
{
    /// <summary>Gets the plugin's unique slug identifier, matching <c>id</c> in <c>plugin.json</c>.</summary>
    string Id { get; }

    /// <summary>Gets the custom node types this plugin registers on the scenario canvas.</summary>
    IReadOnlyList<Type> NodeTypes { get; }
}
