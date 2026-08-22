using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
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
    private const string PatchId = "Emby.StrmBridge.Playback.v3";
    private const string PlaybackInfoServiceTypeName = "Emby.Server.MediaEncoding.Api.MediaInfoService";
    private const string VideoServiceTypeName = "Emby.Server.MediaEncoding.Api.Progressive.VideoService";
    private const string ProgressiveServiceTypeName =
        "Emby.Server.MediaEncoding.Api.Progressive.BaseProgressiveStreamingService";
    private const string BaseStreamingServiceTypeName =
        "Emby.Server.MediaEncoding.Api.BaseStreamingService";
    private readonly PlaybackInfoProcessor processor;
    private readonly NativeVideoStreamProcessor nativeStreamProcessor;
    private readonly TranscodeInputProcessor transcodeInputProcessor;
    private readonly ILogger logger;
    private Harmony? harmony;
    private bool disposed;

    internal HarmonyPatchHost(
        PlaybackInfoProcessor processor,
        NativeVideoStreamProcessor nativeStreamProcessor,
        TranscodeInputProcessor transcodeInputProcessor,
        ILogManager logManager)
    {
        this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        this.nativeStreamProcessor = nativeStreamProcessor ??
            throw new ArgumentNullException(nameof(nativeStreamProcessor));
        this.transcodeInputProcessor = transcodeInputProcessor ??
            throw new ArgumentNullException(nameof(transcodeInputProcessor));
        logger = (logManager ?? throw new ArgumentNullException(nameof(logManager)))
            .GetLogger(Plugin.Instance?.Name ?? "STRM Bridge");
    }

    public PlaybackPatchStatus Status { get; private set; } = PlaybackPatchStatus.NativeOnly;

    public string HostAbi { get; private set; } = "unavailable";

    public PlaybackPatchStatus Install()
    {
        if (disposed) throw new ObjectDisposedException(nameof(HarmonyPatchHost));
        if (harmony is not null) return Status;
        try
        {
            var playbackInfoServiceType = ResolveServiceType(PlaybackInfoServiceTypeName);
            var videoServiceType = ResolveServiceType(VideoServiceTypeName);
            var progressiveServiceType = ResolveServiceType(ProgressiveServiceTypeName);
            var baseStreamingServiceType = ResolveServiceType(BaseStreamingServiceTypeName);
            var version = playbackInfoServiceType.Assembly.GetName().Version ?? new Version(0, 0);
            HostAbi = version.ToString();
            if (version.Major != 4 || version.Minor != 9 || version.Build != 5 ||
                videoServiceType.Assembly.GetName().Version != version ||
                progressiveServiceType.Assembly.GetName().Version != version ||
                baseStreamingServiceType.Assembly.GetName().Version != version)
            {
                logger.Warn("STRM_BRIDGE_PATCH_ABI_UNSUPPORTED abi=" + HostAbi);
                Status = PlaybackPatchStatus.NativeOnly;
                return Status;
            }

            var playbackInfoTargets = new[]
            {
                FindTarget(playbackInfoServiceType, "Get", "Emby.Server.MediaEncoding.Api.GetPlaybackInfo"),
                FindTarget(playbackInfoServiceType, "Post", "Emby.Server.MediaEncoding.Api.GetPostedPlaybackInfo"),
            };
            var nativeStreamTarget = FindTarget(
                progressiveServiceType,
                "ProcessRequest",
                "Emby.Server.MediaEncoding.Api.StreamRequest",
                typeof(bool).FullName!);
            var transcodeInputTarget = FindTaskTarget(
                baseStreamingServiceType,
                "StartFfMpeg",
                "Emby.Server.MediaEncoding.Api.TranscodingJob",
                "Emby.Server.MediaEncoding.Api.StreamState",
                typeof(string).FullName!,
                "System.Threading.CancellationToken",
                typeof(bool).FullName!);
            PlaybackPatchBridge.Attach(processor);
            NativeStreamPatchBridge.Attach(nativeStreamProcessor);
            TranscodeInputPatchBridge.Attach(transcodeInputProcessor);
            harmony = new Harmony(PatchId);
            var postfix = new HarmonyMethod(typeof(PlaybackPatchBridge).GetMethod(
                nameof(PlaybackPatchBridge.Postfix), BindingFlags.Static | BindingFlags.Public));
            var prefix = new HarmonyMethod(typeof(NativeStreamPatchBridge).GetMethod(
                nameof(NativeStreamPatchBridge.Prefix), BindingFlags.Static | BindingFlags.Public));
            var transcodeInputPrefix = new HarmonyMethod(typeof(TranscodeInputPatchBridge).GetMethod(
                nameof(TranscodeInputPatchBridge.Prefix), BindingFlags.Static | BindingFlags.Public));
            foreach (var target in playbackInfoTargets) harmony.Patch(target, postfix: postfix);
            harmony.Patch(nativeStreamTarget, prefix: prefix);
            harmony.Patch(transcodeInputTarget, prefix: transcodeInputPrefix);
            var targets = playbackInfoTargets.Append(nativeStreamTarget).Append(transcodeInputTarget).ToArray();
            if (targets.Any(target => Harmony.GetPatchInfo(target)?.Owners.Contains(PatchId) != true))
                throw new InvalidOperationException("Playback patch ownership validation failed.");
            Status = PlaybackPatchStatus.Ready;
            logger.Info("STRM_BRIDGE_PATCH_READY abi=" + HostAbi + " targets=" + targets.Length);
        }
        catch (Exception exception)
        {
            harmony?.UnpatchAll(PatchId);
            harmony = null;
            PlaybackPatchBridge.Detach(processor);
            NativeStreamPatchBridge.Detach(nativeStreamProcessor);
            TranscodeInputPatchBridge.Detach(transcodeInputProcessor);
            Status = PlaybackPatchStatus.Failed;
            logger.Warn("STRM_BRIDGE_PATCH_FAILED error=" + exception.GetType().Name);
        }
        return Status;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        harmony?.UnpatchAll(PatchId);
        harmony = null;
        PlaybackPatchBridge.Detach(processor);
        NativeStreamPatchBridge.Detach(nativeStreamProcessor);
        TranscodeInputPatchBridge.Detach(transcodeInputProcessor);
        Status = PlaybackPatchStatus.NativeOnly;
    }

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

public static class TranscodeInputPatchBridge
{
    private static readonly object Sync = new();
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

    public static void Prefix(object __instance, object __0)
    {
        TranscodeInputProcessor? current;
        lock (Sync) current = processor;
        current?.TryRoute(__instance, __0);
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
