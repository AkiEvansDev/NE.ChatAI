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

    /// <summary>Which chat the home page shows.</summary>
    public const string ChatParameter = "chat";

    /// <summary>Which scenario the scenarios page shows.</summary>
    public const string CardParameter = "id";

    public static string ChatOf(string chatId)
        => $"{Home}?{ChatParameter}={Uri.EscapeDataString(chatId)}";

    public static string ScenarioOf(string scenarioId)
        => $"{Scenarios}?{CardParameter}={Uri.EscapeDataString(scenarioId)}";
}
