using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Backends.KoboldCpp;

namespace NE.ChatAI.Hosting;

/// <summary>Tracks in-flight koboldcpp stream keys across concurrent generations so Stop can abort every active server call.</summary>
internal sealed class ActiveGenerations
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, KoboldCppClient> _inFlight = [with(StringComparer.Ordinal)];

    public IDisposable Track(KoboldCppClient client, string genKey)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(genKey);

        lock (_lock)
            _inFlight[genKey] = client;

        return new Registration(this, genKey);
    }

    public async Task AbortAllAsync(Action<HttpRequestException>? onFailure, CancellationToken cancellationToken)
    {
        KeyValuePair<string, KoboldCppClient>[] snapshot;

        lock (_lock)
        {
            if (_inFlight.Count == 0)
                return;

            snapshot = [.. _inFlight];
        }

        if (snapshot.Length == 1)
        {
            await AbortOneAsync(snapshot[0].Value, snapshot[0].Key, onFailure, cancellationToken).ConfigureAwait(false);
            return;
        }

        Task[] aborts = new Task[snapshot.Length];

        for (var i = 0; i < snapshot.Length; i++)
            aborts[i] = AbortOneAsync(snapshot[i].Value, snapshot[i].Key, onFailure, cancellationToken);

        await Task.WhenAll(aborts).ConfigureAwait(false);
    }

    private static async Task AbortOneAsync(KoboldCppClient client, string genKey, Action<HttpRequestException>? onFailure, CancellationToken cancellationToken)
    {
        try
        {
            await client.AbortAsync(genKey, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            onFailure?.Invoke(exception);
        }
    }

    private void Remove(string genKey)
    {
        lock (_lock)
            _ = _inFlight.Remove(genKey);
    }

    private sealed class Registration(ActiveGenerations owner, string key) : IDisposable
    {
        private ActiveGenerations? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Remove(key);
    }
}
