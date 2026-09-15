using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Persistence;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using Emby.StrmBridge.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Text;
using MediaBrowser.Model.Globalization;
using System.Text;

if (args.Length is not (2 or 3 or 4))
    throw new ArgumentException("Usage: Emby.StrmBridge.HostCheck.dll <host-assembly-directory> <plugin-dll> [synthetic-subtitle-fixture-directory] [dashboard-ui-directory]");
var hostDirectory = Path.GetFullPath(args[0]);
var pluginPath = Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = name.Name == "Emby.StrmBridge" ? pluginPath : Path.Combine(hostDirectory, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
// Defer JIT compilation of code using host types until the isolated resolver is installed.
Run(hostDirectory, pluginPath, args.Length >= 3 ? Path.GetFullPath(args[2]) : null, args.Length >= 4 ? Path.GetFullPath(args[3]) : null);

[MethodImpl(MethodImplOptions.NoInlining)]
static void Run(string hostDirectory, string pluginPath, string? fixtureDirectory, string? webDirectory)
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
            "six playback patches required");
        host.Dispose();
        Require(targets.All(target => harmony.GetPatchInfo(target)?.Owners.Contains(owner) != true),
            "patch removal incomplete");
    }
    Console.WriteLine("PASS host=" + version + " install/reinstall/dispose targets=6");

    for (var cycle = 0; cycle < 2; cycle++)
    {
        using var subtitlePatch = new SubtitlePatchHost(new PluginRuntime(), UninitializedResultFactory(), logger);
        Require(subtitlePatch.Install(), "subtitle patch installation failed");
        Require(subtitlePatch.Install(), "subtitle repeat installation failed");
    }
    Console.WriteLine("PASS subtitle Web patch install/dispose");
    // Optional subtitle failure or late contention must not remove the six video hooks.
    using (var runtime = new PluginRuntime())
    using (var playback = new HarmonyPatchHost(Uninitialized<PlaybackInfoProcessor>(),
        Uninitialized<NativeVideoStreamProcessor>(), Uninitialized<TranscodeInputProcessor>(),
        Uninitialized<FfmpegCommandProcessor>(), logManager))
    {
        runtime.SetPlaybackHealth(playback.Install(), playback.HostAbi);
        runtime.AttachPlaybackPatch(playback);
        Require(runtime.SubtitleInputsReady, "shared input hooks not ready");
        var runner = hostAssembly.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfmpegRunner", true)!;
        var target = SubtitlePatchHost.FindRunnerTarget(runner)!;
        var foreignOwner = "StrmBridge.HostCheck.SubtitleConflict";
        var foreign = HarmonyRuntimeAdapter.Select(foreignOwner, EmbeddedAssemblyLoader.EnsureHarmonyLoaded);
        try
        {
            using (var subtitle = new SubtitlePatchHost(runtime, UninitializedResultFactory(), logger))
            {
                Require(subtitle.Install() && subtitle.CanServe, "complete subtitle hooks not ready");
                foreign.Patch(target, typeof(FixtureSubtitleConflict).GetMethod(nameof(FixtureSubtitleConflict.Prefix)));
                Require(!subtitle.CanServe, "late foreign prefix must disable subtitle takeover");
            }
            using var unavailable = new SubtitlePatchHost(runtime, UninitializedResultFactory(), logger);
            Require(!unavailable.Install() && !unavailable.CanServe, "contended subtitle hook accepted");
            Require(playback.Status == PlaybackPatchStatus.Ready && runtime.SubtitleInputsReady, "optional subtitle failure disabled video hooks");
            var main = HarmonyRuntimeAdapter.Select(owner, EmbeddedAssemblyLoader.EnsureHarmonyLoaded);
            Require(targets.Count(t => main.GetPatchInfo(t)?.Owners.Contains(owner) == true) == 6, "optional failure removed video hooks");
        }
        finally { foreign.UnpatchAll(foreignOwner); }
    }
    Console.WriteLine("PASS optional subtitle contention/failure preserves six video hooks and rejects takeover");
    using (var resource = typeof(Emby.StrmBridge.Plugin).Assembly.GetManifestResourceStream("Emby.StrmBridge.Subtitles.Player.js"))
    {
        Require(resource is not null, "automatic subtitle player module missing");
        using var reader = new StreamReader(resource!);
        Require(reader.ReadToEnd().Contains("define([]"), "invalid subtitle AMD module");
    }
    Console.WriteLine("PASS automatic subtitle player module resource");

    if (fixtureDirectory is not null)
    {
        CheckSubtitleFixture(hostDirectory, hostAssembly, fixtureDirectory, webDirectory, logger);
    }

    CheckSharedNegotiation(logger);
    CheckSharedRunnerBinding(hostAssembly, logger);

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

