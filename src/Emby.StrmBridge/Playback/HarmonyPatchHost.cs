using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Playback;

public enum PlaybackPatchStatus
{
    NativeOnly = 0,
    Ready = 1,
    Failed = 2,
}

public sealed class HarmonyPatchHost : IDisposable
{
    private const string PatchId = "Emby.StrmBridge.Playback.v4";
    private const string PlaybackInfoServiceTypeName = "Emby.Server.MediaEncoding.Api.MediaInfoService";
    private const string VideoServiceTypeName = "Emby.Server.MediaEncoding.Api.Progressive.VideoService";
    private const string ProgressiveServiceTypeName =
        "Emby.Server.MediaEncoding.Api.Progressive.BaseProgressiveStreamingService";
    private const string BaseStreamingServiceTypeName =
        "Emby.Server.MediaEncoding.Api.BaseStreamingService";
    private const string FfmpegRunnerTypeName =
        "Emby.Server.MediaEncoding.Unified.Ffmpeg.FfmpegRunner";
    private const string EncodingManagerTypeName = "Emby.Server.MediaEncoding.Api.EncodingManager";
    private static readonly MethodInfo NativePrefixMethod = typeof(NativeStreamPatchBridge).GetMethod(
        nameof(NativeStreamPatchBridge.Prefix),
        BindingFlags.Static | BindingFlags.Public) ??
        throw new MissingMethodException(
            typeof(NativeStreamPatchBridge).FullName,
            nameof(NativeStreamPatchBridge.Prefix));
    private readonly PlaybackInfoProcessor processor;
    private readonly NativeVideoStreamProcessor nativeStreamProcessor;
    private readonly TranscodeInputProcessor transcodeInputProcessor;
    private readonly FfmpegCommandProcessor ffmpegCommandProcessor;
    private readonly ILogger logger;
    private readonly object lifecycleSync = new();
    private MethodInfo? nativeStreamTarget;
    private HarmonyRuntimeAdapter? harmonyRuntime;
    private bool disposed;

    internal HarmonyPatchHost(
        PlaybackInfoProcessor processor,
        NativeVideoStreamProcessor nativeStreamProcessor,
        TranscodeInputProcessor transcodeInputProcessor,
        FfmpegCommandProcessor ffmpegCommandProcessor,
        ILogManager logManager)
    {
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        this.nativeStreamProcessor = nativeStreamProcessor ??
            throw new ArgumentNullException(nameof(nativeStreamProcessor));
        this.transcodeInputProcessor = transcodeInputProcessor ??
            throw new ArgumentNullException(nameof(transcodeInputProcessor));
        this.ffmpegCommandProcessor = ffmpegCommandProcessor ??
            throw new ArgumentNullException(nameof(ffmpegCommandProcessor));
        logger = (logManager ?? throw new ArgumentNullException(nameof(logManager)))
            .GetLogger(Plugin.Instance?.Name ?? "STRM Bridge");
    }

    public PlaybackPatchStatus Status { get; private set; } = PlaybackPatchStatus.NativeOnly;

    public string HostAbi { get; private set; } = "unavailable";

    public PlaybackPatchStatus Install()
    {
        lock (lifecycleSync) return InstallCore();
    }

