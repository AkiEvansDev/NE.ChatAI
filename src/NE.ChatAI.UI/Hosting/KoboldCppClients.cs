using System;
using System.Collections.Concurrent;
using System.Net.Http;
using NE.ChatAI.Backends.KoboldCpp;
using NE.ChatAI.Prompts;

namespace NE.ChatAI.Hosting;

/// <summary>A koboldcpp client for a connection's address, over the host's pooled HTTP handlers, and the token counts it gave.</summary>
internal sealed class KoboldCppClients(IHttpClientFactory http)
{
    public const string HttpClientName = "koboldcpp";

    /// <summary>One cache per server: another address or another model loaded there counts otherwise.</summary>
    private readonly ConcurrentDictionary<string, TokenCache> _caches = new(StringComparer.OrdinalIgnoreCase);

    public KoboldCppClient For(Uri address)
        => new(http.CreateClient(HttpClientName), address);

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

        return _caches.GetOrAdd(address.AbsoluteUri, static _ => new TokenCache());
    }
}