static MediaBrowser.Controller.Net.IHttpResultFactory UninitializedResultFactory() => DispatchProxy.Create<MediaBrowser.Controller.Net.IHttpResultFactory, LogProxy>();

static void CheckSubtitleFixture(string hostDirectory, Assembly hostAssembly, string fixtureDirectory, string? webDirectory, ILogger logger)
{
    var version = hostAssembly.GetName().Version!;
    if (webDirectory is not null)
    {
        var original = File.ReadAllText(Path.Combine(webDirectory, SubtitlePatchHost.PlayerResource));
        var transformed = SubtitlePatchHost.Transform(original);
        Require(transformed is not null && transformed.Contains("renderTracksEventsNative"), "known Web resource did not transform");
        Require(SubtitlePatchHost.Transform(original + " ") is null, "modified Web resource accepted");
        File.WriteAllText(Path.Combine(fixtureDirectory, "web-" + version + ".js"), transformed);
        Console.WriteLine("PASS exact Web resource adaptation and unknown revision guard");
    }
    if (Environment.GetEnvironmentVariable("STRM_BRIDGE_WEB_ONLY") == "1") return;
    CheckSharedVideoSubtitle(hostDirectory, fixtureDirectory).GetAwaiter().GetResult();
}

static void CheckSharedNegotiation(ILogger logger)
{
    var source = new MediaSourceInfo { Id = "fixture", Container = "mkv", Protocol = MediaProtocol.Http,
        SupportsDirectPlay = true, SupportsDirectStream = true, SupportsTranscoding = true, Bitrate = 2_128_000,
        RunTimeTicks = TimeSpan.FromMinutes(2).Ticks, DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = 2,
        MediaStreams = new List<MediaStream> {
            new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 1280, Height = 720, BitRate = 2_000_000,
                AverageFrameRate = 24, RealFrameRate = 24, IsInterlaced = false, Profile = "High", Level = 40 },
            new() { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, SampleRate = 48000, BitRate = 128000 },
            new() { Index = 2, Type = MediaStreamType.Subtitle, Codec = "ass" },
        } };
    var original = new DeviceProfile { Name = SharedSubtitleNegotiation.ProfileName,
        DirectPlayProfiles = new[] { new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mkv", VideoCodec = "h264", AudioCodec = "aac" } },
        TranscodingProfiles = new[] { new TranscodingProfile { Type = DlnaProfileType.Video, Context = EncodingContext.Streaming,
            Protocol = "http", Container = "mkv", VideoCodec = "h264", AudioCodec = "aac" },
            new TranscodingProfile { Type = DlnaProfileType.Video, Context = EncodingContext.Streaming,
                Protocol = "hls", Container = "m4s,ts", VideoCodec = "h264", AudioCodec = "aac", MinSegments = 1 } },
        SubtitleProfiles = new[] { new SubtitleProfile { Format = "ass", Method = SubtitleDeliveryMethod.External } } };
    var prepared = SharedSubtitleNegotiation.CreateProfile(source, original) ?? throw new InvalidOperationException("shared profile missing");
    Require((bool)typeof(SubtitleProfile).GetProperty("AllowChunkedResponse")!.GetValue(prepared.SubtitleProfiles[0])!, "chunked external subtitle capability missing");
    var builder = new StreamBuilder(new FixtureTranscoderSupport(), logger);
    var options = new VideoOptions { ItemId = 1, DeviceId = "fixture", MediaSourceId = source.Id, MediaSources = new[] { source },
        Profile = original, Context = EncodingContext.Streaming, MaxBitrate = 50_000_000, SubtitleStreamIndex = -1 };
    var before = builder.BuildVideoItem(options);
    Require(before is not null && before.PlayMethod != MediaBrowser.Model.Session.PlayMethod.Transcode, "fixture must previously choose raw MKV");
    // These are the two inputs changed by the per-source hook; the host then
    // applies them to source.SupportsDirectPlay/SupportsDirectStream itself.
    source.SupportsDirectPlay = false; source.SupportsDirectStream = false;
    options.Profile = prepared;
    foreach (var subtitle in new[] { -1, 2 })
    {
        options.SubtitleStreamIndex = subtitle;
        var result = builder.BuildVideoItem(options) ?? throw new InvalidOperationException("missing host stream");
        Require( result.PlayMethod == MediaBrowser.Model.Session.PlayMethod.Transcode && result.SubProtocol == "hls" && result.Container == "ts", "host did not choose shared HLS");
        result.PlaySessionId = "fixture-session";
        var url = result.ToUrl("http://127.0.0.1", "fixture-token");
        Require(url.Contains("PlaySessionId=fixture-session") && url.Contains("master.m3u8"), "host URL lacks video session");
        Require(!url.Contains("allowVideoStreamCopy=false", StringComparison.OrdinalIgnoreCase), "negotiation disabled video copy");
        Require(result.SubtitleStreams.Any(t => t.Index == 2 && t.SubtitleDeliveryMethod == SubtitleDeliveryMethod.External), "host did not keep text selectable as external when off/on");
    }
    Require(original.TranscodingProfiles[1].Container == "m4s,ts", "shared profile mutated another version");
    Console.WriteLine("PASS actual host StreamBuilder raw MKV -> session-bound HLS TS, external ASS and copy eligibility, subtitles off/on");
}

