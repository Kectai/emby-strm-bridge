using System.Reflection;
using Emby.StrmBridge.Playback;
using HarmonyLib;

namespace Emby.StrmBridge.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HarmonyPatchHostTests
{
    [TestMethod]
    public void PrefixDiagnostics_ReflectLatePatchChangesAndReportOnlyNumericPriorityFacts()
    {
        var target = GetMethod(nameof(Target));
        var nativePrefix = GetMethod(nameof(NativePrefix));
        var nativeOwner = "StrmBridge.Tests.Native." + Guid.NewGuid().ToString("N");
        var lowerOwner = "StrmBridge.Tests.ExternalLower." + Guid.NewGuid().ToString("N");
        var equalOwner = "StrmBridge.Tests.ExternalEqual." + Guid.NewGuid().ToString("N");
        var higherOwner = "StrmBridge.Tests.ExternalHigher." + Guid.NewGuid().ToString("N");
        var nativeHarmony = new Harmony(nativeOwner);
        var lowerHarmony = new Harmony(lowerOwner);
        var equalHarmony = new Harmony(equalOwner);
        var higherHarmony = new Harmony(higherOwner);
        try
        {
            nativeHarmony.Patch(target, prefix: new HarmonyMethod(nativePrefix) { priority = Priority.First });

            var uncontended = InspectProgressivePrefixes(target, nativeOwner, nativePrefix);
            Assert.IsTrue(uncontended.IsAvailable);
            Assert.AreEqual(0, uncontended.ExternalPrefixCount);
            Assert.IsTrue(uncontended.NativePrefixInstalled);
            Assert.AreEqual(Priority.First, uncontended.NativePrefixPriority);
            Assert.IsNull(uncontended.HighestExternalPrefixPriority);
            Assert.IsTrue(uncontended.NativePrefixUncontended);
            Assert.IsFalse(uncontended.NativePrefixPriorityStrictlyHigher);

            lowerHarmony.Patch(
                target,
                prefix: new HarmonyMethod(GetMethod(nameof(ExternalLowerPrefix))) { priority = Priority.High });

            var lowerAdded = InspectProgressivePrefixes(target, nativeOwner, nativePrefix);
            Assert.AreEqual(1, lowerAdded.ExternalPrefixCount);
            Assert.AreEqual(Priority.High, lowerAdded.HighestExternalPrefixPriority);
            Assert.IsFalse(lowerAdded.NativePrefixUncontended);
            Assert.IsTrue(lowerAdded.NativePrefixPriorityStrictlyHigher);

            equalHarmony.Patch(
                target,
                prefix: new HarmonyMethod(GetMethod(nameof(ExternalEqualPrefix))) { priority = Priority.First });

            var equalAdded = InspectProgressivePrefixes(target, nativeOwner, nativePrefix);
            Assert.AreEqual(2, equalAdded.ExternalPrefixCount);
            Assert.AreEqual(Priority.First, equalAdded.HighestExternalPrefixPriority);
            Assert.IsFalse(equalAdded.NativePrefixPriorityStrictlyHigher);

            higherHarmony.Patch(
                target,
                prefix: new HarmonyMethod(GetMethod(nameof(ExternalHigherPrefix)))
                {
                    priority = Priority.First + 1,
                });

            var higherAdded = InspectProgressivePrefixes(target, nativeOwner, nativePrefix);
            Assert.AreEqual(3, higherAdded.ExternalPrefixCount);
            Assert.AreEqual(Priority.First + 1, higherAdded.HighestExternalPrefixPriority);
            Assert.IsFalse(higherAdded.NativePrefixUncontended);
            Assert.IsFalse(higherAdded.NativePrefixPriorityStrictlyHigher);

            higherHarmony.UnpatchAll(higherOwner);
            equalHarmony.UnpatchAll(equalOwner);
            var equalRemoved = InspectProgressivePrefixes(target, nativeOwner, nativePrefix);
            Assert.AreEqual(1, equalRemoved.ExternalPrefixCount);
            Assert.IsTrue(equalRemoved.NativePrefixPriorityStrictlyHigher);

            nativeHarmony.UnpatchAll(nativeOwner);
            var nativeRemoved = InspectProgressivePrefixes(target, nativeOwner, nativePrefix);
            Assert.IsFalse(nativeRemoved.NativePrefixInstalled);
            Assert.IsNull(nativeRemoved.NativePrefixPriority);
            Assert.IsFalse(nativeRemoved.NativePrefixUncontended);
            Assert.IsFalse(nativeRemoved.NativePrefixPriorityStrictlyHigher);
        }
        finally
        {
            higherHarmony.UnpatchAll(higherOwner);
            equalHarmony.UnpatchAll(equalOwner);
            lowerHarmony.UnpatchAll(lowerOwner);
            nativeHarmony.UnpatchAll(nativeOwner);
        }
    }

