using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using Emby.StrmBridge.Playback;
using HarmonyLib;

namespace Emby.StrmBridge.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HarmonyRuntimeAdapterTests
{
    [TestMethod]
    public void Select_PrefersActiveCompatibleRuntimeWithoutLoadingBundledRuntime()
    {
        var inactive = CreateCompatibleRuntime(activeMethodCount: 0);
        var active = CreateCompatibleRuntime(activeMethodCount: 3);
        var loadCount = 0;
        try
        {
            var runtime = HarmonyRuntimeAdapter.Select(
                NewOwner(),
                new[] { inactive, active },
                () =>
                {
                    loadCount++;
                    return inactive;
                });

            Assert.IsTrue(runtime.IsShared);
            Assert.AreEqual(3, runtime.ActiveMethodCount);
            Assert.AreEqual(0, loadCount);
        }
        finally
        {
            ClearActivity(active);
        }
    }

    [TestMethod]
    public void Select_ReusesSingleInactiveCompatibleRuntimeWithoutLoadingBundledRuntime()
    {
        var inactive = CreateCompatibleRuntime(activeMethodCount: 0);
        var loadCount = 0;

        var runtime = HarmonyRuntimeAdapter.Select(
            NewOwner(),
            new[] { inactive },
            () =>
            {
                loadCount++;
                return inactive;
            });

        Assert.IsTrue(runtime.IsShared);
        Assert.AreEqual(0, runtime.ActiveMethodCount);
        Assert.AreEqual(0, loadCount);
    }

    [TestMethod]
    public void Select_LoadsBundledRuntimeOnlyWhenNoCompatibleRuntimeIsLoaded()
    {
        var bundled = CreateCompatibleRuntime(activeMethodCount: 0);
        var loadCount = 0;

        var runtime = HarmonyRuntimeAdapter.Select(
            NewOwner(),
            Array.Empty<Assembly>(),
            () =>
            {
                loadCount++;
                return bundled;
            });

        Assert.IsFalse(runtime.IsShared);
        Assert.AreEqual(0, runtime.ActiveMethodCount);
        Assert.AreEqual(1, loadCount);
    }

    [TestMethod]
    public void Select_FailsClosedWhenMultipleActiveCompatibleRuntimesAreLoaded()
    {
        var first = CreateCompatibleRuntime(activeMethodCount: 1);
        var second = CreateCompatibleRuntime(activeMethodCount: 2);
        var loadCount = 0;
        try
        {
            var exception = Assert.ThrowsExactly<HarmonyRuntimeSelectionException>(() =>
                HarmonyRuntimeAdapter.Select(
                    NewOwner(),
                    new[] { first, second },
                    () =>
                    {
                        loadCount++;
                        return first;
                    }));

            Assert.AreEqual("multiple-active", exception.ReasonCode);
            Assert.AreEqual(0, loadCount);
        }
        finally
        {
            ClearActivity(first);
            ClearActivity(second);
        }
    }

    [TestMethod]
    public void Select_FailsClosedWhenMultipleInactiveCompatibleRuntimesAreLoaded()
    {
        var first = CreateCompatibleRuntime(activeMethodCount: 0);
        var second = CreateCompatibleRuntime(activeMethodCount: 0);
        var loadCount = 0;

        var exception = Assert.ThrowsExactly<HarmonyRuntimeSelectionException>(() =>
            HarmonyRuntimeAdapter.Select(
                NewOwner(),
                new[] { first, second },
                () =>
                {
                    loadCount++;
                    return first;
                }));

        Assert.AreEqual("multiple-inactive", exception.ReasonCode);
        Assert.AreEqual(0, loadCount);
    }

    [TestMethod]
    public void Select_FailsClosedWhenBundledRuntimeIsIncompatible()
    {
        var exception = Assert.ThrowsExactly<HarmonyRuntimeSelectionException>(() => HarmonyRuntimeAdapter.Select(
            NewOwner(),
            Array.Empty<Assembly>(),
            () => typeof(string).Assembly));

        Assert.AreEqual("bundled-incompatible", exception.ReasonCode);
    }