    private PlaybackPatchStatus InstallCore()
    {
        if (disposed) throw new ObjectDisposedException(nameof(HarmonyPatchHost));
        if (harmonyRuntime is not null) return Status;
        var stage = "resolve-service-types";
        try
        {
            var playbackInfoServiceType = ResolveServiceType(PlaybackInfoServiceTypeName);
            var videoServiceType = ResolveServiceType(VideoServiceTypeName);
            var progressiveServiceType = ResolveServiceType(ProgressiveServiceTypeName);
            var baseStreamingServiceType = ResolveServiceType(BaseStreamingServiceTypeName);
            var ffmpegRunnerType = ResolveServiceType(FfmpegRunnerTypeName);
            var encodingManagerType = ResolveServiceType(EncodingManagerTypeName);
            stage = "validate-host-abi";
            var version = playbackInfoServiceType.Assembly.GetName().Version ?? new Version(0, 0);
            HostAbi = version.ToString();
            if (!IsSupportedHostAbi(version,
                    videoServiceType.Assembly.GetName().Version,
                    progressiveServiceType.Assembly.GetName().Version,
                    baseStreamingServiceType.Assembly.GetName().Version,
                    ffmpegRunnerType.Assembly.GetName().Version,
                    encodingManagerType.Assembly.GetName().Version))
            {
                logger.Warn("STRM_BRIDGE_PATCH_ABI_UNSUPPORTED abi=" + HostAbi);
                Status = PlaybackPatchStatus.NativeOnly;
                return Status;
            }

            stage = "resolve-playback-info-targets";
            var playbackInfoTargets = new[]
            {
                FindTarget(playbackInfoServiceType, "Get", "Emby.Server.MediaEncoding.Api.GetPlaybackInfo"),
                FindTarget(playbackInfoServiceType, "Post", "Emby.Server.MediaEncoding.Api.GetPostedPlaybackInfo"),
            };
            stage = "resolve-native-stream-target";
            var nativeTarget = FindTarget(
                progressiveServiceType,
                "ProcessRequest",
                "Emby.Server.MediaEncoding.Api.StreamRequest",
                typeof(bool).FullName!);
            nativeStreamTarget = nativeTarget;
            stage = "select-harmony-runtime";
            harmonyRuntime = HarmonyRuntimeAdapter.Select(PatchId, EmbeddedAssemblyLoader.EnsureHarmonyLoaded);
            logger.Info("STRM_BRIDGE_HARMONY_RUNTIME shared=" +
                        harmonyRuntime.IsShared.ToString().ToLowerInvariant() +
                        " activeMethods=" + harmonyRuntime.ActiveMethodCount);
            stage = "inspect-native-stream-prefixes";
            var foreignProgressivePrefixes = harmonyRuntime.GetPatchInfo(nativeTarget)?.Prefixes.Count(
                patch => !string.Equals(patch.Owner, PatchId, StringComparison.Ordinal)) ?? 0;
            if (foreignProgressivePrefixes > 0)
                logger.Warn("STRM_BRIDGE_PATCH_FOREIGN_PROGRESSIVE_PREFIXES count=" +
                            foreignProgressivePrefixes);
            stage = "resolve-transcode-input-target";
            var transcodeInputTarget = FindTaskTarget(
                baseStreamingServiceType,
                "StartFfMpeg",
                "Emby.Server.MediaEncoding.Api.TranscodingJob",
                "Emby.Server.MediaEncoding.Api.StreamState",
                typeof(string).FullName!,
                "System.Threading.CancellationToken",
                typeof(bool).FullName!);
            stage = "resolve-ffmpeg-command-target";
            var ffmpegCommandTarget = FindTaskTarget(
                ffmpegRunnerType,
                "Start",
                typeof(bool).FullName!,
                "Emby.Ffmpeg.Model.FfmpegCommand",
                "System.Threading.CancellationToken");
            stage = "validate-ffmpeg-http-profile";
            var httpProtocolType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Emby.Ffmpeg.Lib.InputProtocols.http", false))
                .FirstOrDefault(type => type is not null) ??
                Type.GetType("Emby.Ffmpeg.Lib.InputProtocols.http, Emby.Ffmpeg.Lib", true)!;
            foreach (var propertyName in new[] { "user_agent", "headers" })
            {
                var property = httpProtocolType.GetProperty(propertyName);
                if (property?.PropertyType != typeof(string) || property.SetMethod is null)
                    throw new MissingMemberException("The FFmpeg HTTP request-profile ABI is unavailable.");
            }
            stage = "resolve-transcode-cleanup-target";
            var jobType = transcodeInputTarget.ReturnType.GetGenericArguments()[0];
            var cleanupTarget = encodingManagerType.GetMethod("DeletePartialStreamFiles",
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { jobType, typeof(int), typeof(int) }, null);
            var jobKind = jobType.GetProperty("Type")?.PropertyType;
            if (cleanupTarget?.ReturnType != typeof(Task) ||
                encodingManagerType.GetField("_fileSystem", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType !=
                    typeof(MediaBrowser.Model.IO.IFileSystem) || jobKind is null ||
                encodingManagerType.GetMethod("GetTranscodingJob", new[] { typeof(string), jobKind })?.ReturnType != jobType ||
                jobType.GetProperty("MediaSource")?.PropertyType != typeof(MediaBrowser.Model.Dto.MediaSourceInfo) ||
                jobType.GetProperty("Path")?.PropertyType != typeof(string))
                throw new MissingMethodException("Transcode cleanup ABI is unavailable.");
            stage = "attach-patch-bridges";
            TranscodeInputPatchBridge.PrepareResultType(jobType);
            PlaybackPatchBridge.Attach(processor);
            NativeStreamPatchBridge.Attach(nativeStreamProcessor);
            TranscodeInputPatchBridge.Attach(transcodeInputProcessor);
            FfmpegCommandPatchBridge.Attach(ffmpegCommandProcessor);
            var postfix = GetBridgeMethod(typeof(PlaybackPatchBridge), nameof(PlaybackPatchBridge.Postfix));
            var transcodeInputPrefix = GetBridgeMethod(
                typeof(TranscodeInputPatchBridge), nameof(TranscodeInputPatchBridge.Prefix));
            var transcodeInputPostfix = GetBridgeMethod(
                typeof(TranscodeInputPatchBridge), nameof(TranscodeInputPatchBridge.Postfix));
            var cleanupPrefix = GetBridgeMethod(
                typeof(TranscodeInputPatchBridge), nameof(TranscodeInputPatchBridge.CleanupPrefix));
            var ffmpegCommandPrefix = GetBridgeMethod(
                typeof(FfmpegCommandPatchBridge), nameof(FfmpegCommandPatchBridge.Prefix));
            var ffmpegCommandPostfix = GetBridgeMethod(
                typeof(FfmpegCommandPatchBridge), nameof(FfmpegCommandPatchBridge.Postfix));
            stage = "patch-playback-info-get";
            harmonyRuntime.Patch(playbackInfoTargets[0], postfix: postfix);
            stage = "patch-playback-info-post";
            harmonyRuntime.Patch(playbackInfoTargets[1], postfix: postfix);
            stage = "patch-native-stream";
            harmonyRuntime.Patch(
                nativeTarget,
                prefix: NativePrefixMethod,
                prefixPriority: harmonyRuntime.FirstPriority);
            stage = "patch-transcode-input";
            harmonyRuntime.Patch(
                transcodeInputTarget,
                prefix: transcodeInputPrefix,
                postfix: transcodeInputPostfix);
            stage = "patch-transcode-cleanup";
            harmonyRuntime.Patch(cleanupTarget, prefix: cleanupPrefix);
            stage = "patch-ffmpeg-command";
            harmonyRuntime.Patch(
                ffmpegCommandTarget,
                prefix: ffmpegCommandPrefix,
                postfix: ffmpegCommandPostfix);
            var targets = playbackInfoTargets
                .Append(nativeTarget)
                .Append(transcodeInputTarget)
                .Append(ffmpegCommandTarget)
                .Append(cleanupTarget)
                .ToArray();
            stage = "validate-patch-ownership";
            if (targets.Any(target => harmonyRuntime.GetPatchInfo(target)?.Owners.Contains(PatchId) != true))
                throw new InvalidOperationException("Playback patch ownership validation failed.");
            Status = PlaybackPatchStatus.Ready;
            logger.Info("STRM_BRIDGE_PATCH_READY abi=" + HostAbi + " targets=" + targets.Length);
        }
        catch (Exception exception)
        {
            var rollbackException = TryUnpatchAll();
            PlaybackPatchBridge.Detach(processor);
            NativeStreamPatchBridge.Detach(nativeStreamProcessor);
            TranscodeInputPatchBridge.Detach(transcodeInputProcessor);
            FfmpegCommandPatchBridge.Detach(ffmpegCommandProcessor);
            Status = PlaybackPatchStatus.Failed;
            var reason = exception is HarmonyRuntimeSelectionException selectionException
                ? " reason=" + selectionException.ReasonCode
                : string.Empty;
            logger.Warn("STRM_BRIDGE_PATCH_FAILED stage=" + stage +
                        reason +
                        " error=" + exception.GetType().Name +
                        " hresult=" + exception.HResult.ToString("X8"));
            if (rollbackException is not null)
                logger.Warn("STRM_BRIDGE_PATCH_ROLLBACK_FAILED error=" +
                            rollbackException.GetType().Name +
                            " hresult=" + rollbackException.HResult.ToString("X8"));
        }
        return Status;
    }

    // These release lines share the six integration contracts checked below. Keep the
    // 4.9 baseline and admit the verified 4.10 stable line, not earlier 4.10 previews.
    // A version match never replaces signature/member checks or patch ownership checks.
    internal static bool IsSupportedHostAbi(Version version, params Version?[] componentVersions) =>
        version.Major == 4 &&
        ((version.Minor == 9 && version.Build == 5 && version.Revision >= 0) ||
         (version.Minor == 10 && version.Build == 0 && version.Revision >= 40)) &&
        componentVersions.All(component => component == version);

    internal ProgressivePrefixDiagnostics GetProgressivePrefixDiagnostics()
    {
        lock (lifecycleSync)
        {
            if (Status != PlaybackPatchStatus.Ready)
                return ProgressivePrefixDiagnostics.Unavailable;
            try
            {
                return InspectProgressivePrefixes(
                    nativeStreamTarget,
                    PatchId,
                    NativePrefixMethod,
                    harmonyRuntime);
            }
            catch (Exception exception) when (IsRecoverableDiagnosticException(exception))
            {
                return ProgressivePrefixDiagnostics.Unavailable;
            }
        }
    }

    internal static ProgressivePrefixDiagnostics InspectProgressivePrefixes(
        MethodBase? target,
        string nativeOwner,
        MethodInfo nativePrefixMethod,
        HarmonyRuntimeAdapter? runtime)
    {
        if (target is null || runtime is null) return ProgressivePrefixDiagnostics.Unavailable;
        if (string.IsNullOrEmpty(nativeOwner))
            throw new ArgumentException("A native patch owner is required.", nameof(nativeOwner));
        if (nativePrefixMethod is null) throw new ArgumentNullException(nameof(nativePrefixMethod));

        var prefixes = runtime.GetPatchInfo(target)?.Prefixes.ToArray() ??
            Array.Empty<HarmonyPatchRecord>();
        bool IsNative(HarmonyPatchRecord patch) =>
            string.Equals(patch.Owner, nativeOwner, StringComparison.Ordinal) &&
            Equals(patch.Method, nativePrefixMethod);
        var nativePrefixes = prefixes.Where(IsNative).ToArray();
        var externalPrefixes = prefixes.Where(patch => !IsNative(patch)).ToArray();
        int? nativePriority = nativePrefixes.Length == 0
            ? null
            : nativePrefixes.Max(patch => patch.Priority);
        int? highestExternalPriority = externalPrefixes.Length == 0
            ? null
            : externalPrefixes.Max(patch => patch.Priority);
        return new ProgressivePrefixDiagnostics(
            isAvailable: true,
            externalPrefixCount: externalPrefixes.Length,
            nativePrefixInstalled: nativePrefixes.Length > 0,
            nativePrefixPriority: nativePriority,
            highestExternalPrefixPriority: highestExternalPriority,
            nativePrefixUncontended: nativePrefixes.Length > 0 && externalPrefixes.Length == 0,
            nativePrefixPriorityStrictlyHigher: nativePriority.HasValue &&
                highestExternalPriority.HasValue && nativePriority.Value > highestExternalPriority.Value);
    }

    private static bool IsRecoverableDiagnosticException(Exception exception) =>
        exception is not OutOfMemoryException &&
        exception is not StackOverflowException &&
        exception is not AccessViolationException;

    public void Dispose()
    {
        lock (lifecycleSync) DisposeCore();
    }

    private void DisposeCore()
    {
        if (disposed)
        {
            var retryException = TryUnpatchAll();
            if (retryException is not null)
                LogDisposeFailure(retryException);
            return;
        }
        disposed = true;
        var rollbackException = TryUnpatchAll();
        if (rollbackException is not null)
            LogDisposeFailure(rollbackException);
        PlaybackPatchBridge.Detach(processor);
        NativeStreamPatchBridge.Detach(nativeStreamProcessor);
        TranscodeInputPatchBridge.Detach(transcodeInputProcessor);
        FfmpegCommandPatchBridge.Detach(ffmpegCommandProcessor);
        Status = PlaybackPatchStatus.NativeOnly;
    }

    private void LogDisposeFailure(Exception exception) =>
        logger.Warn("STRM_BRIDGE_PATCH_DISPOSE_FAILED error=" +
                    exception.GetType().Name +
                    " hresult=" + exception.HResult.ToString("X8"));

    private Exception? TryUnpatchAll()
    {
        var current = harmonyRuntime;
        if (current is null) return null;
        try
        {
            current.UnpatchAll(PatchId);
            harmonyRuntime = null;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static MethodInfo GetBridgeMethod(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Static | BindingFlags.Public) ??
        throw new MissingMethodException(type.FullName, name);

    private static Type ResolveServiceType(string serviceTypeName)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(serviceTypeName, throwOnError: false))
            .FirstOrDefault(candidate => candidate is not null);
        return type ?? Type.GetType(serviceTypeName + ", Emby.Server.MediaEncoding", throwOnError: true)!;
    }

    private static MethodInfo FindTarget(Type serviceType, string name, params string[] parameterTypeNames)
    {
        var target = serviceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method =>
                string.Equals(method.Name, name, StringComparison.Ordinal) &&
                method.ReturnType == typeof(Task<object>) &&
                method.GetParameters().Select(parameter => parameter.ParameterType.FullName)
                    .SequenceEqual(parameterTypeNames, StringComparer.Ordinal));
        return target ?? throw new MissingMethodException(
            serviceType.FullName,
            name + "(" + string.Join(",", parameterTypeNames) + ")");
    }

