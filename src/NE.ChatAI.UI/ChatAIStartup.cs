using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NE.ChatAI.Chats;
using NE.ChatAI.Controllers;
using NE.ChatAI.Hosting;
using NE.ChatAI.Localization;
using NE.ChatAI.Plugins;
using NE.ChatAI.Secrets;
using NE.ChatAI.Storage;
using NE.ChatAI.Views;
using NE.Colors;

namespace NE.ChatAI;

/// <summary>The application as the UI framework sees it, whatever the platform: its services, its routes and its words.</summary>
internal sealed class ChatAIStartup : UIStartupBase
{
    private string? _logsDirectory;

    protected override void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _logsDirectory = ResolveLogsDirectory(services);
        _ = services.AddOptions<ChatAIOptions>().BindConfiguration(ChatAIOptions.Section);

        _ = services.AddSingleton(static provider =>
        {
            ChatAIOptions options = provider.GetRequiredService<IOptions<ChatAIOptions>>().Value;

            return new DataFolder(Path.Combine(provider.GetRequiredService<IHostEnvironment>().ContentRootPath, options.DataPath));
        });

        // The keys stay in the user's profile, out of the data folder: a copy of the folder carries no usable secret.
        _ = services.AddDataProtection().SetApplicationName("NE.ChatAI");
        _ = services.AddSingleton(static provider =>
        {
            IConfiguration configuration = provider.GetRequiredService<IConfiguration>();

            return new SecretStore(
                Path.Combine(provider.GetRequiredService<DataFolder>().Root, "secrets.json"),
                provider.GetRequiredService<IDataProtectionProvider>(),
                name => configuration[$"{ChatAIOptions.Section}:Secrets:{name}"],
                () => ConfiguredSecretNames(configuration)
            );
        });

        // A reply streams for as long as the model writes it; the generation's own cancellation ends it, not a client timeout.
        // koboldcpp is an HTTP/1.0 server that closes after each answer, and unreachable probes are handled by the controller.
        _ = services.AddHttpClient(KoboldCppClients.HttpClientName, static http =>
            {
                http.Timeout = Timeout.InfiniteTimeSpan;
                http.DefaultRequestHeaders.ConnectionClose = true;
            })
            .RemoveAllLoggers();
        _ = services.AddSingleton<KoboldCppClients>();

