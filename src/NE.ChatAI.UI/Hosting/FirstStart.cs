using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NE.ChatAI.Connections;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Hosting;

/// <summary>On an empty data folder, lays down a connection and the documents it names, so the first chat works at once.</summary>
internal sealed class FirstStart(DataFolder data, IOptions<ChatAIOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => Profiles.EnsureDefaultsAsync(data, options.Value.KoboldCppAddress, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