    [TestMethod]
    public void Select_FailsClosedWithAStableReasonForAnIncompleteLoadedRuntime()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("IncompleteHarmonyRuntimeFixture." + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        assembly.DefineDynamicModule("main")
            .DefineType("HarmonyLib.Harmony", TypeAttributes.Public | TypeAttributes.Class)
            .CreateType();
        var loadCount = 0;

        var exception = Assert.ThrowsExactly<HarmonyRuntimeSelectionException>(() =>
            HarmonyRuntimeAdapter.Select(
                NewOwner(),
                new[] { assembly },
                () =>
                {
                    loadCount++;
                    return typeof(string).Assembly;
                }));

        Assert.AreEqual("candidate-incompatible", exception.ReasonCode);
        Assert.AreEqual(0, loadCount);
    }

    [TestMethod]
    public void Select_UnwrapsCompatibleRuntimeConstructorFailure()
    {
        var active = CreateCompatibleRuntime(activeMethodCount: 1, throwFromConstructor: true);
        try
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                HarmonyRuntimeAdapter.Select(NewOwner(), new[] { active }, () => active));

            Assert.AreEqual("fixture-constructor", exception.Message);
        }
        finally
        {
            ClearActivity(active);
        }
    }

    [TestMethod]
    public void Adapter_PatchesInspectsAndUnpatchesWithoutStaticHarmonyCalls()
    {
        var owner = NewOwner();
        var target = GetMethod(nameof(Target));
        var prefix = GetMethod(nameof(Prefix));
        var runtime = HarmonyRuntimeAdapter.CreateForAssembly(owner, typeof(Harmony).Assembly);
        try
        {
            runtime.Patch(target, prefix: prefix, prefixPriority: runtime.FirstPriority);

            var snapshot = runtime.GetPatchInfo(target);

            Assert.IsNotNull(snapshot);
            Assert.AreEqual(1, snapshot.Owners.Count(value => value == owner));
            var installed = snapshot.Prefixes.Single(value => value.Owner == owner);
            Assert.AreEqual(prefix, installed.Method);
            Assert.AreEqual(runtime.FirstPriority, installed.Priority);
        }
        finally
        {
            runtime.UnpatchAll(owner);
        }
    }

    private static Assembly CreateCompatibleRuntime(int activeMethodCount, bool throwFromConstructor = false)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("HarmonyRuntimeFixture." + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("main");

        var harmonyMethodBuilder = module.DefineType(
            "HarmonyLib.HarmonyMethod",
            TypeAttributes.Public | TypeAttributes.Class);
        var priorityField = harmonyMethodBuilder.DefineField("priority", typeof(int), FieldAttributes.Public);
        var harmonyMethodConstructor = harmonyMethodBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeof(MethodInfo) });
        var il = harmonyMethodConstructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Stfld, priorityField);
        il.Emit(OpCodes.Ret);
        var harmonyMethodType = harmonyMethodBuilder.CreateType()!;

        var patchBuilder = module.DefineType("HarmonyLib.Patch", TypeAttributes.Public | TypeAttributes.Class);
        patchBuilder.DefineField("owner", typeof(string), FieldAttributes.Public);
        patchBuilder.DefineField("priority", typeof(int), FieldAttributes.Public);
        var storedPatchMethod = patchBuilder.DefineField("storedPatchMethod", typeof(MethodInfo), FieldAttributes.Private);
        var patchMethodProperty = patchBuilder.DefineProperty(
            "PatchMethod", PropertyAttributes.None, typeof(MethodInfo), Type.EmptyTypes);
        var patchMethodGetter = patchBuilder.DefineMethod(
            "get_PatchMethod",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(MethodInfo),
            Type.EmptyTypes);
        il = patchMethodGetter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, storedPatchMethod);
        il.Emit(OpCodes.Ret);
        patchMethodProperty.SetGetMethod(patchMethodGetter);
        patchBuilder.DefineDefaultConstructor(MethodAttributes.Public);
        var patchType = patchBuilder.CreateType()!;

        var patchesBuilder = module.DefineType("HarmonyLib.Patches", TypeAttributes.Public | TypeAttributes.Class);
        patchesBuilder.DefineField("Prefixes", patchType.MakeArrayType(), FieldAttributes.Public);
        var ownersProperty = patchesBuilder.DefineProperty(
            "Owners", PropertyAttributes.None, typeof(string[]), Type.EmptyTypes);
        var ownersGetter = patchesBuilder.DefineMethod(
            "get_Owners",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(string[]),
            Type.EmptyTypes);
        il = ownersGetter.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Newarr, typeof(string));
        il.Emit(OpCodes.Ret);
        ownersProperty.SetGetMethod(ownersGetter);
        patchesBuilder.DefineDefaultConstructor(MethodAttributes.Public);
        var patchesType = patchesBuilder.CreateType()!;

        var priorityBuilder = module.DefineType(
            "HarmonyLib.Priority",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var firstField = priorityBuilder.DefineField(
            "First",
            typeof(int),
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal);
        firstField.SetConstant(800);
        priorityBuilder.CreateType();

        var patchToolsBuilder = module.DefineType(
            "HarmonyLib.PatchTools",
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var detoursField = patchToolsBuilder.DefineField(
            "detours",
            typeof(List<object>),
            FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
        var typeInitializer = patchToolsBuilder.DefineTypeInitializer();
        il = typeInitializer.GetILGenerator();
        il.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
        for (var index = 0; index < activeMethodCount; index++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Newobj, typeof(object).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod(nameof(List<object>.Add))!);
        }
        il.Emit(OpCodes.Stsfld, detoursField);
        il.Emit(OpCodes.Ret);
        patchToolsBuilder.CreateType();

        var harmonyBuilder = module.DefineType("HarmonyLib.Harmony", TypeAttributes.Public | TypeAttributes.Class);
        var harmonyConstructor = harmonyBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeof(string) });
        il = harmonyConstructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        if (throwFromConstructor)
        {
            il.Emit(OpCodes.Ldstr, "fixture-constructor");
            il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
            il.Emit(OpCodes.Throw);
        }
        else
        {
            il.Emit(OpCodes.Ret);
        }

        var patch = harmonyBuilder.DefineMethod(
            "Patch",
            MethodAttributes.Public,
            typeof(MethodInfo),
            new[]
            {
                typeof(MethodBase),
                harmonyMethodType,
                harmonyMethodType,
                harmonyMethodType,
                harmonyMethodType,
            });
        il = patch.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        var unpatchAll = harmonyBuilder.DefineMethod(
            "UnpatchAll", MethodAttributes.Public, typeof(void), new[] { typeof(string) });
        il = unpatchAll.GetILGenerator();
        il.Emit(OpCodes.Ret);

        var getPatchInfo = harmonyBuilder.DefineMethod(
            "GetPatchInfo",
            MethodAttributes.Public | MethodAttributes.Static,
            patchesType,
            new[] { typeof(MethodBase) });
        il = getPatchInfo.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        var getAllPatchedMethods = harmonyBuilder.DefineMethod(
            "GetAllPatchedMethods",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(IEnumerable<MethodBase>),
            Type.EmptyTypes);
        il = getAllPatchedMethods.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        harmonyBuilder.CreateType();

        return assembly;
    }

    private static void ClearActivity(Assembly assembly)
    {
        var field = assembly.GetType("HarmonyLib.PatchTools")!
            .GetField("detours", BindingFlags.Static | BindingFlags.NonPublic)!;
        ((IList)field.GetValue(null)!).Clear();
    }

    private static MethodInfo GetMethod(string name) =>
        typeof(HarmonyRuntimeAdapterTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static string NewOwner() => "StrmBridge.Tests.Runtime." + Guid.NewGuid().ToString("N");

    private static int Target(int value) => value + 1;

    private static void Prefix() { }
}
