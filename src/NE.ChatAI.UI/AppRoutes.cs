using System;

namespace NE.ChatAI;

/// <summary>Every address of the application, declared once so the registration and the links agree.</summary>
internal static class AppRoutes
{
    /// <summary>The chat itself: the page the application is used on.</summary>
    public const string Home = "/";

    public const string Settings = "/settings";

    /// <summary>The node-sheet editor for auxiliary scenarios.</summary>
    public const string Scenarios = "/scenarios";

    /// <summary>Installed scenario plugins and updates from GitHub releases.</summary>
    public const string Plugins = "/plugins";

    /// <summary>Layered graph of personas, characters and the worlds they are bound to.</summary>
    public const string Worlds = "/worlds";

    /// <summary>The PIN unlock page shown when a network PIN is configured.</summary>
    public const string Pin = "/pin";

    /// <summary>Which chat the home page shows.</summary>
    public const string ChatParameter = "chat";

    /// <summary>Which tab the settings page shows.</summary>
    public const string TabParameter = "tab";

    /// <summary>Which scenario the scenarios page shows.</summary>
    public const string CardParameter = "id";

    public static string ChatOf(string chatId)
        => $"{Home}?{ChatParameter}={Uri.EscapeDataString(chatId)}";

    public static string ScenarioOf(string scenarioId)
        => $"{Scenarios}?{CardParameter}={Uri.EscapeDataString(scenarioId)}";
}
