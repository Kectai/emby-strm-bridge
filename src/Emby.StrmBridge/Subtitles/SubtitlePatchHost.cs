using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Subtitles;

// Adapts the known Web player resource; the native SubtitleService is untouched.
internal sealed class SubtitlePatchHost : IDisposable
{
    private const string Owner = "Emby.StrmBridge.Subtitles.Web.v3";
    internal const string PlayerResource = "modules/htmlvideoplayer/plugin.js";
    internal const string AdapterResource = "modules/strmbridge/subtitle-player.js";
    private static SubtitlePatchHost? active;
    private readonly PluginRuntime runtime;
    private readonly IHttpResultFactory results;
    private readonly ILogger logger;
    private HarmonyRuntimeAdapter? harmony;
    private MethodInfo? target;
    private MethodInfo? playbackTarget;
    private MethodInfo? runnerTarget;
    internal bool CanServe => runtime.SubtitleInputsReady && IsUncontended;
    private readonly SharedSubtitleNegotiation? negotiation;
    private string resourceStatus = "NotRequested";
    internal string ResourceStatus => Volatile.Read(ref resourceStatus);
    internal bool IsUncontended
    {
        get
        {
            try
            {
                return harmony is not null && target is not null && harmony.GetPatchInfo(target) is { } info &&
                info.Prefixes.Any(p => p.Owner == Owner) && info.Prefixes.All(p => p.Owner == Owner) && playbackTarget is not null &&
                harmony.GetPatchInfo(playbackTarget) is { } playbackInfo && playbackInfo.Prefixes.Any(p => p.Owner == Owner) &&
                playbackInfo.Prefixes.All(p => p.Owner == Owner) && runnerTarget is not null &&
                harmony.GetPatchInfo(runnerTarget) is { } runnerInfo && runnerInfo.Prefixes.Any(p => p.Owner == Owner) &&
                runnerInfo.Prefixes.All(p => p.Owner == Owner);
            }
            catch { return false; }
        }
    }
    internal SubtitlePatchHost(PluginRuntime runtime, IHttpResultFactory results, ILogger logger, SharedSubtitleNegotiation? negotiation = null)
    { this.runtime = runtime; this.results = results; this.logger = logger; this.negotiation = negotiation; }
    internal bool Install()
    {
        if (IsUncontended) return true;
        try
        {
            var type = Type.GetType("Emby.Web.Api.WebAppService, Emby.Web", true)!;
            var request = type.Assembly.GetType("Emby.Web.Api.GetDashboardResource", true)!;
            target = type.GetMethod("Get", new[] { request });
            if (target?.ReturnType != typeof(Task<object>) || request.GetProperty("ResourceName")?.PropertyType != typeof(string) ||
                type.GetProperty("DashboardUIPath")?.PropertyType != typeof(string)) return false;
            var service = Type.GetType("Emby.Server.MediaEncoding.Api.MediaInfoService, Emby.Server.MediaEncoding", true)!;
            playbackTarget = service.GetMethod("SetDeviceSpecificData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(long), typeof(string), typeof(MediaSourceInfo), typeof(DeviceProfile), typeof(AuthorizationInfo),
                    typeof(long), typeof(long), typeof(string), typeof(int?), typeof(int?), typeof(int?), typeof(string), typeof(User),
                    typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool) }, null);
            if (playbackTarget?.ReturnType != typeof(void) ||
                !playbackTarget.GetParameters().Select(p => p.Name).SequenceEqual(new[] { "itemId", "mediaType", "mediaSource", "profile", "auth",
                    "maxBitrate", "startTimeTicks", "mediaSourceId", "audioStreamIndex", "subtitleStreamIndex", "maxAudioChannels", "playSessionId", "user",
                    "enableDirectPlay", "enableDirectStream", "enableTranscoding", "allowVideoStreamCopy", "allowInterlacedVideoStreamCopy", "allowAudioStreamCopy" }) ||
                typeof(SubtitleProfile).GetProperty("AllowChunkedResponse")?.PropertyType != typeof(bool)) return false;
            var runner = Type.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfmpegRunner, Emby.Server.MediaEncoding", true)!;
            runnerTarget = FindRunnerTarget(runner);
            if (runnerTarget is null) return false;
            harmony = HarmonyRuntimeAdapter.Select(Owner, EmbeddedAssemblyLoader.EnsureHarmonyLoaded);
            if (harmony.GetPatchInfo(target)?.Prefixes.Any(p => p.Owner != Owner) == true ||
                harmony.GetPatchInfo(playbackTarget)?.Prefixes.Any(p => p.Owner != Owner) == true ||
                harmony.GetPatchInfo(runnerTarget)?.Prefixes.Any(p => p.Owner != Owner) == true) return false;
            if (Interlocked.CompareExchange(ref active, this, null) is not null) throw new InvalidOperationException("Web subtitle adapter already attached.");
            harmony.Patch(target, typeof(SubtitlePatchHost).GetMethod(nameof(Prefix), BindingFlags.Public | BindingFlags.Static));
            harmony.Patch(playbackTarget, typeof(SubtitlePatchHost).GetMethod(nameof(PlaybackPrefix), BindingFlags.Public | BindingFlags.Static));
            harmony.Patch(runnerTarget, typeof(FfmpegCommandPatchBridge).GetMethod(nameof(FfmpegCommandPatchBridge.TextPrefix)),
                typeof(FfmpegCommandPatchBridge).GetMethod(nameof(FfmpegCommandPatchBridge.TextPostfix)));
            if (!IsUncontended) throw new InvalidOperationException("Web subtitle adapter ownership unavailable.");
            logger.Info("STRM_BRIDGE_SUBTITLE_WEB_PATCH_READY");
            return true;
        }
        catch (Exception error) { Dispose(); logger.Warn("STRM_BRIDGE_SUBTITLE_WEB_PATCH_FAILED error=" + error.GetType().Name); return false; }
    }
    internal static MethodInfo? FindRunnerTarget(Type runner)
    {
        var method = runner.GetMethod("Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(CancellationToken) }, null);
        return method?.ReturnType == typeof(Task<bool>) &&
            runner.GetField("command", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
            runner.GetField("jobState", BindingFlags.Instance | BindingFlags.NonPublic) is not null &&
            runner.GetEvent("Exited")?.EventHandlerType == typeof(EventHandler<MediaBrowser.Model.Events.GenericEventArgs<int>>)
            ? method : null;
    }
    public static void PlaybackPrefix(long __0, string __1, MediaSourceInfo __2, ref DeviceProfile __3,
        User __12, ref bool __13, ref bool __14, bool __15, bool __runOriginal)
    {
        var handler = Volatile.Read(ref active);
        if (!__runOriginal || handler?.negotiation is null || !handler.CanServe ||
            !string.Equals(__1, "Video", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var profile = handler.negotiation.Prepare(__0, __2, __3, __12, __15);
            if (profile is null) return;
            __3 = profile; __13 = false; __14 = false;
            handler.logger.Debug("STRM_BRIDGE_SUBTITLE_NEGOTIATED transport=hls-ts input=shared-video");
        }
        catch (Exception error)
        { handler.logger.Warn("STRM_BRIDGE_SUBTITLE_NEGOTIATION_SKIPPED error=" + error.GetType().Name); }
    }
    public static bool Prefix(object __instance, object __0, bool __runOriginal, ref Task<object> __result)
    {
        var handler = Volatile.Read(ref active);
        if (!__runOriginal || handler is null || !handler.CanServe) return true;
        var name = __0.GetType().GetProperty("ResourceName")?.GetValue(__0) as string;
        if (name != PlayerResource && name != AdapterResource) return true;
        try
        {
            var request = __instance.GetType().GetProperty("Request")?.GetValue(__instance) as IRequest;
            if (request is null) return true;
            byte[] content;
            if (name == AdapterResource)
            {
                using var stream = typeof(SubtitlePatchHost).Assembly.GetManifestResourceStream("Emby.StrmBridge.Subtitles.Player.js")!;
                using var reader = new StreamReader(stream);
                content = Encoding.UTF8.GetBytes(reader.ReadToEnd());
            }
            else
            {
                var options = handler.runtime.GetOptionsSnapshot();
                if (!options.Enabled || !options.EnableSubtitles) return true;
                var directory = (string)__instance.GetType().GetProperty("DashboardUIPath")!.GetValue(__instance)!;
                var path = Path.Combine(directory, PlayerResource.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return true;
                var source = File.ReadAllText(path);
                var transformed = Transform(source);
                if (transformed is null)
                { Volatile.Write(ref handler.resourceStatus, "Unsupported"); handler.logger.Warn("STRM_BRIDGE_SUBTITLE_WEB_RESOURCE_UNSUPPORTED"); return true; }
                Volatile.Write(ref handler.resourceStatus, "Ready");
                content = Encoding.UTF8.GetBytes(transformed);
            }
            request.Response.StatusCode = 200;
            __result = Task.FromResult(handler.results.GetResult(request, new ReadOnlyMemory<byte>(content), "application/javascript; charset=utf-8",
                new Dictionary<string, string> { ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff" }));
            return false;
        }
        catch (Exception error)
        { handler.logger.Warn("STRM_BRIDGE_SUBTITLE_WEB_RESOURCE_FAILED error=" + error.GetType().Name); return true; }
    }
    internal static string? Transform(string source)
    {
        var digest = SubtitleDigest.Compute(source);
        var is49 = digest == "203a794fdd9fb401cade4d95af93bc33167c12aabf64ae9533caa571e15a548c";
        if (!is49 && digest != "4ac47f6b4f7c2d87b4e8db8a2ea6442cc6dc075c22bdd5e6161c5a5bbc900702") return null;
        var arguments = "instance,videoElement,track,item,mediaSource" + (is49 ? "" : ",signal");
        var anchor = "function renderTracksEvents(" + arguments + "){";
        var destruction = "function destroyCustomTrack(instance,videoElement){";
        var selection = "function setCurrentTrackElement(instance,mediaElement,streamIndex,currentPlayOptions){";
        var manualSelection = is49 ? "self.setSubtitleStreamIndex=function(index){" :
            "HtmlVideoPlayer.prototype.setSubtitleStreamIndex=function(index){";
        if (source.IndexOf(anchor, StringComparison.Ordinal) < 0 || source.IndexOf(destruction, StringComparison.Ordinal) < 0 || source.IndexOf(selection, StringComparison.Ordinal) < 0 || source.IndexOf(manualSelection, StringComparison.Ordinal) < 0) return null;
        var candidate = "function strmBridgeCandidate(track,mediaSource){return strmBridgeHlsCapable(mediaSource)&&track&&mediaSource&&!track.IsExternal&&track.DeliveryMethod===\"External\"&&/^(ass|ssa|srt|subrip)$/i.test(track.Codec||\"\")&&String(mediaSource.Container).toLowerCase()===\"mkv\"&&String(mediaSource.Protocol).toLowerCase()===\"http\";}";
        var synchronizeSelection = "function strmBridgeSelect(instance,index){var play=instance._currentPlayOptions;var track=play&&(play.mediaSource.MediaStreams||[]).filter(function(t){return t.Type===\"Subtitle\"&&t.Index===index;})[0];if(instance.strmBridgeSubtitle||strmBridgeCandidate(track,play&&play.mediaSource)){subtitleTrackIndexToSetOnPlaying=index;if(initialSubtitleTrackTimeout){clearTimeout(initialSubtitleTrackTimeout);initialSubtitleTrackTimeout=null;}}}";
        var wrapper = candidate + synchronizeSelection + "function renderTracksEvents(" + arguments + "){var epoch=instance._strmBridgeEpoch=(instance._strmBridgeEpoch||0)+1;" +
            "function fallback(){if(instance._strmBridgeEpoch===epoch)return renderTracksEventsNative(" + arguments + ");}" +
            "if(!strmBridgeCandidate(track,mediaSource))return fallback();" +
            "return Emby.importModule(\"./" + AdapterResource + "\").then(function(adapter){if(instance._strmBridgeEpoch!==epoch)return;return adapter.render({instance:instance,video:videoElement,track:track,item:item,source:mediaSource,epoch:epoch,api:_connectionmanager.default.getApiClient(item),fallback:fallback});},fallback);}" +
            "function renderTracksEventsNative(" + arguments + "){";
        var initialAnchor = "function startInitialSubtitleTrackTimeout(instance){";
        if (!source.Contains(initialAnchor, StringComparison.Ordinal)) return null;
        var profileAnchor = "_basehtmlplayer.default.call(this),";
        if (source.IndexOf(profileAnchor, StringComparison.Ordinal) < 0) return null;
        var profileWrapper = "function strmBridgeHlsCapable(mediaSource){return window.Worker&&window.ReadableStream&&window.AbortController&&window.TextDecoder&&!_browser.default.chromecast&&_htmlmediahelper.default.enableHlsJsPlayer(mediaSource?mediaSource.RunTimeTicks:null,\"Video\");}function strmBridgeInstallProfile(instance){var getProfile=instance.getDeviceProfile;instance.getDeviceProfile=function(){return getProfile.apply(this,arguments).then(function(profile){if(!strmBridgeHlsCapable(null))return profile;return Object.assign({},profile,{Name:\"" + SharedSubtitleNegotiation.ProfileName + "\"});});};}";
        return source.Replace(initialAnchor, initialAnchor +
            "var sbPlay=instance._currentPlayOptions,sbTrack=sbPlay&&(sbPlay.mediaSource.MediaStreams||[]).filter(function(t){return t.Type===\"Subtitle\"&&t.Index===subtitleTrackIndexToSetOnPlaying;})[0];if(strmBridgeCandidate(sbTrack,sbPlay&&sbPlay.mediaSource)){if(initialSubtitleTrackTimeout){clearTimeout(initialSubtitleTrackTimeout);initialSubtitleTrackTimeout=null;}setCurrentTrackElement(instance,instance._mediaElement,subtitleTrackIndexToSetOnPlaying,sbPlay);return;}").Replace(anchor, profileWrapper + wrapper).Replace(profileAnchor, profileAnchor + "strmBridgeInstallProfile(this),").Replace(manualSelection, manualSelection +
            "strmBridgeSelect(" + (is49 ? "self" : "this") + ",index);").Replace(selection, selection +
            "if(currentPlayOptions&&mediaElement){var sbTrack=(currentPlayOptions.mediaSource.MediaStreams||[]).filter(function(t){return t.Type===\"Subtitle\"&&t.Index===streamIndex;})[0];if(strmBridgeCandidate(sbTrack,currentPlayOptions.mediaSource)){instance.setSubtitleOffset(0);for(var sbI=0;sbI<mediaElement.textTracks.length;sbI++){var sbT=mediaElement.textTracks[sbI];sbT.mode=\"disabled\";removeCueEvents(instance,sbT);}setTrackForCustomDisplay(instance,mediaElement,sbTrack);return;}}").Replace(destruction, destruction +
            "instance._strmBridgeEpoch=(instance._strmBridgeEpoch||0)+1;if(instance.strmBridgeSubtitle){instance.strmBridgeSubtitle.dispose();instance.strmBridgeSubtitle=null;}");
    }
    public void Dispose()
    {
        Interlocked.CompareExchange(ref active, null, this);
        harmony?.UnpatchAll(Owner); harmony = null; target = null; playbackTarget = null; runnerTarget = null;
    }
}