    private static MethodInfo FindTaskTarget(
        Type serviceType,
        string name,
        string resultTypeName,
        params string[] parameterTypeNames)
    {
        var target = serviceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method =>
                string.Equals(method.Name, name, StringComparison.Ordinal) &&
                method.ReturnType.IsGenericType &&
                method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) &&
                string.Equals(
                    method.ReturnType.GetGenericArguments()[0].FullName,
                    resultTypeName,
                    StringComparison.Ordinal) &&
                method.GetParameters().Select(parameter => parameter.ParameterType.FullName)
                    .SequenceEqual(parameterTypeNames, StringComparer.Ordinal));
        return target ?? throw new MissingMethodException(
            serviceType.FullName,
            name + "(" + string.Join(",", parameterTypeNames) + ")");
    }
}

public readonly struct ProgressivePrefixDiagnostics
{
    public static ProgressivePrefixDiagnostics Unavailable => new(
        isAvailable: false,
        externalPrefixCount: 0,
        nativePrefixInstalled: false,
        nativePrefixPriority: null,
        highestExternalPrefixPriority: null,
        nativePrefixUncontended: false,
        nativePrefixPriorityStrictlyHigher: false);

    public ProgressivePrefixDiagnostics(
        bool isAvailable,
        int externalPrefixCount,
        bool nativePrefixInstalled,
        int? nativePrefixPriority,
        int? highestExternalPrefixPriority,
        bool nativePrefixUncontended,
        bool nativePrefixPriorityStrictlyHigher)
    {
        IsAvailable = isAvailable;
        ExternalPrefixCount = externalPrefixCount;
        NativePrefixInstalled = nativePrefixInstalled;
        NativePrefixPriority = nativePrefixPriority;
        HighestExternalPrefixPriority = highestExternalPrefixPriority;
        NativePrefixUncontended = nativePrefixUncontended;
        NativePrefixPriorityStrictlyHigher = nativePrefixPriorityStrictlyHigher;
    }

    public bool IsAvailable { get; }

    public int ExternalPrefixCount { get; }

    public bool NativePrefixInstalled { get; }

    public int? NativePrefixPriority { get; }

    public int? HighestExternalPrefixPriority { get; }

    public bool NativePrefixUncontended { get; }

    public bool NativePrefixPriorityStrictlyHigher { get; }
}

