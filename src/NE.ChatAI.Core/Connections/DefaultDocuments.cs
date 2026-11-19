using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NE.ChatAI.Scenarios;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Connections;

/// <summary>
/// The base set of model formats, system prompts, prompt presets, samplers and scenarios — the repository's <c>default/</c> folder, built into
/// this assembly: laid down on a first start, and on request whatever of it a data folder lacks.
/// </summary>
public static class DefaultDocuments
{
    /// <summary>The first connection laid down on an empty data folder, and the documents it names.</summary>
    public const string ConnectionId = "koboldcpp-d0000001";
    public const string FormatId = "gemma-4-d1000001";
    public const string SystemPromptId = "roleplay-d2000001";
    public const string PromptId = "roleplay-d3000001";
    public const string SamplersId = "balanced-d4000001";
    public const string SummaryScenarioId = "summary-d5000001";
    public const string SceneReportScenarioId = "scene-report-d5000002";
    public const string ImpersonateScenarioId = "impersonate-d5000003";
    public const string InitiativeScenarioId = "initiative-d5000004";

    // A resource is named `default/<kind>/<id>.json` by the project file; the kind is the data folder's own folder name.
    private const string Prefix = "default/";
    private const string Extension = ".json";

    private static readonly HashSet<string> BuiltInIds = ReadBuiltInIds();

    /// <summary>Whether <paramref name="id"/> is one of the shipped default documents in <c>default/</c>.</summary>
    public static bool IsBuiltIn(string? id)
        => id is not null && BuiltInIds.Contains(id);

    private static HashSet<string> ReadBuiltInIds()
    {
        HashSet<string> ids = [with(StringComparer.Ordinal)];

        foreach (var resource in typeof(DefaultDocuments).Assembly.GetManifestResourceNames())
        {
            var name = resource.Replace('\\', '/');

            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Extension, StringComparison.Ordinal))
                continue;

            var parts = name[Prefix.Length..^Extension.Length].Split('/');

            if (parts.Length == 2)
                _ = ids.Add(parts[1]);
        }

        return ids;
    }

    /// <summary>Writes each document of the base set the folder holds no document under its id, or all of them when <paramref name="overwrite"/> is <c>true</c>; answers how many it wrote.</summary>
    /// <remarks>When <paramref name="overwrite"/> is <c>false</c>, never writes over one the folder has: a default the reader changed stays changed, and one deleted comes back only from here.</remarks>
    public static async Task<int> LayDownAsync(DataFolder data, CancellationToken cancellationToken, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(data);

        Assembly assembly = typeof(DefaultDocuments).Assembly;
        var written = 0;

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var name = resource.Replace('\\', '/');

            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Extension, StringComparison.Ordinal))
                continue;

            var parts = name[Prefix.Length..^Extension.Length].Split('/');

            if (parts.Length != 2)
                continue;

            written += parts[0] switch
            {
                "instruct" => await LayDownAsync(data.ModelFormats, assembly, resource, parts[1], overwrite, cancellationToken).ConfigureAwait(false),
                "sysprompts" => await LayDownAsync(data.SystemPrompts, assembly, resource, parts[1], overwrite, cancellationToken).ConfigureAwait(false),
                "prompts" => await LayDownAsync(data.Prompts, assembly, resource, parts[1], overwrite, cancellationToken).ConfigureAwait(false),
                "samplers" => await LayDownAsync(data.Samplers, assembly, resource, parts[1], overwrite, cancellationToken).ConfigureAwait(false),
                "graphs" => await LayDownAsync(data.Scenarios, assembly, resource, parts[1], overwrite, cancellationToken).ConfigureAwait(false),
                _ => 0
            };
        }

        return written;
    }

    /// <summary>Writes the shipped scenarios when an older data folder has never had a <c>graphs/</c> folder yet.</summary>
    public static async Task LayDownScenariosAsync(DataFolder data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        var graphsFolder = Path.Combine(data.Root, "graphs");
        _ = Directory.CreateDirectory(graphsFolder);
        Assembly assembly = typeof(DefaultDocuments).Assembly;

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var name = resource.Replace('\\', '/');

            if (!name.StartsWith("default/graphs/", StringComparison.Ordinal) || !name.EndsWith(Extension, StringComparison.Ordinal))
                continue;

            var id = name["default/graphs/".Length..^Extension.Length];

            if (!id.Contains('/', StringComparison.Ordinal))
                _ = await LayDownAsync(data.Scenarios, assembly, resource, id, overwrite: false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a shipped scenario by <paramref name="id"/>, laying it down from <c>default/graphs/</c> first when absent.</summary>
    public static async Task<Scenario?> EnsureScenarioAsync(DataFolder data, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!Slug.IsValidId(id))
            return null;

        if (await data.Scenarios.ReadAsync(id, cancellationToken).ConfigureAwait(false) is Scenario existing)
            return existing;

        Assembly assembly = typeof(DefaultDocuments).Assembly;
        var target = $"default/graphs/{id}{Extension}";

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (string.Equals(resource.Replace('\\', '/'), target, StringComparison.Ordinal))
            {
                _ = await LayDownAsync(data.Scenarios, assembly, resource, id, overwrite: false, cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        return await data.Scenarios.ReadAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> LayDownAsync<T>(DocumentStore<T> store, Assembly assembly, string resource, string id, bool overwrite, CancellationToken cancellationToken)
        where T : NamedDocument
    {
        if (!Slug.IsValidId(id) || (!overwrite && store.Exists(id)))
            return 0;

        Stream stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"The resource '{resource}' is gone.");

        await using ConfiguredAsyncDisposable disposing = stream.ConfigureAwait(false);
        T document = await JsonSerializer.DeserializeAsync<T>(stream, DocumentJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The default document '{resource}' is empty.");

        await store.WriteAsync(id, document, cancellationToken).ConfigureAwait(false);

        return 1;
    }
}
