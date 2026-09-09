using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Emby.StrmBridge.Playback;

internal sealed class HarmonyRuntimeAdapter
{
    private const string HarmonyTypeName = "HarmonyLib.Harmony";
    private const string HarmonyMethodTypeName = "HarmonyLib.HarmonyMethod";
    private const string HarmonyPatchTypeName = "HarmonyLib.Patch";
    private const string HarmonyPriorityTypeName = "HarmonyLib.Priority";
    private const string HarmonyPatchToolsTypeName = "HarmonyLib.PatchTools";

    private readonly object instance;
    private readonly Type harmonyMethodType;
    private readonly ConstructorInfo harmonyMethodConstructor;
    private readonly MethodInfo patchMethod;
    private readonly MethodInfo unpatchAllMethod;
    private readonly MethodInfo getPatchInfoMethod;
    private readonly MemberInfo harmonyMethodPriorityMember;
    private readonly MemberInfo prefixesMember;
    private readonly MemberInfo ownersMember;
    private readonly MemberInfo patchOwnerMember;
    private readonly MemberInfo patchPriorityMember;
    private readonly MemberInfo patchMethodMember;

    private HarmonyRuntimeAdapter(RuntimeCandidate candidate, string owner, bool shared)
    {
        instance = Invoke(candidate.HarmonyConstructor, new object?[] { owner }) ??
            throw new InvalidOperationException("The Harmony runtime returned no instance.");
        harmonyMethodType = candidate.HarmonyMethodType;
        harmonyMethodConstructor = candidate.HarmonyMethodConstructor;
        patchMethod = candidate.PatchMethod;
        unpatchAllMethod = candidate.UnpatchAllMethod;
        getPatchInfoMethod = candidate.GetPatchInfoMethod;
        harmonyMethodPriorityMember = candidate.HarmonyMethodPriorityMember;
        prefixesMember = candidate.PrefixesMember;
        ownersMember = candidate.OwnersMember;
        patchOwnerMember = candidate.PatchOwnerMember;
        patchPriorityMember = candidate.PatchPriorityMember;
        patchMethodMember = candidate.PatchMethodMember;
        IsShared = shared;
        ActiveMethodCount = candidate.ActiveMethodCount;
        FirstPriority = candidate.FirstPriority;
    }

    internal bool IsShared { get; }

    internal int ActiveMethodCount { get; }

    internal int FirstPriority { get; }

    internal static HarmonyRuntimeAdapter Select(string owner, Func<Assembly> loadBundledRuntime) =>
        Select(owner, AppDomain.CurrentDomain.GetAssemblies(), loadBundledRuntime);

    internal static HarmonyRuntimeAdapter CreateForAssembly(string owner, Assembly assembly)
    {
        if (string.IsNullOrEmpty(owner))
            throw new ArgumentException("A Harmony owner is required.", nameof(owner));
        var candidate = RuntimeCandidate.TryCreate(assembly) ??
            throw new InvalidOperationException("The Harmony runtime is incompatible.");
        return new HarmonyRuntimeAdapter(candidate, owner, shared: false);
    }

    internal static HarmonyRuntimeAdapter Select(
        string owner,
        IEnumerable<Assembly> loadedAssemblies,
        Func<Assembly> loadBundledRuntime)
    {
        if (string.IsNullOrEmpty(owner))
            throw new ArgumentException("A Harmony owner is required.", nameof(owner));
        if (loadedAssemblies is null) throw new ArgumentNullException(nameof(loadedAssemblies));
        if (loadBundledRuntime is null) throw new ArgumentNullException(nameof(loadBundledRuntime));

        var candidates = new List<RuntimeCandidate>();
        foreach (var assembly in loadedAssemblies.Distinct())
        {
            RuntimeCandidate? candidate;
            try { candidate = RuntimeCandidate.TryCreate(assembly); }
            catch (InvalidOperationException exception)
            {
                throw new HarmonyRuntimeSelectionException("candidate-incompatible", exception);
            }
            if (candidate is not null) candidates.Add(candidate);
        }

        var active = candidates.Where(candidate => candidate.ActiveMethodCount > 0).ToArray();
        if (active.Length == 1) return new HarmonyRuntimeAdapter(active[0], owner, shared: true);
        if (active.Length > 1)
            throw new HarmonyRuntimeSelectionException("multiple-active");
        if (candidates.Count == 1)
            return new HarmonyRuntimeAdapter(candidates[0], owner, shared: true);
        if (candidates.Count > 1)
            throw new HarmonyRuntimeSelectionException("multiple-inactive");

        var bundledAssembly = loadBundledRuntime() ??
            throw new HarmonyRuntimeSelectionException("bundled-unavailable");
        RuntimeCandidate? bundled;
        try { bundled = RuntimeCandidate.TryCreate(bundledAssembly); }
        catch (InvalidOperationException exception)
        {
            throw new HarmonyRuntimeSelectionException("bundled-incompatible", exception);
        }
        if (bundled is null)
            throw new HarmonyRuntimeSelectionException("bundled-incompatible");
        return new HarmonyRuntimeAdapter(bundled, owner, shared: false);
    }