public static class FfmpegCommandPatchBridge
{
    private static readonly object Sync = new();
    private static FfmpegCommandProcessor? processor;

    internal static void Attach(FfmpegCommandProcessor value)
    {
        lock (Sync) processor = value;
    }

    internal static void Detach(FfmpegCommandProcessor value)
    {
        lock (Sync)
        {
            if (ReferenceEquals(processor, value)) processor = null;
        }
    }

    public static void Prefix(object __0, CancellationToken __1, out object? __state)
    {
        __state = null;
        FfmpegCommandProcessor? current;
        lock (Sync) current = processor;
        if (current is not null && current.TryApply(__0, __1, out var mutation)) __state = mutation;
    }

    public static void Postfix(object __instance, object __0, CancellationToken __1, object? __state,
        MethodBase __originalMethod, ref Task<bool> __result)
    {
        if (__state is not FastSeekCommandMutation mutation) return;
        Task<bool> StartNative(CancellationToken token)
        {
            try { return (Task<bool>)((MethodInfo)__originalMethod).Invoke(__instance, new[] { __0, (object)token })!; }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
        __result = mutation.ObserveStartAsync(__result, StartNative, __1);
    }
}

public static class TranscodeInputPatchBridge
{
    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<Type, Func<Task, StartObservation, Task>> Observers = new();
    private static TranscodeInputProcessor? processor;