    [TestMethod]
    public void PrefixDiagnostics_CountASharedPatchMethodAsExternalForAnotherOwner()
    {
        var target = GetMethod(nameof(AmbiguousTarget));
        var nativePrefix = GetMethod(nameof(AmbiguousPrefix));
        var nativeOwner = "StrmBridge.Tests.Native." + Guid.NewGuid().ToString("N");
        var externalOwner = "StrmBridge.Tests.ExternalSameMethod." + Guid.NewGuid().ToString("N");
        var nativeHarmony = new Harmony(nativeOwner);
        var externalHarmony = new Harmony(externalOwner);
        try
        {
            nativeHarmony.Patch(target, prefix: new HarmonyMethod(nativePrefix) { priority = Priority.First });
            externalHarmony.Patch(target, prefix: new HarmonyMethod(nativePrefix) { priority = Priority.High });

            var diagnostics = InspectProgressivePrefixes(target, nativeOwner, nativePrefix);

            Assert.AreEqual(1, diagnostics.ExternalPrefixCount);
            Assert.IsTrue(diagnostics.NativePrefixInstalled);
            Assert.IsFalse(diagnostics.NativePrefixUncontended);
            Assert.IsTrue(diagnostics.NativePrefixPriorityStrictlyHigher);
        }
        finally
        {
            externalHarmony.UnpatchAll(externalOwner);
            nativeHarmony.UnpatchAll(nativeOwner);
        }
    }

    [TestMethod]
    public void PrefixDiagnostics_AreUnavailableWithoutAResolvedTarget()
    {
        var diagnostics = InspectProgressivePrefixes(
            null,
            "StrmBridge.Tests.Native",
            GetMethod(nameof(NativePrefix)));

        Assert.IsFalse(diagnostics.IsAvailable);
        Assert.AreEqual(0, diagnostics.ExternalPrefixCount);
        Assert.IsFalse(diagnostics.NativePrefixInstalled);
        Assert.IsNull(diagnostics.NativePrefixPriority);
        Assert.IsNull(diagnostics.HighestExternalPrefixPriority);
        Assert.IsFalse(diagnostics.NativePrefixUncontended);
        Assert.IsFalse(diagnostics.NativePrefixPriorityStrictlyHigher);
    }

    private static MethodInfo GetMethod(string name) =>
        typeof(HarmonyPatchHostTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static ProgressivePrefixDiagnostics InspectProgressivePrefixes(
        MethodBase? target,
        string owner,
        MethodInfo prefix) =>
        HarmonyPatchHost.InspectProgressivePrefixes(
            target,
            owner,
            prefix,
            HarmonyRuntimeAdapter.CreateForAssembly(owner, typeof(Harmony).Assembly));

    private static int Target(int value) => value + 1;

    private static int AmbiguousTarget(int value) => value + 1;

    private static void NativePrefix() { }

    private static void ExternalLowerPrefix() { }

    private static void ExternalEqualPrefix() { }

    private static void ExternalHigherPrefix() { }

    private static void AmbiguousPrefix() { }
}
