using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NE.ChatAI.Connections;
using NE.ChatAI.Plugins;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Hosting;

/// <summary>On an empty data folder, lays down a connection and the documents it names, and warms up installed scenario plugins.</summary>
internal sealed class FirstStart(DataFolder data, IOptions<ChatAIOptions> options, PluginManager plugins) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = plugins.Catalog;
        await Profiles.EnsureDefaultsAsync(data, options.Value.KoboldCppAddress, cancellationToken).ConfigureAwait(false);
        await DocumentRenames.SyncAllIdsAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
