using System;
using System.Collections.Generic;
using System.Globalization;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Plugins;

/// <summary>Metadata read from a plugin release's <c>plugin.json</c> manifest.</summary>
internal sealed record PluginManifest
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Author { get; init; }

    public string? Repository { get; init; }

    public string EntryAssembly { get; init; } = string.Empty;

    public string? MinAppVersion { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>Persisted state of an installed plugin in <c>data/plugins/plugins.json</c>.</summary>
internal sealed record InstalledPluginRecord
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Author { get; init; }

    public required string Repository { get; init; }

    public required string InstalledVersion { get; init; }

    public string? LatestVersion { get; init; }

    public required string EntryAssembly { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public bool Enabled { get; init; } = true;

    public DateTimeOffset InstalledAt { get; init; }
}

/// <summary>Root document stored at <c>data/plugins/plugins.json</c>.</summary>
internal sealed record InstalledPluginsDocument : Document
{
    public IReadOnlyList<InstalledPluginRecord> Plugins { get; init; } = [];
}

/// <summary>Summary of one scenario node type contributed by an installed plugin.</summary>
internal sealed record PluginNodeSummary(string Key, string Title, string Category);

/// <summary>Snapshot of an installed plugin together with its runtime load state and registered nodes.</summary>
internal sealed record PluginStatus(InstalledPluginRecord Record, IReadOnlyList<PluginNodeSummary> Nodes, string? LoadError)
{
    public bool HasUpdate
        => !string.IsNullOrWhiteSpace(Record.LatestVersion)
            && PluginCoordinates.CompareVersions(Record.LatestVersion, Record.InstalledVersion) > 0;
}

/// <summary>Parses GitHub repository coordinates and compares plugin release versions.</summary>
internal static class PluginCoordinates
{
    public static string? NormalizeRepository(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var candidate = raw.Trim();

        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                return null;

            candidate = uri.AbsolutePath;
        }
        else if (candidate.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["github.com/".Length..];
        }

        candidate = candidate.Trim('/');

        if (candidate.EndsWith("/releases", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[..^"/releases".Length].TrimEnd('/');

        if (candidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[..^".git".Length];

        var parts = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
            return null;

        var owner = parts[0];
        var repo = parts[1];

        return IsValidSegment(owner) && IsValidSegment(repo) ? $"{owner}/{repo}" : null;
    }

    private static bool IsValidSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment is "." or "..")
            return false;

        for (var i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            var ok = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.';

            if (!ok)
                return false;
        }

        return true;
    }

    public static string NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var trimmed = raw.Trim();

        if (trimmed.Length > 1 && trimmed[0] is 'v' or 'V' && char.IsAsciiDigit(trimmed[1]))
            trimmed = trimmed[1..];

        return trimmed;
    }

    public static string VersionFolderName(string version)
    {
        var normalized = NormalizeVersion(version);
        Span<char> buffer = stackalloc char[Math.Min(normalized.Length, 64)];
        var written = 0;

        for (var i = 0; i < normalized.Length && written < buffer.Length; i++)
        {
            var ch = normalized[i];
            buffer[written++] = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-';
        }

        var folder = new string(buffer[..written]).Trim('.', '-');

        return folder.Length == 0 ? "0.0.0" : folder;
    }

    public static int CompareVersions(string? left, string? right)
    {
        var normLeft = NormalizeVersion(left);
        var normRight = NormalizeVersion(right);

        if (string.Equals(normLeft, normRight, StringComparison.OrdinalIgnoreCase))
            return 0;

        SplitPreRelease(normLeft, out var coreLeft, out var preLeft);
        SplitPreRelease(normRight, out var coreRight, out var preRight);

        var leftValid = TryParseCoreVersion(coreLeft, out Version vLeft);
        var rightValid = TryParseCoreVersion(coreRight, out Version vRight);

        if (leftValid && rightValid)
        {
            var byCore = vLeft.CompareTo(vRight);

            if (byCore != 0)
                return byCore;

            if (string.IsNullOrEmpty(preLeft) && string.IsNullOrEmpty(preRight))
                return 0;

            if (string.IsNullOrEmpty(preLeft))
                return 1;

            if (string.IsNullOrEmpty(preRight))
                return -1;

            return ComparePreRelease(preLeft, preRight);
        }

        if (leftValid != rightValid)
            return leftValid ? 1 : -1;

        return 0;
    }

    private static void SplitPreRelease(string value, out string core, out string preRelease)
    {
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        var withoutBuild = plus >= 0 ? value[..plus] : value;
        var dash = withoutBuild.IndexOf('-', StringComparison.Ordinal);

        if (dash < 0)
        {
            core = withoutBuild;
            preRelease = string.Empty;
            return;
        }

        core = withoutBuild[..dash];
        preRelease = withoutBuild[(dash + 1)..];
    }

    private static bool TryParseCoreVersion(string value, out Version version)
    {
        var parts = value.Split('.');

        if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var majorOnly))
        {
            version = new Version(majorOnly, 0, 0, 0);
            return true;
        }

        if (Version.TryParse(value, out Version? parsed))
        {
            version = new Version(
                parsed.Major,
                Math.Max(0, parsed.Minor),
                Math.Max(0, parsed.Build),
                Math.Max(0, parsed.Revision)
            );
            return true;
        }

        version = new Version(0, 0, 0, 0);
        return false;
    }

    private static int ComparePreRelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var common = Math.Min(leftParts.Length, rightParts.Length);

        for (var i = 0; i < common; i++)
        {
            var leftIsNum = ulong.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNum);
            var rightIsNum = ulong.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNum);

            if (leftIsNum && rightIsNum)
            {
                var byNum = leftNum.CompareTo(rightNum);

                if (byNum != 0)
                    return byNum;
            }
            else if (leftIsNum != rightIsNum)
            {
                return leftIsNum ? -1 : 1;
            }
            else
            {
                var byText = string.Compare(leftParts[i], rightParts[i], StringComparison.OrdinalIgnoreCase);

                if (byText != 0)
                    return byText;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    public static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? raw)
    {
        if (raw is not { Count: > 0 })
            return [];

        List<string> result = [with(raw.Count)];
        HashSet<string> seen = [with(StringComparer.OrdinalIgnoreCase)];

        for (var i = 0; i < raw.Count; i++)
        {
            var tag = raw[i]?.Trim();

            if (!string.IsNullOrEmpty(tag) && seen.Add(tag))
                result.Add(tag);
        }

        return result;
    }
}
