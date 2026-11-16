using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using NE.ChatAI.Storage;

namespace NE.ChatAI.Secrets;

/// <summary>What the settings may know of a secret: whether it is set, whether the environment sets it, and whether <c>secrets.json</c> holds an encrypted value — never its value.</summary>
public sealed record SecretInfo(string Name, bool IsSet, bool FromEnvironment, bool Stored = false);

/// <summary>A secret kept in the file but no longer readable: encrypted with keys this machine or user does not have.</summary>
public sealed class SecretUnreadableException : Exception
{
    public SecretUnreadableException()
    {
    }

    public SecretUnreadableException(string message) : base(message)
    {
    }

    public SecretUnreadableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// The application's secrets — API keys — in <c>secrets.json</c>, each encrypted with Data Protection, whose keys live in the user's
/// profile rather than in the data folder; an environment variable (<c>ChatAI__Secrets__&lt;name&gt;</c>) overrides the file.
/// </summary>
public sealed class SecretStore : IDisposable
{
    private const string Purpose = "NE.ChatAI.Secrets.v1";

    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly Func<string, string?> _environment;
    private readonly Func<IEnumerable<string>>? _environmentNames;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="path">The secrets file.</param>
    /// <param name="protection">The Data Protection the values are encrypted with.</param>
    /// <param name="environment">A secret's value from outside the file by its name, or <c>null</c>.</param>
    /// <param name="environmentNames">Optional enumeration of secret names currently provided by the environment.</param>
    public SecretStore(string path, IDataProtectionProvider protection, Func<string, string?> environment, Func<IEnumerable<string>>? environmentNames = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(environment);

        _path = path;
        _protector = protection.CreateProtector(Purpose);
        _environment = environment;
        _environmentNames = environmentNames;
    }

    /// <summary>The secret's value — the environment's first — or <c>null</c> when it is not set.</summary>
    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        if (_environment(Checked(name)) is { Length: > 0 } fromEnvironment)
            return fromEnvironment;

        SecretsFile file = await ReadAsync(cancellationToken).ConfigureAwait(false);

        if (!file.Secrets.TryGetValue(name, out var protectedValue))
            return null;

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException exception)
        {
            throw new SecretUnreadableException($"The secret '{name}' was kept with keys this machine does not have; set it again.", exception);
        }
    }

    private static string Checked(string name)
        => Slug.IsValidId(name) ? name : throw new ArgumentException($"'{name}' is not a secret's name: lower-case letters, digits and hyphens.", nameof(name));

    private async Task<SecretsFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new SecretsFile();

        var content = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);

        try
        {
            SecretsFile file = JsonSerializer.Deserialize<SecretsFile>(content, DocumentJson.Options) ?? new SecretsFile();

            return file.Format > Document.CurrentFormat
                ? throw new DocumentFormatException(_path, $"format {file.Format} is newer than this version writes ({Document.CurrentFormat}).")
                : file;
        }
        catch (JsonException exception)
        {
            throw new DocumentFormatException(_path, exception.Message, exception);
        }
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        var protectedValue = _protector.Protect(value);

        await ChangeAsync(secrets => secrets[Checked(name)] = protectedValue, cancellationToken).ConfigureAwait(false);
    }

    private async Task ChangeAsync(Action<Dictionary<string, string>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            SecretsFile file = await ReadAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, string> secrets = new(file.Secrets, StringComparer.Ordinal);

            change(secrets);

            await AtomicFile.WriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(file with { Format = Document.CurrentFormat, Secrets = secrets }, DocumentJson.Options), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var removed = false;

        await ChangeAsync(secrets => removed = secrets.Remove(Checked(name)), cancellationToken).ConfigureAwait(false);

        return removed;
    }

    /// <summary>Whether <paramref name="name"/> is set, and where from.</summary>
    public async Task<SecretInfo> DescribeAsync(string name, CancellationToken cancellationToken)
    {
        var fromEnvironment = _environment(Checked(name)) is { Length: > 0 };
        SecretsFile file = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var stored = file.Secrets.ContainsKey(name);

        return new SecretInfo(name, fromEnvironment || stored, fromEnvironment, stored);
    }

    /// <summary>Every stored or environment-supplied secret in ordinal name order, without exposing any secret value.</summary>
    public async Task<IReadOnlyList<SecretInfo>> ListAsync(CancellationToken cancellationToken)
    {
        SecretsFile file = await ReadAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> names = [with(StringComparer.Ordinal)];

        foreach (var key in file.Secrets.Keys)
        {
            if (Slug.IsValidId(key))
                _ = names.Add(key);
        }

        if (_environmentNames is not null)
        {
            foreach (var key in _environmentNames())
            {
                if (Slug.IsValidId(key) && _environment(key) is { Length: > 0 })
                    _ = names.Add(key);
            }
        }

        if (names.Count == 0)
            return [];

        List<string> ordered = [.. names];
        ordered.Sort(StringComparer.Ordinal);
        List<SecretInfo> items = [with(ordered.Count)];

        for (var i = 0; i < ordered.Count; i++)
        {
            var name = ordered[i];
            var fromEnvironment = _environment(name) is { Length: > 0 };
            var stored = file.Secrets.ContainsKey(name);
            items.Add(new SecretInfo(name, fromEnvironment || stored, fromEnvironment, stored));
        }

        return items;
    }

    public void Dispose()
        => _gate.Dispose();

    private sealed record SecretsFile : Document
    {
        public Dictionary<string, string> Secrets { get; init; } = [with(StringComparer.Ordinal)];
    }
}