        _ = services.AddHttpClient(PluginManager.HttpClientName, static http =>
            {
                http.BaseAddress = new Uri("https://api.github.com/");
                http.Timeout = TimeSpan.FromSeconds(30);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NE.ChatAI/1.0");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            })
            .RemoveAllLoggers();
        _ = services.AddSingleton<PluginManager>();

        _ = services.AddSingleton(TimeProvider.System);
        _ = services.AddSingleton<ChatLibrary>();
        _ = services.AddSingleton<PictureContent>();
        _ = services.AddSingleton<IUIContentProvider>(static provider => provider.GetRequiredService<PictureContent>());
        _ = services.AddSingleton<PinAttemptThrottle>();
        _ = services.AddSingleton<UserSessionResolver>();
        _ = services.AddSingleton<IUserSessionResolver>(static provider => provider.GetRequiredService<UserSessionResolver>());

        _ = services.AddHostedService<FirstStart>();
    }

    private static string ResolveLogsDirectory(IServiceCollection services)
    {
        IConfiguration? configuration = null;
        IHostEnvironment? environment = null;

        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IConfiguration) && descriptor.ImplementationInstance is IConfiguration config)
                configuration = config;
            else if (descriptor.ServiceType == typeof(IHostEnvironment) && descriptor.ImplementationInstance is IHostEnvironment env)
                environment = env;
        }

        var configured = configuration?[$"{ChatAIOptions.Section}:{nameof(ChatAIOptions.DataPath)}"];
        var dataPath = string.IsNullOrWhiteSpace(configured) ? DefaultDataPath() : configured;
        var contentRoot = environment?.ContentRootPath ?? Directory.GetCurrentDirectory();

        return Path.GetFullPath(Path.Combine(contentRoot, dataPath, "logs"));
    }

    private static string DefaultDataPath()
    {
        var env = Environment.GetEnvironmentVariable($"{ChatAIOptions.Section}__{nameof(ChatAIOptions.DataPath)}");

        return string.IsNullOrWhiteSpace(env) ? new ChatAIOptions().DataPath : env;
    }

    private static IEnumerable<string> ConfiguredSecretNames(IConfiguration configuration)
    {
        foreach (IConfigurationSection child in configuration.GetSection($"{ChatAIOptions.Section}:Secrets").GetChildren())
        {
            if (!string.IsNullOrEmpty(child.Value) && Slug.IsValidId(child.Key))
                yield return child.Key;
        }
    }

    protected override void ConfigureApplication(UIApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);

        _ = application.ConfigurePersistence(static persistence => persistence.Lifetime = UIRuntimeLifetime.PerClient);
        _ = application.ConfigureSessions(static sessions =>
        {
            sessions.ClientKey = UserSessionResolver.CookieName;
            sessions.ClientKeyLifetime = UserSessionResolver.CookieLifetime;
            sessions.IdleTimeout = UserSessionResolver.CookieLifetime;
        });
        _ = application.ConfigureSecurity(static security =>
        {
            security.DefaultPolicy = UIAuthorizationDefault.Authenticated;
            security.RecheckRouteOnActivity = true;
        });

        _ = application.ConfigureLocalization(static localization =>
        {
            localization.DefaultLanguage = Words.DefaultLanguage;
            localization.KeyPrefixes.Add(Words.KeyPrefix);
        });

        _ = application.AddLocalizationSource(Words.Tables());
        _ = application.AddFrameworkWords("ru");

        _ = application.ConfigureTheme(static theme => theme
            .ConfigureDarkPalette(static palette => palette with { Series = SeriesOf(palette, firstTint: 2, secondTint: 4, thirdTint: 6) })
            .ConfigureLightPalette(static palette => palette with { Series = SeriesOf(palette, firstTint: 0, secondTint: 2, thirdTint: 4) })
        );

        _ = application.ConfigureFiles(static files => files.MaxFileSize = SettingsController.MaxImportBytes);

        // Kept inside the data folder so launcher updates that replace app/ preserve past logs.
        _ = application.ConfigureLogging(logging => logging.UseFile(
            _logsDirectory ?? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), DefaultDataPath(), "logs")),
            static file => file.MinimumLevel = LogLevel.Warning
        ));

        _ = application.SignInView<PinView, PinController>(AppRoutes.Pin);
        _ = application.Route<ChatView, ChatController>(AppRoutes.Home);
        _ = application.Route<SettingsView, SettingsController>(AppRoutes.Settings);
        _ = application.Route<WorldsView, WorldsController>(AppRoutes.Worlds);
        _ = application.Route<ScenarioView, ScenarioController>(AppRoutes.Scenarios);
        _ = application.Route<PluginsView, PluginsController>(AppRoutes.Plugins);
    }

    /// <summary>
    /// Twelve chart series colours drawn from the palette's five semantic hues and their clean pastel tints, avoiding the default wheel's
    /// yellow-olive second slot.
    /// </summary>
    private static ColorVariant[] SeriesOf(UIColorPalette palette, int firstTint, int secondTint, int thirdTint)
    {
        ColorAdjustment first = firstTint == 0 ? ColorAdjustment.None : ColorAdjustment.Tint;

        return
        [
            new ColorVariant(palette.Primary.Name, first, firstTint),
            new ColorVariant(palette.Accent.Name, first, firstTint),
            new ColorVariant(palette.Info.Name, first, firstTint),
            new ColorVariant(palette.Success.Name, first, firstTint),
            new ColorVariant(palette.Danger.Name, first, firstTint),
            new ColorVariant(palette.Primary.Name, ColorAdjustment.Tint, secondTint),
            new ColorVariant(palette.Accent.Name, ColorAdjustment.Tint, secondTint),
            new ColorVariant(palette.Info.Name, ColorAdjustment.Tint, secondTint),
            new ColorVariant(palette.Success.Name, ColorAdjustment.Tint, secondTint),
            new ColorVariant(palette.Danger.Name, ColorAdjustment.Tint, secondTint),
            new ColorVariant(palette.Primary.Name, ColorAdjustment.Tint, thirdTint),
            new ColorVariant(palette.Accent.Name, ColorAdjustment.Tint, thirdTint)
        ];
    }
}
