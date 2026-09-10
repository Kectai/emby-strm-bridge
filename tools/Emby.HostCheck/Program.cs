using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Persistence;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

if (args.Length != 2)
    throw new ArgumentException("Usage: Emby.StrmBridge.HostCheck.dll <host-assembly-directory> <plugin-dll>");
var hostDirectory = Path.GetFullPath(args[0]);
var pluginPath = Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = name.Name == "Emby.StrmBridge" ? pluginPath : Path.Combine(hostDirectory, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
// Defer JIT compilation of code using host types until the isolated resolver is installed.
Run(hostDirectory, pluginPath);

[MethodImpl(MethodImplOptions.NoInlining)]
static void Run(string hostDirectory, string pluginPath)
{
    Require(typeof(HarmonyPatchHost).Assembly.Location == pluginPath, "wrong plugin loaded");
    Console.WriteLine("plugin_sha256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath))).ToLowerInvariant());
    Console.WriteLine("runtime=" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
    var hostAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(
        Path.Combine(hostDirectory, "Emby.Server.MediaEncoding.dll"));
    var version = hostAssembly.GetName().Version;
    foreach (var assembly in new[] { typeof(MediaSourceInfo).Assembly, typeof(MediaBrowser.Controller.IServerApplicationPaths).Assembly })
    {
        Require(assembly.GetName().Version == version, "mixed host assemblies");
        Require(Path.GetDirectoryName(assembly.Location) == hostDirectory, "host SDK substituted for runtime");
    }

    var logger = DispatchProxy.Create<ILogger, LogProxy>();
    var logManager = DispatchProxy.Create<ILogManager, LogProxy>();
    ((LogProxy)(object)logManager).Logger = logger;
    var targets = hostAssembly.GetTypes()
        .Where(type => type.FullName is "Emby.Server.MediaEncoding.Api.MediaInfoService" or
            "Emby.Server.MediaEncoding.Api.Progressive.BaseProgressiveStreamingService" or
            "Emby.Server.MediaEncoding.Api.BaseStreamingService" or
            "Emby.Server.MediaEncoding.Api.EncodingManager" or
            "Emby.Server.MediaEncoding.Unified.Ffmpeg.FfmpegRunner")
        .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        .ToArray();
    const string owner = "Emby.StrmBridge.Playback.v4";
    for (var cycle = 0; cycle < 2; cycle++)
    {
        // Installation only: no host service is invoked, and no media or user configuration is opened.
        using var host = new HarmonyPatchHost(Uninitialized<PlaybackInfoProcessor>(),
            Uninitialized<NativeVideoStreamProcessor>(), Uninitialized<TranscodeInputProcessor>(),
            Uninitialized<FfmpegCommandProcessor>(), logManager);
        Require(host.Install() == PlaybackPatchStatus.Ready, "patch installation failed");
        Require(host.Install() == PlaybackPatchStatus.Ready, "repeat installation failed");
        var harmony = HarmonyRuntimeAdapter.Select(owner, EmbeddedAssemblyLoader.EnsureHarmonyLoaded);
        Require(targets.Count(target => harmony.GetPatchInfo(target)?.Owners.Contains(owner) == true) == 6,
            "six patches required");
        host.Dispose();
        Require(targets.All(target => harmony.GetPatchInfo(target)?.Owners.Contains(owner) != true),
            "patch removal incomplete");
    }
    Console.WriteLine("PASS host=" + version + " install/reinstall/dispose targets=6");

    // Exercise the reflected StreamState contract used when preparing and restoring FFmpeg input.
    var stateType = hostAssembly.GetType("Emby.Server.MediaEncoding.Api.StreamState", true)!;
    var state = FormatterServices.GetUninitializedObject(stateType);
    foreach (var propertyName in new[] { "MediaPath", "DirectMediaPath" })
    {
        var property = stateType.GetProperty(propertyName)!;
        property.SetValue(state, "http://fixture.invalid/media");
        Require((string?)property.GetValue(state) == "http://fixture.invalid/media", propertyName);
        property.SetValue(state, null);
        Require(property.GetValue(state) is null, propertyName + " restore");
    }
    foreach (var propertyName in new[] { "MediaProtocol", "DirectMediaProtocol" })
    {
        var property = stateType.GetProperty(propertyName)!;
        property.SetValue(state, MediaProtocol.Http);
        Require(Equals(property.GetValue(state), MediaProtocol.Http), propertyName);
    }
    var mediaSource = new MediaSourceInfo();
    stateType.GetProperty("MediaSource")!.SetValue(state, mediaSource);
    Require(ReferenceEquals(stateType.GetProperty("MediaSource")!.GetValue(state), mediaSource), "MediaSource identity");
    Console.WriteLine("PASS reflected StreamState input contract");

    var source = new SourceIdentity(new string('a', 64), new string('b', 64),
        new Uri("http://fixture.invalid/media"), "/synthetic/item.strm", 40, DateTimeOffset.UtcNow);
    var metadata = new MediaSourceInfo
    {
        Container = "mkv",
        RunTimeTicks = TimeSpan.FromMinutes(1).Ticks,
        MediaStreams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc",
                ExtendedVideoType = ExtendedVideoTypes.DolbyVision,
                ExtendedVideoSubType = ExtendedVideoSubTypes.DoviProfile81, Rotation = -90, TimeBase = "1/90000" },
        },
    };
    var serializer = new SnapshotSerializer();
    var restored = serializer.Deserialize(serializer.Serialize(MediaInfoSnapshot.FromMediaSource(
        source, metadata, DateTimeOffset.UtcNow))).ToMediaSource("restored").MediaStreams.Single();
    Require(restored.ExtendedVideoType == ExtendedVideoTypes.DolbyVision &&
        restored.ExtendedVideoSubType == ExtendedVideoSubTypes.DoviProfile81 &&
        restored.Rotation == -90 && restored.TimeBase == "1/90000", "snapshot fields lost");
    Console.WriteLine("PASS HDR/rotation snapshot round trip");
}

static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

public class LogProxy : DispatchProxy
{
    public ILogger? Logger { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method?.Name == "GetLogger") return Logger;
        if (method?.Name is "Info" or "Warn") Console.WriteLine(arguments![0]);
        return method?.ReturnType == typeof(void) || method?.ReturnType.IsValueType != true
            ? null : Activator.CreateInstance(method.ReturnType);
    }
}
