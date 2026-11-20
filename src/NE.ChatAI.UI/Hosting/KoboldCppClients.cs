using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Backends.KoboldCpp;
using NE.ChatAI.Chats;
using NE.ChatAI.Prompts;

namespace NE.ChatAI.Hosting;

/// <summary>Creates koboldcpp clients over pooled HTTP handlers, caches token counts per server, and serializes calls per endpoint.</summary>
internal sealed class KoboldCppClients(IHttpClientFactory http)
{
    public const string HttpClientName = "koboldcpp";

    private static readonly TimeSpan ReplyCountPatience = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, TokenCache> _caches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _slots = new(StringComparer.OrdinalIgnoreCase);

    public KoboldCppClient For(Uri address)
        => new(http.CreateClient(HttpClientName), address);

    public async ValueTask<IDisposable> AcquireSlotAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        SemaphoreSlim gate = _slots.GetOrAdd(ServerKey(address), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new SlotLease(() => _ = gate.Release());
    }

    private static string ServerKey(Uri address)
        => address.AbsoluteUri.TrimEnd('/');

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

    public async Task<BuiltPrompt> BuildPromptWithFallbackAsync(PromptSpec spec, ModelFormat format, int contextLength, int replyLength, Uri? address, KoboldCppClient? client, string? model, CancellationToken cancellationToken, Action<HttpRequestException>? onFailure = null)
    {
        if (client is not null && address is not null)
        {
            try
            {
                ITokenCounter counter = CounterFor(address, client, model);

                return await PromptBuilder.BuildAsync(spec, format, contextLength, replyLength, counter, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                onFailure?.Invoke(exception);
            }
        }

        return await PromptBuilder.BuildAsync(spec, format, contextLength, replyLength, ApproximateTokenCounter.Instance, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string?> TryGetModelNameAsync(KoboldCppClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            var model = (await client.GetModelAsync(cancellationToken).ConfigureAwait(false)).Trim();

            return model.Length == 0 ? null : model;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public static async Task<(int Tokens, TokenCounts Counts)> CountReplyAsync(KoboldCppClient client, string reply, BuiltPrompt? prompt, Action<HttpRequestException>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(reply);

        if (prompt?.Counts == TokenCounts.Exact)
        {
            using CancellationTokenSource patience = new(ReplyCountPatience);

            try
            {
                return (reply.Length == 0 ? 0 : await client.CountTokensAsync(reply, patience.Token).ConfigureAwait(false), TokenCounts.Exact);
            }
            catch (HttpRequestException exception)
            {
                onFailure?.Invoke(exception);
            }
            catch (OperationCanceledException) when (patience.IsCancellationRequested)
            {
            }
        }

        return (ApproximateTokenCounter.Estimate(reply), TokenCounts.Approximate);
    }

    private sealed class SlotLease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
            => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
