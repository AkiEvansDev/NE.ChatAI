using System;
using System.Net.Http;

namespace NE.ChatAI.Plugins;

/// <summary>Typed helpers for resolving scenario services from <see cref="UINodeRunContext"/> inside plugin nodes.</summary>
public static class ScenarioNodeExtensions
{
    private static readonly HttpClient FallbackHttp = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) }, disposeHandler: false);

    /// <summary>Resolves the active <see cref="IScenarioContext"/> for <paramref name="context"/>.</summary>
    public static IScenarioContext GetScenarioContext(this UINodeRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Services?.GetService(typeof(IScenarioContext)) as IScenarioContext
            ?? throw new UINodeFailureException("Scenario context is only available inside a chat scenario.");
    }

    /// <summary>Resolves <see cref="IScenarioSecrets"/> for <paramref name="context"/>.</summary>
    public static IScenarioSecrets GetScenarioSecrets(this UINodeRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Services?.GetService(typeof(IScenarioSecrets)) as IScenarioSecrets
            ?? throw new UINodeFailureException("Scenario secrets are not available in this run.");
    }

    /// <summary>Resolves <see cref="IScenarioMedia"/> for <paramref name="context"/>.</summary>
    public static IScenarioMedia GetScenarioMedia(this UINodeRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Services?.GetService(typeof(IScenarioMedia)) as IScenarioMedia
            ?? throw new UINodeFailureException("Scenario media storage is not available in this run.");
    }

    /// <summary>Creates or retrieves a pooled <see cref="HttpClient"/> from the host without exhausting sockets.</summary>
    public static HttpClient CreateHttpClient(this UINodeRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Services?.GetService(typeof(HttpClient)) as HttpClient ?? FallbackHttp;
    }
}
