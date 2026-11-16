using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace NE.ChatAI.Plugins;

/// <summary>
/// Collectible load context for one installed plugin version. Host and framework assemblies unify to <see cref="AssemblyLoadContext.Default"/>;
/// plugin-private assemblies are read into memory so no <c>.dll</c> file on disk is locked during hot-updates or uninstalls.
/// </summary>
internal sealed class PluginLoadContext(string name, string pluginDirectory) : AssemblyLoadContext(name, isCollectible: true)
{
    public Assembly LoadEntryAssembly(string entryFileName)
    {
        var fullPath = Path.GetFullPath(Path.Combine(pluginDirectory, entryFileName));
        var rootWithSep = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new FileNotFoundException($"Entry assembly '{entryFileName}' was not found in the plugin package.", entryFileName);

        return LoadFromMemory(fullPath);
    }

    private Assembly LoadFromMemory(string dllPath)
    {
        var assemblyBytes = File.ReadAllBytes(dllPath);
        using MemoryStream assemblyStream = new(assemblyBytes, writable: false);
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");

        if (File.Exists(pdbPath))
        {
            var pdbBytes = File.ReadAllBytes(pdbPath);
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

        var localDll = Path.Combine(pluginDirectory, simpleName + ".dll");

        return File.Exists(localDll) ? LoadFromMemory(localDll) : null;
    }
}
