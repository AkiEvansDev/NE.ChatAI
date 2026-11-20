using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NE.ChatAI.Plugins;

namespace NE.ChatAI.Controllers;

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
    public partial UIPhrase? NodesLine { get; set; }

    [RecursiveMember]
    public partial UIVisibility NodesVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial string LoadError { get; set; } = string.Empty;

    [RecursiveMember]
    public partial UIVisibility ErrorVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial UIPhrase? UpdateTitle { get; set; }

    [RecursiveMember]
    public partial UIVisibility UpdateVisibility { get; set; } = UIVisibility.Collapsed;

    [RecursiveMember]
    public partial bool Enabled { get; set; } = true;
}

internal sealed partial class PluginsController : ShellController
{
    public const string RepositoryInputId = "chatai-plugins-repo";
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
        HashSet<string> keep = new(snapshot.Count, StringComparer.Ordinal);

        for (var i = 0; i < snapshot.Count; i++)
            _ = keep.Add(snapshot[i].Record.Id);

        for (var i = Plugins.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Plugins[i].Id))
                _ = Plugins.Remove(Plugins[i].Id);
        }

        for (var i = 0; i < snapshot.Count; i++)
        {
            PluginStatus status = snapshot[i];
            InstalledPluginRecord record = status.Record;
            var description = record.Description ?? string.Empty;
            UIPhrase? nodesLine = FormatNodesLine(status.Nodes);
            var loadError = status.LoadError ?? string.Empty;
            var hasUpdate = status.HasUpdate;
            var latestVersion = record.LatestVersion ?? record.InstalledVersion;
            var versionText = $"v{record.InstalledVersion}";

            if (Plugins.TryGet(record.Id, out PluginItem? row))
            {
                row.Name = record.Name;
                row.VersionText = versionText;
                row.Repository = record.Repository;
                row.RepositoryUrl = $"https://github.com/{record.Repository}";
                row.Description = description;
                row.DescriptionVisibility = description.Length > 0 ? UIVisibility.Visible : UIVisibility.Collapsed;
                row.NodesLine = nodesLine;
                row.NodesVisibility = nodesLine is not null ? UIVisibility.Visible : UIVisibility.Collapsed;
                row.LoadError = loadError;
                row.ErrorVisibility = loadError.Length > 0 ? UIVisibility.Visible : UIVisibility.Collapsed;
                row.UpdateTitle = hasUpdate ? UIPhrase.Of("chatai.plugins.update-to", ("version", latestVersion)) : null;
                row.UpdateVisibility = hasUpdate ? UIVisibility.Visible : UIVisibility.Collapsed;
                row.Enabled = record.Enabled;
                SyncBadges(row.Badges, versionText, record.Tags);

                if (Plugins[i].Id != record.Id)
                    _ = Plugins.Move(record.Id, i);
            }
            else
            {
                PluginItem created = new()
                {
                    Id = record.Id,
                    Name = record.Name,
                    VersionText = versionText,
                    Repository = record.Repository,
                    RepositoryUrl = $"https://github.com/{record.Repository}",
                    Description = description,
                    DescriptionVisibility = description.Length > 0 ? UIVisibility.Visible : UIVisibility.Collapsed,
                    NodesLine = nodesLine,
                    NodesVisibility = nodesLine is not null ? UIVisibility.Visible : UIVisibility.Collapsed,
                    LoadError = loadError,
                    ErrorVisibility = loadError.Length > 0 ? UIVisibility.Visible : UIVisibility.Collapsed,
                    UpdateTitle = hasUpdate ? UIPhrase.Of("chatai.plugins.update-to", ("version", latestVersion)) : null,
                    UpdateVisibility = hasUpdate ? UIVisibility.Visible : UIVisibility.Collapsed,
                    Enabled = record.Enabled
                };
                SyncBadges(created.Badges, versionText, record.Tags);
                Plugins.Insert(i, created);
            }
        }
    }

    private static UIPhrase? FormatNodesLine(IReadOnlyList<PluginNodeSummary> nodes)
    {
        if (nodes.Count == 0)
            return null;

        StringBuilder list = new();

        for (var i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
                _ = list.Append(", ");

            _ = list.Append(nodes[i].Title).Append(" (").Append(nodes[i].Key).Append(')');
        }

        return UIPhrase.Of("chatai.plugins.nodes", ("nodes", list.ToString()));
    }

    private static void SyncBadges(RecursiveCollection<BadgeItem> badges, string versionText, IReadOnlyList<string> tags)
    {
        HashSet<string> keep = new(1 + tags.Count, StringComparer.Ordinal) { "__version" };

        for (var i = 0; i < tags.Count; i++)
            _ = keep.Add($"tag:{tags[i]}");

        for (var i = badges.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(badges[i].Id))
                _ = badges.Remove(badges[i].Id);
        }

        UpsertBadge(badges, 0, "__version", versionText, null);

        for (var i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            UpsertBadge(badges, i + 1, $"tag:{tag}", tag, TagColorOf(tag));
        }
    }

    private static void UpsertBadge(RecursiveCollection<BadgeItem> badges, int index, string id, string title, UIThemeColor? color)
    {
        if (badges.TryGet(id, out BadgeItem? existing))
        {
            existing.BadgeTitle = title;
            existing.BadgeColor = color;

            if (badges[index].Id != id)
                _ = badges.Move(id, index);

            return;
        }

        badges.Insert(index, new BadgeItem
        {
            Id = id,
            IsContent = true,
            BadgeTitle = title,
            BadgeColor = color
        });
    }

    private static UIThemeColor TagColorOf(string tag)
        => tag.ToLowerInvariant() switch
        {
            "alpha" or "beta" or "preview" or "experimental" => UIThemeColor.Warning,
            _ => UIThemeColor.Primary
        };

    /// <summary>Downloads and installs the latest release from <see cref="RepositoryInput"/>.</summary>
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

    /// <summary>Queries GitHub Releases for installed plugins and refreshes available update actions.</summary>
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

    /// <summary>Hot-updates an installed plugin in place without restarting the host.</summary>
    [UICommand]
    public async Task<UICommandResult> UpdatePluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        PluginOperationResult result = await PluginsManager.UpdateAsync(pluginId, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || result.Plugin is null)
            return NotifyError(result);

        RefreshPlugins();

        return Notify(UIPhrase.Of("chatai.plugins.updated", ("name", result.Plugin.Record.Name), ("version", result.Plugin.Record.InstalledVersion)), UIColorStyle.Success);
    }

    /// <summary>Enables or disables an installed plugin and rebuilds the scenario catalogue.</summary>
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

    /// <summary>Unloads and deletes the plugin pending confirmation.</summary>
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
