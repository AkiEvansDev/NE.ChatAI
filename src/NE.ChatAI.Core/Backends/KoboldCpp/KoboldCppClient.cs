using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NE.ChatAI.Backends.KoboldCpp;

/// <summary>koboldcpp's own API: what the model is, how long its context is, how a text counts in tokens, and a streamed generation.</summary>
/// <param name="http">A client with no timeout of its own: a reply streams for as long as the model writes it.</param>
/// <param name="address">The server's root, a connection's address; each connection has its own.</param>
public sealed class KoboldCppClient(HttpClient http, Uri address)
{
    private const string EventDataPrefix = "data:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // An address without its final slash would lose its last segment to the endpoint's path.
    private readonly Uri _root = address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");

    /// <summary>The loaded model's name, as koboldcpp reports it (<c>koboldcpp/…</c>).</summary>
    public async Task<string> GetModelAsync(CancellationToken cancellationToken)
    {
        ResultAnswer? answer = await http.GetFromJsonAsync<ResultAnswer>(Endpoint("api/v1/model"), Json, cancellationToken).ConfigureAwait(false);

        return answer?.Result ?? string.Empty;
    }

    private Uri Endpoint(string path)
        => new(_root, path);

    /// <summary>The context the server was started with, in tokens — not the one a client may ask for.</summary>
    public async Task<int> GetContextLengthAsync(CancellationToken cancellationToken)
    {
        ValueAnswer? answer = await http.GetFromJsonAsync<ValueAnswer>(Endpoint("api/extra/true_max_context_length"), Json, cancellationToken).ConfigureAwait(false);

        return answer?.Value ?? 0;
    }

    /// <summary>How many tokens the model's own tokenizer makes of <paramref name="text"/>: an exact count.</summary>
    public async Task<int> CountTokensAsync(string text, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(Endpoint("api/extra/tokencount"), new { prompt = text }, Json, cancellationToken).ConfigureAwait(false);

        _ = response.EnsureSuccessStatusCode();

        ValueAnswer? answer = await response.Content.ReadFromJsonAsync<ValueAnswer>(Json, cancellationToken).ConfigureAwait(false);

        return answer?.Value ?? 0;
    }

    /// <summary>Generates from <paramref name="request"/>, yielding the text as it comes; cancelling stops reading, <see cref="AbortAsync"/> stops the server.</summary>
    public async IAsyncEnumerable<string> StreamAsync(KoboldGenerateRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using HttpRequestMessage message = new(HttpMethod.Post, Endpoint("api/extra/generate/stream")) { Content = JsonContent.Create(request, options: Json) };
        using HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        _ = response.EnsureSuccessStatusCode();

        Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (body.ConfigureAwait(false))
        {
            using StreamReader reader = new(body);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
            {
                if (!line.StartsWith(EventDataPrefix, StringComparison.Ordinal))
                    continue;

                ReadOnlySpan<char> payload = line.AsSpan(EventDataPrefix.Length).Trim();

                if (payload.IsEmpty)
                    continue;

                StreamEvent? streamEvent = JsonSerializer.Deserialize<StreamEvent>(payload, Json);

                if (streamEvent?.Token is { Length: > 0 } token)
                    yield return token;

                if (streamEvent?.FinishReason is not null)
                    yield break;
            }
        }
    }

    /// <summary>Stops the generation started under <paramref name="genKey"/>; what it made so far is the end of the stream.</summary>
    public async Task AbortAsync(string genKey, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(Endpoint("api/extra/abort"), new { genkey = genKey }, Json, cancellationToken).ConfigureAwait(false);

        _ = response.EnsureSuccessStatusCode();
    }

    private sealed record ResultAnswer(string? Result);

    private sealed record ValueAnswer(int Value);

    private sealed record StreamEvent(string? Token, [property: JsonPropertyName("finish_reason")] string? FinishReason);
}