    internal static void Attach(TranscodeInputProcessor value)
    {
        lock (Sync) processor = value;
    }

    internal static void Detach(TranscodeInputProcessor value)
    {
        lock (Sync)
        {
            if (ReferenceEquals(processor, value)) processor = null;
        }
    }

    public static void Prefix(object __instance, object __0, string __1, out object? __state)
    {
        __state = null;
        TranscodeInputProcessor? current;
        lock (Sync) current = processor;
        if (current is null) return;
        try
        {
            var attempt = current.BeforeStart(__instance, __0, __1);
            if (attempt is not null) __state = new StartObservation(current.Jobs, attempt);
        }
        catch (TranscodeStartRejectedException exception)
        {
            TranscodeInputProcessor.SetRetryAfter(__instance, exception.RetryAfterSeconds);
            throw;
        }
    }

    public static void Postfix(object? __state, ref object __result, MethodBase __originalMethod)
    {
        if (__state is not StartObservation observation || __result is not Task task) return;
        var resultType = ((MethodInfo)__originalMethod).ReturnType.GetGenericArguments()[0];
        __result = GetObserver(resultType)(task, observation);
    }

    internal static void PrepareResultType(Type resultType) => GetObserver(resultType);

    private static Func<Task, StartObservation, Task> GetObserver(Type resultType) =>
        Observers.GetOrAdd(resultType, type =>
            (Func<Task, StartObservation, Task>)typeof(TranscodeInputPatchBridge)
                .GetMethod(nameof(Observe), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(type).CreateDelegate(typeof(Func<Task, StartObservation, Task>)));

    private static Task Observe<T>(Task task, StartObservation observation) =>
        observation.Coordinator.ObserveStartAsync((Task<T>)task, observation.Attempt);

    public static bool CleanupPrefix(object __instance, object __0, int __1, int __2, ref Task __result)
    {
        TranscodeInputProcessor? current;
        lock (Sync) current = processor;
        if (current is null || !current.TryCleanup(__instance, __0, __1, __2, out var cleanup)) return true;
        __result = cleanup!;
        return false;
    }

    private sealed class StartObservation
    {
        public StartObservation(TranscodeJobCoordinator coordinator, TranscodeJobCoordinator.Attempt attempt)
        {
            Coordinator = coordinator;
            Attempt = attempt;
        }

        public TranscodeJobCoordinator Coordinator { get; }
        public TranscodeJobCoordinator.Attempt Attempt { get; }
    }
}

public static class NativeStreamPatchBridge
{
    private const string VideoServiceTypeName = "Emby.Server.MediaEncoding.Api.Progressive.VideoService";
    private static readonly object Sync = new();
    private static NativeVideoStreamProcessor? processor;

