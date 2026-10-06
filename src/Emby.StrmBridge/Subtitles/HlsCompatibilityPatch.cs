using System;
using System.IO;

namespace Emby.StrmBridge.Subtitles;

// Backport video-dev/hls.js#7984 to the library shipped by the verified Emby
// 4.9.5.0 / 4.10.0.40 Web clients. Do not replace the library or patch unknown builds.
internal static class HlsCompatibilityPatch
{
    internal const string Resource = "modules/hlsjs/hls.js";
    internal const string BaseResource = "modules/htmlvideoplayer/basehtmlplayer.js";
    // Distinct module identities bypass both HTTP and AMD caches of host scripts.
    internal const string VersionedResource = "modules/strmbridge/hls-seek-v1.js";
    internal const string VersionedBaseResource = "modules/strmbridge/basehtmlplayer-hls-v1.js";

    internal static bool CanAdapt(string directory)
    {
        var hls = Path.Combine(directory, Resource);
        var player = Path.Combine(directory, BaseResource);
        return File.Exists(hls) && File.Exists(player) && new FileInfo(hls).Length <= 1024 * 1024 &&
            new FileInfo(player).Length <= 1024 * 1024 && Transform(File.ReadAllText(hls)) is not null &&
            TransformBase(File.ReadAllText(player)) is not null;
    }

    internal static string? TransformBase(string source)
    {
        var digest = SubtitleDigest.Compute(source);
        if (digest != "8c61400307b6a13a668731bbc086b5673538f9aaee261e7b8d2bb49403a6e531" &&
            digest != "b3864a5dbebd14ed6d5e858fded2ff87f5c66119af4d3ca3475f82219482f848") return null;
        return source.Replace("./" + Resource, "./" + VersionedResource);
    }

    internal static string? Transform(string source)
    {
        if (SubtitleDigest.Compute(source) != "04a55387b26d6becff5b87b470b9b19c3fe41d25c0cd3c1e6c3d885a56572fdb") return null;
        var changes = new[]
        {
            // Match upstream: prefer ordinary MSE where available; MMS remains
            // the fallback on platforms which only expose ManagedMediaSource.
            ("preferManagedMediaSource:!0,", "preferManagedMediaSource:!1,"),
            ("media.addEventListener(\"emptied\",this._onMediaEmptied)}}",
             "media.addEventListener(\"emptied\",this._onMediaEmptied),this.appendSource&&media.addEventListener(\"seeking\",this._onStartStreaming)}}"),
            ("_this2.hls&&\"open\"===(null==(_this2$mediaSource=_this2.mediaSource)?void 0:_this2$mediaSource.readyState)&&_this2.hls.pauseBuffering()",
             "_this2.hls&&_this2.media&&!_this2.media.seeking&&\"open\"===(null==(_this2$mediaSource=_this2.mediaSource)?void 0:_this2$mediaSource.readyState)&&_this2.hls.pauseBuffering()"),
            ("media.removeEventListener(\"emptied\",this._onMediaEmptied),transferringMedia||",
             "media.removeEventListener(\"emptied\",this._onMediaEmptied),media.removeEventListener(\"seeking\",this._onStartStreaming),transferringMedia||")
        };
        foreach (var (before, after) in changes)
        {
            var at = source.IndexOf(before, StringComparison.Ordinal);
            if (at < 0 || source.IndexOf(before, at + before.Length, StringComparison.Ordinal) >= 0) return null;
            source = source.Replace(before, after);
        }
        return source;
    }
}
