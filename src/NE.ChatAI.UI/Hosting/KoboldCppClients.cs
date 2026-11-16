using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Backends.KoboldCpp;
using NE.ChatAI.Prompts;

namespace NE.ChatAI.Hosting;

/// <summary>A koboldcpp client for a connection's address, over the host's pooled HTTP handlers, its token cache, and its per-server generation gate.</summary>
internal sealed class KoboldCppClients(IHttpClientFactory http)
{
    public const string HttpClientName = "koboldcpp";

    /// <summary>One cache per server: another address or another model loaded there counts otherwise.</summary>
    private readonly ConcurrentDictionary<string, TokenCache> _caches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One FIFO gate per physical server address so scenarios sharing an endpoint run one at a time while distinct servers run in parallel.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _slots = new(StringComparer.OrdinalIgnoreCase);

    public KoboldCppClient For(Uri address)
        => new(http.CreateClient(HttpClientName), address);

    /// <summary>Acquires exclusive generation access to the physical server at <paramref name="address"/> until disposed.</summary>
    public async ValueTask<IDisposable> AcquireSlotAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        SemaphoreSlim gate = _slots.GetOrAdd(ServerKey(address), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new SlotLease(() => _ = gate.Release());
    }

    /// <summary>The model's own counts through <paramref name="client"/>, each text asked of the server once while its model stays the same.</summary>
    public ITokenCounter CounterFor(Uri address, KoboldCppClient client, string? model = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        TokenCache cache = CacheFor(address);
        cache.UseModel(model);

        return new CachedTokenCounter(client.CountTokensAsync, cache);
    }

    public TokenCache CacheFor(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return _caches.GetOrAdd(ServerKey(address), static _ => new TokenCache());
    }

    private static string ServerKey(Uri address)
        => address.AbsoluteUri.TrimEnd('/');

    private sealed class SlotLease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
            => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
