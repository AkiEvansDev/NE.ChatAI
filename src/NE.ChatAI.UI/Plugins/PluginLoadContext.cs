using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

namespace NE.ChatAI.Plugins;

/// <summary>Collectible load context for one plugin version; unifies host assemblies to <see cref="AssemblyLoadContext.Default"/> and preloads plugin binaries into memory.</summary>
internal sealed class PluginLoadContext(string name, string pluginDirectory) : AssemblyLoadContext(name, isCollectible: true)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (byte[] Assembly, byte[]? Symbols)> _packagedAssemblies = [with(StringComparer.OrdinalIgnoreCase)];

    public Assembly LoadEntryAssembly(string entryFileName)
    {
        var fullPath = Path.GetFullPath(Path.Combine(pluginDirectory, entryFileName));
        var rootWithSep = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new FileNotFoundException($"Entry assembly '{entryFileName}' was not found in the plugin package.", entryFileName);

        PreloadSecondaryAssemblies(fullPath);

        return LoadImage(ReadAssemblyImage(fullPath));
    }

    private void PreloadSecondaryAssemblies(string entryFullPath)
    {
        var files = Directory.GetFiles(pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly);

        lock (_sync)
        {
            for (var i = 0; i < files.Length; i++)
            {
                var dllPath = Path.GetFullPath(files[i]);

                if (string.Equals(dllPath, entryFullPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var simpleName = Path.GetFileNameWithoutExtension(dllPath);

                if (simpleName.Length > 0 && !_packagedAssemblies.ContainsKey(simpleName))
                    _packagedAssemblies[simpleName] = ReadAssemblyImage(dllPath);
            }
        }
    }

    private static (byte[] Assembly, byte[]? Symbols) ReadAssemblyImage(string dllPath)
    {
        var assemblyBytes = File.ReadAllBytes(dllPath);
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        var pdbBytes = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;

        return (assemblyBytes, pdbBytes);
    }

    private Assembly LoadImage((byte[] Assembly, byte[]? Symbols) image)
    {
        using MemoryStream assemblyStream = new(image.Assembly, writable: false);

        if (image.Symbols is byte[] pdbBytes)
        {
            using MemoryStream pdbStream = new(pdbBytes, writable: false);

            return LoadFromStream(assemblyStream, pdbStream);
        }

        return LoadFromStream(assemblyStream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not string simpleName)
            return null;

        foreach (Assembly loaded in Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return loaded;
        }

        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch (FileNotFoundException)
        {
        }
        catch (FileLoadException)
        {
        }

        (byte[] Assembly, byte[]? Symbols) image;

        lock (_sync)
        {
            if (!_packagedAssemblies.Remove(simpleName, out image))
                return null;
        }

        return LoadImage(image);
    }
}
