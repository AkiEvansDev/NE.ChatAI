using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Chats;

namespace NE.ChatAI.Prompts;

/// <summary>How many tokens a text makes, and whether the count is the model's own or an estimate.</summary>
public interface ITokenCounter
{
    TokenCounts Kind { get; }

    ValueTask<int> CountAsync(string text, CancellationToken cancellationToken);
}

/// <summary>An estimate where no tokenizer answers: about four characters a token in Latin text, fewer in Cyrillic.</summary>
public sealed class ApproximateTokenCounter : ITokenCounter
{
    public static ApproximateTokenCounter Instance { get; } = new();

    public TokenCounts Kind => TokenCounts.Approximate;

    public ValueTask<int> CountAsync(string text, CancellationToken cancellationToken)
        => ValueTask.FromResult(Estimate(text));

    /// <summary>Latin at four characters a token, any other letter at two and a half — Cyrillic splits finer in most tokenizers.</summary>
    public static int Estimate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        double tokens = 0;

        foreach (var character in text)
            tokens += character < 0x0250 ? 0.25 : 0.4;

        return (int)Math.Ceiling(tokens);
    }
}

/// <summary>
/// A tokenizer's counts kept by text, for one model: a prompt is mostly the same texts generation after generation — the card, the
/// older messages — so only what is new is asked of the server.
/// </summary>
public sealed class TokenCache
{
    /// <summary>Past this many texts the cache starts over rather than grow with a long session.</summary>
    private const int MaxEntries = 20_000;

    private readonly Dictionary<string, int> _counts = [with(StringComparer.Ordinal)];
    private readonly Lock _gate = new();
    private string? _model;

    /// <summary>Keeps counts only while the server runs the same model; another model counts with another tokenizer.</summary>
    public void UseModel(string? model)
    {
        if (string.IsNullOrEmpty(model))
            return;

        lock (_gate)
        {
            if (_model is not null && !string.Equals(_model, model, StringComparison.Ordinal))
                _counts.Clear();

            _model = model;
        }
    }

    public bool TryGet(string text, out int tokens)
    {
        lock (_gate)
            return _counts.TryGetValue(text, out tokens);
    }

    public void Add(string text, int tokens)
    {
        lock (_gate)
        {
            if (_counts.Count >= MaxEntries)
                _counts.Clear();

            _counts[text] = tokens;
        }
    }
}

/// <summary>The model's own counts through <paramref name="count"/>, each text asked once while <paramref name="cache"/> keeps it.</summary>
public sealed class CachedTokenCounter(Func<string, CancellationToken, Task<int>> count, TokenCache cache) : ITokenCounter
{
    public TokenCounts Kind => TokenCounts.Exact;

    public async ValueTask<int> CountAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
            return 0;

        if (cache.TryGet(text, out var tokens))
            return tokens;

        tokens = await count(text, cancellationToken).ConfigureAwait(false);
        cache.Add(text, tokens);

        return tokens;
    }
}
