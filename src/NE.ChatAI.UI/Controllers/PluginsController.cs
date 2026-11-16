using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Plugins;

namespace NE.ChatAI.Controllers;

/// <summary>One installed plugin row on <c>/plugins</c>.</summary>
internal sealed partial class PluginItem : RecursiveObservable, IBindableItem
{
    [RecursiveMember(false)]
    public required string Id { get; init; }

    [RecursiveMember]
    public partial string Name { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string VersionText { get; set; } = string.Empty;

    [RecursiveMember(false)]
    public RecursiveCollection<BadgeItem> Badges { get; } = [];

    [RecursiveMember]
    public partial string Repository { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string RepositoryUrl { get; set; } = string.Empty;

    [RecursiveMember]
    public partial string Description { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility DescriptionVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial string NodesLine { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility NodesVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial string LoadError { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility ErrorVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial string UpdateTitle { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility UpdateVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial bool Enabled { get; set; } = true;
}

/// <summary>The <c>/plugins</c> page: installing scenario plugins from public GitHub releases, checking and applying hot-updates, and toggling or removing plugins.</summary>
internal sealed partial class PluginsController : ShellController
{
    public const string UninstallDialogKey = "chatai-plugins-uninstall";

    private string? _uninstallingId;

    private PluginManager PluginsManager => Context.Services.GetRequiredService<PluginManager>();

    [RecursiveMember]
    public partial string RepositoryInput { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIPhrase? UninstallQuestion { get; set; }

    [RecursiveMember(false)]
    public RecursiveCollection<PluginItem> Plugins { get; } = [];

    protected override async Task OnAttachedAsync(UINavigationRequest navigation, CancellationToken cancellationToken)
    {
        await base.OnAttachedAsync(navigation, cancellationToken).ConfigureAwait(false);
        RefreshPlugins();
    }

    private void RefreshPlugins()
    {
        IReadOnlyList<PluginStatus> snapshot = PluginsManager.List();
        Plugins.Clear();

        for (var i = 0; i < snapshot.Count; i++)
        {
            PluginStatus status = snapshot[i];
            InstalledPluginRecord record = status.Record;
            var description = record.Description ?? string.Empty;
            var nodesLine = FormatNodesLine(status.Nodes);
            var loadError = status.LoadError ?? string.Empty;
            var hasUpdate = status.HasUpdate;
            var latestVersion = record.LatestVersion ?? record.InstalledVersion;

            var versionText = $"v{record.InstalledVersion}";
            PluginItem row = new()
            {
                Id = record.Id,
                Name = record.Name,
                VersionText = versionText,
                Repository = record.Repository,
                RepositoryUrl = $"https://github.com/{record.Repository}",
                Description = description,
                DescriptionVisibility = description.Length > 0 ? UIVisibility.Visible : UIVisibility.Collapsed,
                NodesLine = nodesLine,
                NodesVisibility = nodesLine.Length > 0 ? UIVisibility.Visible : UIVisibility.Collapsed,
                LoadError = loadError,
                ErrorVisibility = loadError.Length > 0 ? UIVisibility.Visible : UIVisibility.Collapsed,
                UpdateTitle = hasUpdate ? FormatUpdateTitle(latestVersion) : string.Empty,
                UpdateVisibility = hasUpdate ? UIVisibility.Visible : UIVisibility.Collapsed,
                Enabled = record.Enabled
            };

            row.Badges.Add(new BadgeItem
            {
                Id = "__version",
                IsContent = true,
                BadgeTitle = versionText
            });

            for (var t = 0; t < record.Tags.Count; t++)
            {
                var tag = record.Tags[t];
                row.Badges.Add(new BadgeItem
                {
                    Id = $"tag:{tag}",
                    IsContent = true,
                    BadgeTitle = tag,
                    BadgeColor = TagColorOf(tag)
                });
            }

            Plugins.Add(row);
        }
    }

    private string FormatNodesLine(IReadOnlyList<PluginNodeSummary> nodes)
    {
        if (nodes.Count == 0)
            return string.Empty;

        StringBuilder sb = new();
        var prefix = Context.Translate("chatai.plugins.nodes") ?? "Nodes: {nodes}";
        StringBuilder list = new();

        for (var i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
                _ = list.Append(", ");

            _ = list.Append(nodes[i].Title).Append(" (").Append(nodes[i].Key).Append(')');
        }

        _ = sb.Append(prefix.Replace("{nodes}", list.ToString(), StringComparison.Ordinal));

        return sb.ToString();
    }

    private string FormatUpdateTitle(string version)
    {
        var template = Context.Translate("chatai.plugins.update-to") ?? "Update to v{version}";

        return template.Replace("{version}", version, StringComparison.Ordinal);
    }

    private static UIThemeColor TagColorOf(string tag)
        => tag.ToLowerInvariant() switch
        {
            "alpha" or "beta" or "preview" or "experimental" => UIThemeColor.Warning,
            _ => UIThemeColor.Primary
        };

    protected override Task OnLanguageChangedAsync(string language, CancellationToken cancellationToken)
    {
        RefreshPlugins();

        return base.OnLanguageChangedAsync(language, cancellationToken);
    }

    /// <summary>Downloads and installs the latest release from the repository typed into <see cref="RepositoryInput"/>.</summary>
    [UICommand]
    public async Task<UICommandResult> InstallPluginAsync(CancellationToken cancellationToken)
    {
        var input = RepositoryInput.Trim();

        if (input.Length == 0)
            return Notify("chatai.plugins.error.repo", UIColorStyle.Warning);

        PluginOperationResult result = await PluginsManager.InstallAsync(input, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || result.Plugin is null)
            return NotifyError(result);

        RepositoryInput = string.Empty;
        RefreshPlugins();

        return Notify(UIPhrase.Of("chatai.plugins.installed", ("name", result.Plugin.Record.Name), ("version", result.Plugin.Record.InstalledVersion)), UIColorStyle.Success);
    }

    private static UICommandResult NotifyError(PluginOperationResult result)
    {
        var detail = result.Detail ?? string.Empty;

        return result.Error switch
        {
            PluginErrorKind.InvalidRepository => Notify("chatai.plugins.error.repo", UIColorStyle.Warning),
            PluginErrorKind.ReleaseNotFound => Notify(UIPhrase.Of("chatai.plugins.error.no-release", ("repo", detail)), UIColorStyle.Danger),
            PluginErrorKind.AssetMissing => Notify(UIPhrase.Of("chatai.plugins.error.asset-missing", ("detail", detail)), UIColorStyle.Danger),
            PluginErrorKind.ManifestInvalid => Notify(UIPhrase.Of("chatai.plugins.error.manifest", ("detail", detail)), UIColorStyle.Danger),
            PluginErrorKind.VersionIncompatible => Notify(UIPhrase.Of("chatai.plugins.error.incompatible", ("version", detail)), UIColorStyle.Danger),
            PluginErrorKind.DuplicateNodeKey => Notify(UIPhrase.Of("chatai.plugins.error.duplicate-key", ("detail", detail)), UIColorStyle.Danger),
            PluginErrorKind.LoadFailed => Notify(UIPhrase.Of("chatai.plugins.error.load", ("detail", detail)), UIColorStyle.Danger),
            _ => Notify(UIPhrase.Of("chatai.plugins.error.network", ("detail", detail)), UIColorStyle.Danger)
        };
    }

    /// <summary>Queries GitHub Releases for every installed plugin and updates each row's available update button.</summary>
    [UICommand]
    public async Task<UICommandResult> CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        if (Plugins.Count == 0)
            return Notify("chatai.plugins.updates.empty", UIColorStyle.Info);

        PluginUpdateCheckResult check = await PluginsManager.CheckUpdatesAsync(cancellationToken).ConfigureAwait(false);
        RefreshPlugins();

        if (check.UpdatesAvailable > 0)
            return Notify(UIPhrase.Of("chatai.plugins.updates.found", ("count", check.UpdatesAvailable)), UIColorStyle.Info);

        return check.FailedCount > 0
            ? Notify("chatai.plugins.updates.failed", UIColorStyle.Warning)
            : Notify("chatai.plugins.updates.up-to-date", UIColorStyle.Success);
    }

    /// <summary>Hot-updates one installed plugin in place without restarting the host.</summary>
    [UICommand]
    public async Task<UICommandResult> UpdatePluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        PluginOperationResult result = await PluginsManager.UpdateAsync(pluginId, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || result.Plugin is null)
            return NotifyError(result);

        RefreshPlugins();

        return Notify(UIPhrase.Of("chatai.plugins.updated", ("name", result.Plugin.Record.Name), ("version", result.Plugin.Record.InstalledVersion)), UIColorStyle.Success);
    }

    /// <summary>Enables or disables an installed plugin and rebuilds the active scenario catalogue.</summary>
    [UICommand]
    public async Task TogglePluginEnabledAsync(string pluginId, CancellationToken cancellationToken)
    {
        if (!Plugins.TryGet(pluginId, out PluginItem? item))
            return;

        _ = await PluginsManager.SetEnabledAsync(pluginId, item.Enabled, cancellationToken).ConfigureAwait(false);
        RefreshPlugins();
    }

    /// <summary>Opens the confirmation dialog before removing an installed plugin.</summary>
    [UICommand]
    public UICommandResult AskUninstallPlugin(string pluginId)
    {
        if (!Plugins.TryGet(pluginId, out PluginItem? item))
            return UICommandResult.Ok();

        _uninstallingId = pluginId;
        UninstallQuestion = UIPhrase.Of("chatai.plugins.uninstall.question", ("name", item.Name));

        return UICommandResult.Ok([new OpenDialogEffect(UninstallDialogKey)]);
    }

    /// <summary>Unloads and deletes the plugin asked about.</summary>
    [UICommand]
    public async Task<UICommandResult> UninstallPluginAsync(CancellationToken cancellationToken)
    {
        var pluginId = _uninstallingId;
        _uninstallingId = null;

        if (!string.IsNullOrEmpty(pluginId))
        {
            _ = await PluginsManager.UninstallAsync(pluginId, cancellationToken).ConfigureAwait(false);
            RefreshPlugins();
        }

        return UICommandResult.Ok([new CloseDialogEffect(UninstallDialogKey)]);
    }
}