static void CheckSharedRunnerBinding(Assembly hostAssembly, ILogger logger)
{
    var root = Path.Combine(Environment.CurrentDirectory, ".local", "strm-bridge-shared-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    using var runtime = new PluginRuntime();
    try
    {
        runtime.Initialize(root);
        runtime.UpdateOptions(new Emby.StrmBridge.Configuration.PluginConfiguration { Enabled = true, EnableSubtitles = true }, false);
        var path = Path.Combine(root, "fixture.strm"); File.WriteAllText(path, "https://fixture.invalid/video.mkv");
        var user = new MediaBrowser.Controller.Entities.User { Id = Guid.NewGuid() };
        var item = Guid.NewGuid(); var source = runtime.SourcePolicy!.Read(path);
        var ticket = runtime.Tickets.IssuePlayback(item, "exact", user.Id.ToString("N"), source,
            PlaybackTicketPurpose.ServerFfmpeg, runtime.Generation, TimeSpan.FromHours(1));
        var url = GatewayRouteBuilder.CreateInternalPlaybackRoute("http://127.0.0.1:8096", "", ticket, "mkv")!;
        var runnerType = hostAssembly.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfmpegRunner", true)!;
        var commandField = runnerType.GetField("command", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var command = Activator.CreateInstance(commandField.FieldType)!;
        var addInput = command.GetType().GetMethod("AddInput")!;
        var container = Activator.CreateInstance(addInput.GetParameters()[0].ParameterType)!;
        container.GetType().GetProperty("Url")!.SetValue(container, url);
        var protocol = container.GetType().GetProperty("Protocol")!;
        protocol.SetValue(container, "http");
        var input = addInput.Invoke(command, new[] { container })!;
        var inputOptions = input.GetType().GetProperty("Options")!.GetValue(input)!;
        inputOptions.GetType().GetProperty("ss")!.SetValue(inputOptions, TimeSpan.FromSeconds(24));
        var global = command.GetType().GetProperty("Options")!.GetValue(command)!;
        global.GetType().GetProperty("copyts")!.SetValue(global, true);
        global.GetType().GetProperty("start_at_zero")!.SetValue(global, true);
        var stateType = hostAssembly.GetType("Emby.Server.MediaEncoding.Api.StreamState", true)!;
        var state = FormatterServices.GetUninitializedObject(stateType);
        var requestProperty = stateType.GetProperty("BaseRequest")!;
        var request = Activator.CreateInstance(requestProperty.PropertyType)!;
        request.GetType().GetProperty("PlaySessionId")!.SetValue(request, "fixture-play-session");
        request.GetType().GetProperty("StartTimeTicks")!.SetValue(request, 1842869822L);
        requestProperty.SetValue(state, request);
        var jobKind = stateType.GetProperty("TranscodingType")!;
        jobKind.SetValue(state, Enum.Parse(jobKind.PropertyType, "Hls"));
        stateType.GetProperty("SegmentContainer")!.SetValue(state, "ts");
        stateType.GetProperty("RunTimeTicks")!.SetValue(state, TimeSpan.FromMinutes(10).Ticks);
        var segmentLength = stateType.GetProperty("SegmentLength")!;
        segmentLength.SetValue(state, Convert.ChangeType(6, segmentLength.PropertyType));
        var hls = hostAssembly.GetType("Emby.Server.MediaEncoding.Api.Hls.DynamicHlsService", true)!;
        var getStart = hls.GetMethod("GetStartPositionTicks", BindingFlags.NonPublic | BindingFlags.Static)!;
        Require(Convert.ToDouble(segmentLength.GetValue(state)) == 6, "host segment duration setter unavailable");
        var segmentStart = (long)getStart.Invoke(null, new object[] { state, 30 })!;
        Require(segmentStart == TimeSpan.FromSeconds(180).Ticks, "host segment start changed");
        // DynamicHlsService does this replacement before calling StartFfMpeg.
        request.GetType().GetProperty("StartTimeTicks")!.SetValue(request, segmentStart);
        inputOptions.GetType().GetProperty("ss")!.SetValue(inputOptions, TimeSpan.FromTicks(segmentStart));
        var media = new MediaSourceInfo { Id = "exact", Container = "mkv", Path = url, MediaStreams = new List<MediaStream> {
            new() { Index = 1, Type = MediaStreamType.Subtitle, Codec = "ass" } } };
        stateType.GetProperty("MediaSource")!.SetValue(state, media);
        var userProperty = stateType.GetProperty("User");
        if (userProperty?.SetMethod is not null) userProperty.SetValue(state, user);
        else
        {
            var authProperty = stateType.GetProperty("AuthorizationInfo")!;
            var auth = Activator.CreateInstance(authProperty.PropertyType)!;
            auth.GetType().GetProperty("User")!.SetValue(auth, user);
            authProperty.SetValue(state, auth);
        }
        var runner = FormatterServices.GetUninitializedObject(runnerType);
        commandField.SetValue(runner, command);
        runnerType.GetField("jobState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runner, state);
        using var coordinator = new SubtitleCoordinator(runtime, null!, null!, logger);
        var output = coordinator.Shared;
        var videoPattern = Path.Combine(root, "%d.ts");
        command.GetType().GetMethod("AddOutput")!.Invoke(command, new object?[] { videoPattern, null, null });
        var packet = new byte[188]; Array.Fill(packet, (byte)0xff);
        packet[0] = 0x47; packet[1] = 0x41; packet[2] = 0; packet[3] = 0x10;
        packet[4] = 0; packet[5] = 0; packet[6] = 1; packet[7] = 0xe0;
        packet[8] = 0; packet[9] = 0; packet[10] = 0x80; packet[11] = 0x80; packet[12] = 5;
        long pts = 188 * 90000; // Original keyframe 178 s, TS mux delay +10 s.
        packet[13] = (byte)(0x21 | ((pts >> 29) & 14)); packet[14] = (byte)(pts >> 22);
        packet[15] = (byte)(((pts >> 14) & 254) | 1); packet[16] = (byte)(pts >> 7); packet[17] = (byte)(((pts << 1) & 254) | 1);
        File.WriteAllBytes(Path.Combine(root, "30.ts"), packet);
        var arguments = "-y -i \"" + url + "\" -map 0:0 -sn -segment_format mpegts -segment_start_number 30 -max_delay 5000000 \"" + videoPattern + "\"";
        var job = output.Attach(runner, ref arguments);
        Require(job is not null, "actual host runner did not attach shared subtitles");
        var context = new SubtitleRequestContext { Source = source, ItemId = item, MediaSourceId = "exact", UserId = user.Id,
            StreamFingerprint = SubtitleDigest.Streams(media.MediaStreams), PlaySessionId = "fixture-play-session", VideoStartTicks = 1842869822, MseTimestampOffsetTicks = -TimeSpan.FromSeconds(10).Ticks,
            Start = TimeSpan.FromSeconds(184).Ticks, End = TimeSpan.FromSeconds(240).Ticks, Index = 1, Generation = runtime.Generation };
        Require(job!.Matches(context), "actual host playback binding mismatch");
        var session = coordinator.CreateSession(context);
        Require(session.Id.Length > 0, "actual HLS subtitle session rejected");
        var ass = Directory.GetFiles(runtime.DataDirectory!, "*.ass", SearchOption.AllDirectories).Single();
        File.WriteAllText(ass, "[Script Info]\nScriptType: v4.00+\n[Events]\nDialogue: 0,0:03:05.00,0:03:06.00,Default,,0,0,0,,HLS binding cue\n");
        job.Complete();
        using var localStream = output.OpenAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        using var localReader = new StreamReader(localStream);
        var delivered = localReader.ReadToEnd();
        Require(delivered.Contains("HLS binding cue"), "matched session did not deliver local subtitles");
        Require(delivered.Contains("0:03:05.00,0:03:06.00"), "runner keyframe must not replace the browser MSE origin");
        Console.WriteLine("PASS actual Output0.Url binding and browser MSE clock preserved across runner keyframes");
        Console.WriteLine("PASS actual runner command, user, session and seek binding");
        Console.WriteLine("PASS actual HLS 180s segment binds 184.2869822s playlist seek and delivers local ASS");
    }
    finally { runtime.Dispose(); Directory.Delete(root, true); }
}

static async Task CheckSharedVideoSubtitle(string hostDirectory, string directory)
{
    foreach (var seek in new[] { 0, 24 })
    {
        using var origin = new FixtureOrigin(Path.Combine(directory, "fixture.mkv"), 1, holdBodyBeforeEof: true);
        var work = Path.Combine(directory, "shared-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var output = Path.Combine(work, "track.ass"); File.WriteAllText(output, "");
        var video = Path.Combine(work, "video.ts");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var process = new System.Diagnostics.Process();
        try
        {
            process.StartInfo = new System.Diagnostics.ProcessStartInfo(Path.Combine(hostDirectory, "ffmpeg"))
            {
                UseShellExecute = false, RedirectStandardError = true,
                Arguments = "-nostdin -v error -y -copyts -start_at_zero -ss " + seek +
                    " -noaccurate_seek -i \"" + origin.Url + "\" -map 0:0 -sn -an -c:v copy -max_delay 5000000 -avoid_negative_ts disabled -f mpegts \"" + video + "\"" +
                    SharedSubtitleJob.BuildOutputArguments(output, 1, "ass")
            };
            process.Start();
            var stderr = process.StandardError.ReadToEndAsync();
            var context = new SubtitleRequestContext { Start = TimeSpan.FromSeconds(seek).Ticks, End = TimeSpan.FromSeconds(30).Ticks };
            var timeline = new SharedSubtitleTimeline(video, TimeSpan.FromSeconds(seek).Ticks, TimeSpan.FromSeconds(10).Ticks, false);
            var timelineOffset = await timeline.ReadOffsetAsync(timeout.Token);
            Require(timelineOffset == TimeSpan.FromSeconds(seek == 0 ? 0 : 0.5).Ticks, "actual MPEG-TS keyframe timeline mismatch");
            using var stream = new SharedSubtitleStream(output, context, () => process.HasExited, timeout.Token, CancellationToken.None, timelineOffset);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (!line.StartsWith("Dialogue:")) continue;
                Require(line.Contains(seek == 0 ? "0:00:02.00,0:00:04.00" : "0:00:27.50,0:00:29.50"), "shared subtitle did not follow actual video keyframe presentation");
                Require(!process.HasExited, "shared subtitle was delayed until video EOF");
                Require(File.Exists(video) && new FileInfo(video).Length > 0, "subtitle prevented video output");
                process.Kill(); await process.WaitForExitAsync();
                Require((await stderr).Length == 0, "shared video/subtitle emitted FFmpeg errors");
                Console.WriteLine("PASS same FFmpeg video+ASS before held media EOF seek=" + seek + " timeline_offset_ms=" + timelineOffset / 10000);
                break;
            }
            Require(line is not null, "shared video subtitle produced no cue");
        }
        finally
        {
            if (process.Id > 0 && !process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            Directory.Delete(work, true);
        }
    }
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

public class FixtureProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? arguments) => Handler?.Invoke(method!, arguments);
}

// Synthetic, bandwidth-limited origin. No installed media library or user server is read.
public sealed class FixtureOrigin : IDisposable
{
    private readonly System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly byte[] data;
    private readonly int chunkDelay;
    private readonly bool holdBodyBeforeEof;
    private readonly Task accept;
    private readonly List<Task> connections = new();
    public string Url => "http://127.0.0.1:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port + "/fixture.mkv";
    public FixtureOrigin(string path, int chunkDelay = 20, bool holdBodyBeforeEof = false) { this.holdBodyBeforeEof = holdBodyBeforeEof; this.chunkDelay = chunkDelay; data = File.ReadAllBytes(path); listener.Start(); accept = Accept(); }
    private async Task Accept()
    {
        try { while (!lifetime.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(lifetime.Token); lock (connections) connections.Add(Serve(client)); } }
        catch (OperationCanceledException) { }
        catch (System.Net.Sockets.SocketException) when (lifetime.IsCancellationRequested) { }
    }
    private static bool TryRange(string? range, long length, out long start, out long end)
    {
        start = 0; end = length - 1;
        if (range is null) return length > 0;
        if (!range.StartsWith("bytes=", StringComparison.Ordinal)) return false;
        var parts = range.Substring(6).Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], out start) || start < 0 || start >= length) return false;
        if (parts[1].Length > 0 && !long.TryParse(parts[1], out end)) return false;
        end = Math.Min(end, length - 1);
        return end >= start;
    }
    private async Task Serve(System.Net.Sockets.TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string? line; string? range = null;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line.Substring(6).Trim();
            if (!TryRange(range, data.Length, out var start, out var end)) return;
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 206 Partial Content\r\nContent-Length: {end-start+1}\r\nContent-Range: bytes {start}-{end}/{data.Length}\r\nETag: \"fixture-v1\"\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, lifetime.Token);
            for (var at = start; at <= end;)
            {
                // Metadata tail requests remain readable; media bodies never reach
                // EOF, so a seeked subtitle cannot pass by flushing only at trailer.
                if (holdBodyBeforeEof && end-start > 65536 && at >= data.Length - 65536)
                    await Task.Delay(Timeout.Infinite, lifetime.Token);
                var count = (int)Math.Min(32768, end-at+1); await stream.WriteAsync(data.AsMemory((int)at, count), lifetime.Token);
                at += count; await Task.Delay(chunkDelay, lifetime.Token);
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        lifetime.Cancel(); listener.Stop(); accept.GetAwaiter().GetResult();
        Task[] work; lock (connections) work = connections.ToArray();
        Task.WhenAll(work).GetAwaiter().GetResult(); lifetime.Dispose();
    }
}

public sealed class FixtureTranscoderSupport : ITranscoderSupport
{
    public bool CanEncodeToAudioCodec(ReadOnlySpan<char> codec) => true;
    public bool CanEncodeToSubtitleCodec(ReadOnlySpan<char> codec) => true;
    public bool CanExtractSubtitles(string codec, EncodingContext context) => false;
    public bool RequiresSubtitleProcessing(MediaSourceInfo source, MediaStream stream, EncodingContext context, SubtitleDeliveryMethod method) => false;
    public bool SupportsSubtitleConversionTo(MediaSourceInfo source, MediaStream stream, ReadOnlySpan<char> codec, EncodingContext context, SubtitleDeliveryMethod method) => true;
}

public static class FixtureSubtitleConflict
{
    public static bool Prefix() => true;
}