    internal void Patch(
        MethodBase target,
        MethodInfo? prefix = null,
        MethodInfo? postfix = null,
        int? prefixPriority = null)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        var prefixDescriptor = CreateHarmonyMethod(prefix, prefixPriority);
        var postfixDescriptor = CreateHarmonyMethod(postfix, null);
        Invoke(patchMethod, instance, new[]
        {
            (object?)target,
            prefixDescriptor,
            postfixDescriptor,
            null,
            null,
        });
    }

    internal HarmonyPatchSnapshot? GetPatchInfo(MethodBase target)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        var patches = Invoke(getPatchInfoMethod, null, new object?[] { target });
        if (patches is null) return null;

        var prefixes = ReadEnumerable(ReadMember(prefixesMember, patches), "prefix collection")
            .Select(ReadPatch)
            .ToArray();
        var owners = ReadEnumerable(ReadMember(ownersMember, patches), "owner collection")
            .Select(value => value as string ??
                throw new InvalidOperationException("The Harmony owner collection is incompatible."))
            .ToArray();
        return new HarmonyPatchSnapshot(prefixes, owners);
    }

    internal void UnpatchAll(string owner)
    {
        if (string.IsNullOrEmpty(owner))
            throw new ArgumentException("A Harmony owner is required.", nameof(owner));
        Invoke(unpatchAllMethod, instance, new object?[] { owner });
    }

    private object? CreateHarmonyMethod(MethodInfo? method, int? priority)
    {
        if (method is null) return null;
        var descriptor = Invoke(harmonyMethodConstructor, new object?[] { method }) ??
            throw new InvalidOperationException("The Harmony runtime returned no patch descriptor.");
        if (!harmonyMethodType.IsInstanceOfType(descriptor))
            throw new InvalidOperationException("The Harmony patch descriptor is incompatible.");
        if (priority.HasValue) WriteMember(harmonyMethodPriorityMember, descriptor, priority.Value);
        return descriptor;
    }

    private HarmonyPatchRecord ReadPatch(object value)
    {
        var owner = ReadMember(patchOwnerMember, value) as string ??
            throw new InvalidOperationException("The Harmony patch owner is incompatible.");
        var priority = ReadMember(patchPriorityMember, value) is int number
            ? number
            : throw new InvalidOperationException("The Harmony patch priority is incompatible.");
        var method = ReadMember(patchMethodMember, value) as MethodInfo ??
            throw new InvalidOperationException("The Harmony patch method is incompatible.");
        return new HarmonyPatchRecord(owner, priority, method);
    }

    private static IEnumerable<object> ReadEnumerable(object? value, string name)
    {
        if (value is not IEnumerable enumerable)
            throw new InvalidOperationException("The Harmony " + name + " is incompatible.");
        foreach (var item in enumerable)
        {
            if (item is null)
                throw new InvalidOperationException("The Harmony " + name + " contains a null value.");
            yield return item;
        }
    }

    private static object? ReadMember(MemberInfo member, object? instance) => member switch
    {
        FieldInfo field => field.GetValue(instance),
        PropertyInfo property => property.GetValue(instance),
        _ => throw new InvalidOperationException("The Harmony member is incompatible."),
    };

    private static void WriteMember(MemberInfo member, object instance, object value)
    {
        switch (member)
        {
            case FieldInfo field:
                field.SetValue(instance, value);
                break;
            case PropertyInfo property:
                property.SetValue(instance, value);
                break;
            default:
                throw new InvalidOperationException("The Harmony member is incompatible.");
        }
    }

    private static object? Invoke(ConstructorInfo constructor, object?[] arguments)
    {
        try { return constructor.Invoke(arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static object? Invoke(MethodInfo method, object? instance, object?[] arguments)
    {
        try { return method.Invoke(instance, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private sealed class RuntimeCandidate
    {
        private RuntimeCandidate(
            Assembly assembly,
            Type harmonyMethodType,
            ConstructorInfo harmonyConstructor,
            ConstructorInfo harmonyMethodConstructor,
            MethodInfo patchMethod,
            MethodInfo unpatchAllMethod,
            MethodInfo getPatchInfoMethod,
            MemberInfo harmonyMethodPriorityMember,
            MemberInfo prefixesMember,
            MemberInfo ownersMember,
            MemberInfo patchOwnerMember,
            MemberInfo patchPriorityMember,
            MemberInfo patchMethodMember,
            int activeMethodCount,
            int firstPriority)
        {
            Assembly = assembly;
            HarmonyMethodType = harmonyMethodType;
            HarmonyConstructor = harmonyConstructor;
            HarmonyMethodConstructor = harmonyMethodConstructor;
            PatchMethod = patchMethod;
            UnpatchAllMethod = unpatchAllMethod;
            GetPatchInfoMethod = getPatchInfoMethod;
            HarmonyMethodPriorityMember = harmonyMethodPriorityMember;
            PrefixesMember = prefixesMember;
            OwnersMember = ownersMember;
            PatchOwnerMember = patchOwnerMember;
            PatchPriorityMember = patchPriorityMember;
            PatchMethodMember = patchMethodMember;
            ActiveMethodCount = activeMethodCount;
            FirstPriority = firstPriority;
        }

        internal Assembly Assembly { get; }
        internal Type HarmonyMethodType { get; }
        internal ConstructorInfo HarmonyConstructor { get; }
        internal ConstructorInfo HarmonyMethodConstructor { get; }
        internal MethodInfo PatchMethod { get; }
        internal MethodInfo UnpatchAllMethod { get; }
        internal MethodInfo GetPatchInfoMethod { get; }
        internal MemberInfo HarmonyMethodPriorityMember { get; }
        internal MemberInfo PrefixesMember { get; }
        internal MemberInfo OwnersMember { get; }
        internal MemberInfo PatchOwnerMember { get; }
        internal MemberInfo PatchPriorityMember { get; }
        internal MemberInfo PatchMethodMember { get; }
        internal int ActiveMethodCount { get; }
        internal int FirstPriority { get; }

        internal static RuntimeCandidate? TryCreate(Assembly assembly)
        {
            if (assembly is null) throw new ArgumentNullException(nameof(assembly));
            var harmonyType = assembly.GetType(HarmonyTypeName, throwOnError: false);
            var harmonyMethodType = assembly.GetType(HarmonyMethodTypeName, throwOnError: false);
            if (harmonyType is null && harmonyMethodType is null) return null;
            if (harmonyType is null || harmonyMethodType is null)
                throw new InvalidOperationException("A loaded Harmony runtime has an incomplete public surface.");

            var harmonyConstructor = harmonyType.GetConstructor(new[] { typeof(string) });
            var harmonyMethodConstructor = harmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) });
            var patchMethod = harmonyType.GetMethod(
                "Patch",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                new[]
                {
                    typeof(MethodBase),
                    harmonyMethodType,
                    harmonyMethodType,
                    harmonyMethodType,
                    harmonyMethodType,
                },
                modifiers: null);
            var unpatchAllMethod = harmonyType.GetMethod(
                "UnpatchAll",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                new[] { typeof(string) },
                modifiers: null);
            var getPatchInfoMethod = harmonyType.GetMethod(
                "GetPatchInfo",
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                new[] { typeof(MethodBase) },
                modifiers: null);
            var getAllPatchedMethodsMethod = harmonyType.GetMethod(
                "GetAllPatchedMethods",
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);
            var harmonyMethodPriorityMember = FindReadableWritableMember(harmonyMethodType, "priority", typeof(int));
            var patchType = assembly.GetType(HarmonyPatchTypeName, throwOnError: false);
            var priorityType = assembly.GetType(HarmonyPriorityTypeName, throwOnError: false);
            var patchToolsType = assembly.GetType(HarmonyPatchToolsTypeName, throwOnError: false);

            if (harmonyConstructor is null || harmonyMethodConstructor is null || patchMethod is null ||
                patchMethod.ReturnType != typeof(MethodInfo) || unpatchAllMethod?.ReturnType != typeof(void) ||
                getPatchInfoMethod is null || getPatchInfoMethod.ReturnType == typeof(void) ||
                getAllPatchedMethodsMethod is null ||
                !typeof(IEnumerable<MethodBase>).IsAssignableFrom(getAllPatchedMethodsMethod.ReturnType) ||
                harmonyMethodPriorityMember is null || patchType is null || priorityType is null ||
                patchToolsType is null)
                throw new InvalidOperationException("A loaded Harmony runtime has an incompatible public surface.");

            var prefixesMember = FindReadableMember(getPatchInfoMethod.ReturnType, "Prefixes");
            var ownersMember = FindReadableMember(getPatchInfoMethod.ReturnType, "Owners");
            var patchOwnerMember = FindReadableMember(patchType, "owner", typeof(string));
            var patchPriorityMember = FindReadableMember(patchType, "priority", typeof(int));
            var patchMethodMember = FindReadableMember(patchType, "PatchMethod", typeof(MethodInfo));
            var firstPriorityField = priorityType.GetField("First", BindingFlags.Static | BindingFlags.Public);
            var detoursField = patchToolsType.GetField(
                "detours",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prefixesMember is null || ownersMember is null || patchOwnerMember is null ||
                patchPriorityMember is null || patchMethodMember is null || firstPriorityField is null ||
                firstPriorityField.FieldType != typeof(int) || !firstPriorityField.IsLiteral || detoursField is null)
                throw new InvalidOperationException("A loaded Harmony runtime has an incompatible diagnostic surface.");

            var firstPriority = firstPriorityField.GetRawConstantValue() is int value
                ? value
                : throw new InvalidOperationException("The Harmony priority contract is incompatible.");
            var detours = detoursField.GetValue(null) ??
                throw new InvalidOperationException("The Harmony detour registry is unavailable.");
            var countProperty = detours.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            if (countProperty?.PropertyType != typeof(int) || countProperty.GetMethod is null)
                throw new InvalidOperationException("The Harmony detour registry is incompatible.");
            var activeMethodCount = countProperty.GetValue(detours) is int count && count >= 0
                ? count
                : throw new InvalidOperationException("The Harmony detour registry count is invalid.");

            return new RuntimeCandidate(
                assembly,
                harmonyMethodType,
                harmonyConstructor,
                harmonyMethodConstructor,
                patchMethod,
                unpatchAllMethod,
                getPatchInfoMethod,
                harmonyMethodPriorityMember,
                prefixesMember,
                ownersMember,
                patchOwnerMember,
                patchPriorityMember,
                patchMethodMember,
                activeMethodCount,
                firstPriority);
        }

        private static MemberInfo? FindReadableMember(Type type, string name, Type? memberType = null)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field is not null && (memberType is null || field.FieldType == memberType)) return field;
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property?.GetMethod is not null &&
                   (memberType is null || property.PropertyType == memberType)
                ? property
                : null;
        }

        private static MemberInfo? FindReadableWritableMember(Type type, string name, Type memberType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field is not null && field.FieldType == memberType && !field.IsInitOnly) return field;
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property?.PropertyType == memberType && property.GetMethod is not null && property.SetMethod is not null
                ? property
                : null;
        }
    }
}

internal sealed class HarmonyRuntimeSelectionException : InvalidOperationException
{
    internal HarmonyRuntimeSelectionException(string reasonCode, Exception? innerException = null)
        : base("Harmony runtime selection failed.", innerException)
    {
        if (string.IsNullOrEmpty(reasonCode))
            throw new ArgumentException("A selection reason is required.", nameof(reasonCode));
        ReasonCode = reasonCode;
    }

    internal string ReasonCode { get; }
}

internal sealed class HarmonyPatchSnapshot
{
    internal HarmonyPatchSnapshot(
        IReadOnlyList<HarmonyPatchRecord> prefixes,
        IReadOnlyCollection<string> owners)
    {
        Prefixes = prefixes;
        Owners = owners;
    }

    internal IReadOnlyList<HarmonyPatchRecord> Prefixes { get; }

    internal IReadOnlyCollection<string> Owners { get; }
}

internal readonly struct HarmonyPatchRecord
{
    internal HarmonyPatchRecord(string owner, int priority, MethodInfo method)
    {
        Owner = owner;
        Priority = priority;
        Method = method;
    }

    internal string Owner { get; }

    internal int Priority { get; }

    internal MethodInfo Method { get; }
}
