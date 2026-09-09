using System.Reflection;
using Emby.StrmBridge.Runtime;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class EmbeddedAssemblyLoaderTests
{
    [TestMethod]
    public void ProductionAssemblyDoesNotReferenceHarmonyAtLoadTime()
    {
        Assert.IsFalse(typeof(Plugin).Assembly.GetReferencedAssemblies()
            .Any(reference => string.Equals(reference.Name, "0Harmony", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PluginContainsAndLoadsExpectedHarmonyRuntime()
    {
        Assert.Contains(
            EmbeddedAssemblyLoader.HarmonyResourceName,
            typeof(Plugin).Assembly.GetManifestResourceNames());

        var assembly = EmbeddedAssemblyLoader.EnsureHarmonyLoaded();

        Assert.AreEqual("0Harmony", assembly.GetName().Name);
        Assert.AreEqual(new Version(2, 4, 2, 0), assembly.GetName().Version);
    }

    [TestMethod]
    public void ResolverDoesNotHandleUnrelatedAssemblies()
    {
        EmbeddedAssemblyLoader.Register();

        Assert.ThrowsExactly<FileNotFoundException>(() =>
            Assembly.Load(new AssemblyName("Emby.StrmBridge.Unrelated.Dependency")));
    }
}
