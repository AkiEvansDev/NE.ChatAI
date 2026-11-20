using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NE.ChatAI.Backends.KoboldCpp;

/// <summary>Client for koboldcpp's HTTP API: model discovery, context length, token counting, SSE streaming and generation abort.</summary>
public sealed class KoboldCppClient
{
    private const string EventDataPrefix = "data:";

    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TokenCountTimeout = TimeSpan.FromSeconds(16);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly Uri _root;

    /// <summary>Creates a client targeting the koboldcpp server at <paramref name="address"/>.</summary>
    public KoboldCppClient(HttpClient http, Uri address)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(address);

        if (address.Scheme is not ("http" or "https"))
            throw new ArgumentException($"'{address}' is not an HTTP or HTTPS address.", nameof(address));

        _http = http;
        // An address without its final slash would lose its last segment to the endpoint's path.
        _root = address.AbsoluteUri.EndsWith('/', StringComparison.Ordinal) ? address : new Uri(address.AbsoluteUri + "/");
    }

    /// <summary>The loaded model's name, as koboldcpp reports it (<c>koboldcpp/…</c>).</summary>
    public async Task<string> GetModelAsync(CancellationToken cancellationToken)
    {
        ResultAnswer? answer = await RunBoundedAsync(
            ControlTimeout,
            ct => _http.GetFromJsonAsync<ResultAnswer>(Endpoint("api/v1/model"), Json, ct),
            cancellationToken
        ).ConfigureAwait(false);

        return answer?.Result ?? string.Empty;
    }

    private Uri Endpoint(string path)
        => new(_root, path);

    private static async Task<T> RunBoundedAsync<T>(TimeSpan timeout, Func<CancellationToken, Task<T>> callAsync, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        try
        {
            return await callAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The model server timed out.", exception);
        }
    }

    /// <summary>The context the server was started with, in tokens — not the one a client may ask for.</summary>
    public async Task<int> GetContextLengthAsync(CancellationToken cancellationToken)
    {
        ValueAnswer? answer = await RunBoundedAsync(
            ControlTimeout,
            ct => _http.GetFromJsonAsync<ValueAnswer>(Endpoint("api/extra/true_max_context_length"), Json, ct),
            cancellationToken
        ).ConfigureAwait(false);

        return answer?.Value ?? 0;
    }

    /// <summary>How many tokens the model's own tokenizer makes of <paramref name="text"/>: an exact count.</summary>
    public Task<int> CountTokensAsync(string text, CancellationToken cancellationToken)
        => RunBoundedAsync(
            TokenCountTimeout,
            async ct =>
            {
                using ByteArrayContent content = JsonBody(new { prompt = text });
                using HttpResponseMessage response = await _http.PostAsync(Endpoint("api/extra/tokencount"), content, ct).ConfigureAwait(false);

                _ = response.EnsureSuccessStatusCode();

                ValueAnswer? answer = await response.Content.ReadFromJsonAsync<ValueAnswer>(Json, ct).ConfigureAwait(false);

                return answer?.Value ?? 0;
            },
            cancellationToken
        );

    private static ByteArrayContent JsonBody<T>(T value)
    {
        // koboldcpp's HTTP/1.0 server expects a known Content-Length rather than chunked transfer encoding.
        ByteArrayContent content = new(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        return content;
    }

    /// <summary>Generates from <paramref name="request"/>, yielding the text as it comes; cancelling stops reading, <see cref="AbortAsync"/> stops the server.</summary>
    public async IAsyncEnumerable<string> StreamAsync(KoboldGenerateRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using HttpRequestMessage message = new(HttpMethod.Post, Endpoint("api/extra/generate/stream")) { Content = JsonBody(request) };
        using HttpResponseMessage response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

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
    public Task AbortAsync(string genKey, CancellationToken cancellationToken)
        => RunBoundedAsync(
            ControlTimeout,
            async ct =>
            {
                using ByteArrayContent content = JsonBody(new { genkey = genKey });
                using HttpResponseMessage response = await _http.PostAsync(Endpoint("api/extra/abort"), content, ct).ConfigureAwait(false);

                _ = response.EnsureSuccessStatusCode();
                return true;
            },
            cancellationToken
        );

    private sealed record ResultAnswer(string? Result);

    private sealed record ValueAnswer(int Value);

    private sealed record StreamEvent(string? Token, [property: JsonPropertyName("finish_reason")] string? FinishReason);
}
