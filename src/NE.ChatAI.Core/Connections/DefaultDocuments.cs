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

/// <summary>Shipped model formats, system prompts, prompt presets, samplers and scenarios embedded from <c>default/</c>.</summary>
public static class DefaultDocuments
{
    /// <summary>The first connection laid down on an empty data folder.</summary>
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

        foreach ((_, var id, _) in EnumerateResources(typeof(DefaultDocuments).Assembly))
            _ = ids.Add(id);

        return ids;
    }

    private static IEnumerable<(string Kind, string Id, string Resource)> EnumerateResources(Assembly assembly)
    {
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var name = resource.Replace('\\', '/');

            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Extension, StringComparison.Ordinal))
                continue;

            var parts = name[Prefix.Length..^Extension.Length].Split('/');

            if (parts.Length == 2)
                yield return (parts[0], parts[1], resource);
        }
    }

    /// <summary>Writes missing (or all, when <paramref name="overwrite"/> is <see langword="true"/>) shipped documents into <paramref name="data"/> and returns the count written.</summary>
    public static async Task<int> LayDownAsync(DataFolder data, CancellationToken cancellationToken, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(data);

        Assembly assembly = typeof(DefaultDocuments).Assembly;
        var written = 0;

        foreach ((var kind, var id, var resource) in EnumerateResources(assembly))
        {
            written += kind switch
            {
                "instruct" => await LayDownAsync(data.ModelFormats, assembly, resource, id, overwrite, cancellationToken).ConfigureAwait(false),
                "sysprompts" => await LayDownAsync(data.SystemPrompts, assembly, resource, id, overwrite, cancellationToken).ConfigureAwait(false),
                "prompts" => await LayDownAsync(data.Prompts, assembly, resource, id, overwrite, cancellationToken).ConfigureAwait(false),
                "samplers" => await LayDownAsync(data.Samplers, assembly, resource, id, overwrite, cancellationToken).ConfigureAwait(false),
                "graphs" => await LayDownAsync(data.Scenarios, assembly, resource, id, overwrite, cancellationToken).ConfigureAwait(false),
                _ => 0
            };
        }

        return written;
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

    /// <summary>Writes the shipped scenarios when an older data folder has no <c>graphs/</c> directory yet.</summary>
    public static async Task LayDownScenariosAsync(DataFolder data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        _ = Directory.CreateDirectory(Path.Combine(data.Root, "graphs"));
        Assembly assembly = typeof(DefaultDocuments).Assembly;

        foreach ((var kind, var id, var resource) in EnumerateResources(assembly))
        {
            if (kind == "graphs")
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

        foreach ((var kind, var resourceId, var resource) in EnumerateResources(assembly))
        {
            if (kind == "graphs" && string.Equals(resourceId, id, StringComparison.Ordinal))
            {
                _ = await LayDownAsync(data.Scenarios, assembly, resource, id, overwrite: false, cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        return await data.Scenarios.ReadAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
