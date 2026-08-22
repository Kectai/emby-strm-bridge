using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;

namespace Emby.StrmBridge.Runtime;

internal static class EmbeddedAssemblyLoader
{
    internal const string HarmonyResourceName = "Emby.StrmBridge.Dependencies.0Harmony.dll.gz";
    private const string HarmonyAssemblyName = "0Harmony";
    private static readonly Version HarmonyVersion = new(2, 4, 2, 0);
    private static readonly object Sync = new();
    private static bool registered;

    public static Assembly EnsureHarmonyLoaded()
    {
        Register();
        var loaded = FindLoadedHarmony();
        return loaded ?? Assembly.Load(new AssemblyName(HarmonyAssemblyName)
        {
            Version = HarmonyVersion,
        });
    }

    internal static void Register()
    {
        lock (Sync)
        {
            if (registered) return;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            registered = true;
        }
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs arguments)
    {
        AssemblyName requested;
        try { requested = new AssemblyName(arguments.Name); }
        catch (ArgumentException) { return null; }
        if (!string.Equals(requested.Name, HarmonyAssemblyName, StringComparison.Ordinal) ||
            requested.Version is not null && requested.Version != HarmonyVersion)
            return null;

        lock (Sync)
        {
            var loaded = FindLoadedHarmony();
            if (loaded is not null) return loaded;
            var owner = typeof(EmbeddedAssemblyLoader).Assembly;
            using var resource = owner.GetManifestResourceStream(HarmonyResourceName)
                ?? throw new FileNotFoundException("The embedded playback patch runtime is unavailable.");
            if (resource.Length is <= 0 or > 8 * 1024 * 1024)
                throw new InvalidDataException("The embedded playback patch runtime is outside the supported bounds.");
            using var compressed = new GZipStream(resource, CompressionMode.Decompress, leaveOpen: false);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = compressed.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length + read > 8 * 1024 * 1024)
                    throw new InvalidDataException("The embedded playback patch runtime is outside the supported bounds.");
                output.Write(buffer, 0, read);
            }
            if (output.Length == 0)
                throw new InvalidDataException("The embedded playback patch runtime is empty.");
            var assembly = Assembly.Load(output.ToArray());
            var identity = assembly.GetName();
            if (!string.Equals(identity.Name, HarmonyAssemblyName, StringComparison.Ordinal) ||
                identity.Version != HarmonyVersion)
                throw new FileLoadException("The embedded playback patch runtime identity is invalid.");
            return assembly;
        }
    }

    private static Assembly? FindLoadedHarmony() =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
        {
            var identity = assembly.GetName();
            return string.Equals(identity.Name, HarmonyAssemblyName, StringComparison.Ordinal) &&
                   identity.Version == HarmonyVersion;
        });
}