    internal static void Attach(NativeVideoStreamProcessor value)
    {
        lock (Sync) processor = value;
    }

    internal static void Detach(NativeVideoStreamProcessor value)
    {
        lock (Sync)
        {
            if (ReferenceEquals(processor, value)) processor = null;
        }
    }

    public static bool Prefix(object __instance, object __0, bool __1, ref Task<object> __result)
    {
        if (!string.Equals(__instance.GetType().FullName, VideoServiceTypeName, StringComparison.Ordinal)) return true;
        NativeVideoStreamProcessor? current;
        lock (Sync) current = processor;
        if (current is null || !current.TryHandle(__instance, __0, __1, out var replacement)) return true;
        __result = replacement!;
        return false;
    }
}

public static class PlaybackPatchBridge
{
    private static readonly object Sync = new();
    private static PlaybackInfoProcessor? processor;

    internal static void Attach(PlaybackInfoProcessor value)
    {
        lock (Sync) processor = value;
    }

    internal static void Detach(PlaybackInfoProcessor value)
    {
        lock (Sync)
        {
            if (ReferenceEquals(processor, value)) processor = null;
        }
    }

    public static void Postfix(object __instance, object __0, ref Task<object> __result)
    {
        PlaybackInfoProcessor? current;
        lock (Sync) current = processor;
        if (current is not null && __result is not null)
            __result = current.WrapAsync(__result, __instance, __0);
    }
}
