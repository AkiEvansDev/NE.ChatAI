using System;

namespace NE.ChatAI;

/// <summary>The application's own configuration, under <see cref="Section"/>.</summary>
public sealed class ChatAIOptions
{
    public const string Section = "ChatAI";

    /// <summary>Where characters, personas, presets and chats are kept; relative to the host's content root.</summary>
    public string DataPath { get; set; } = "data";

    /// <summary>The koboldcpp server the first connection is made for, on a data folder that has none yet.</summary>
    public Uri KoboldCppAddress { get; set; } = new("http://localhost:5001/");
}
