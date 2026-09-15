using System;
using System.Linq;
using System.Reflection;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Policy;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;

namespace Emby.StrmBridge.Subtitles;

// Runs inside the host's authenticated, per-source playback negotiation. No URL
// is manufactured here: the host retains codec, bitrate, copy and user policy.
internal sealed class SharedSubtitleNegotiation
{
    internal const string ProfileName = "STRM Bridge Web subtitles v2";
    private readonly PluginRuntime runtime;
    private readonly ILibraryManager library;
    private readonly IMediaSourceManager sources;
    private readonly Func<bool> canProvideSubtitles;

    internal SharedSubtitleNegotiation(PluginRuntime runtime, ILibraryManager library, IMediaSourceManager sources, Func<bool>? canProvideSubtitles = null)
    { this.runtime = runtime; this.library = library; this.sources = sources; this.canProvideSubtitles = canProvideSubtitles ?? (() => runtime.SubtitlePatch?.CanServe == true); }

    internal DeviceProfile? Prepare(long itemId, MediaSourceInfo source, DeviceProfile profile, User user, bool enableTranscoding)
    {
        var options = runtime.GetOptionsSnapshot();
        if (!canProvideSubtitles() || !options.Enabled || !options.EnableSubtitles || options.PlaybackMode == PlaybackRoutingMode.Native ||
            runtime.SourcePolicy is null || profile?.Name != ProfileName || !enableTranscoding ||
            user?.Policy is null || user.Policy.IsDisabled || !user.Policy.EnableMediaPlayback ||
            !(user.Policy.EnablePlaybackRemuxing || user.Policy.EnableVideoPlaybackTranscoding || user.Policy.EnableAudioPlaybackTranscoding))
            return null;
        var requested = library.GetItemById(itemId);
        var item = string.IsNullOrEmpty(source.ItemId) ? requested : ResolveItem(source.ItemId);
        bool Included(BaseItem? value) => value is not null && PlaybackItemPolicy.IsVideo(value) &&
            library.GetCollectionFolders(value).Any(folder => options.IncludedLibraryIds.Any(id => Guid.TryParse(id, out var selected) && selected == folder.Id));
        if (!Included(requested) || !Included(item) ||
            !string.Equals(System.IO.Path.GetExtension(item!.Path), ".strm", StringComparison.OrdinalIgnoreCase)) return null;
        var identity = runtime.SourcePolicy.Read(item.Path);
        if (!StaticMediaSourcePolicy.Matches(sources, item, identity) || !StaticMediaSourcePolicy.MatchesPlaybackSource(source, identity)) return null;
        return CreateProfile(source, profile);
    }

    internal static DeviceProfile? CreateProfile(MediaSourceInfo source, DeviceProfile profile)
    {
        if (!source.SupportsTranscoding || profile.Name != ProfileName || !string.Equals(source.Container, "mkv", StringComparison.OrdinalIgnoreCase) ||
            source.RequiredHttpHeaders?.Count > 0 || source.RequiresOpening || source.RequiresClosing ||
            !string.IsNullOrEmpty(source.OpenToken) || !string.IsNullOrEmpty(source.LiveStreamId)) return null;
        var tracks = source.MediaStreams?.Where(SharedSubtitleOutput.IsTextTrack).ToArray();
        if (tracks is null || tracks.Length == 0 || tracks.Length > SharedSubtitleOutput.MaximumTracks ||
            tracks.Select(t => t.Index).Distinct().Count() != tracks.Length) return null;
        var hls = profile.TranscodingProfiles?.FirstOrDefault(p => p.Type == DlnaProfileType.Video &&
            p.Context == EncodingContext.Streaming && string.Equals(p.Protocol, "hls", StringComparison.OrdinalIgnoreCase) &&
            (p.Container ?? "").Split(',').Any(c => c.Trim().Equals("ts", StringComparison.OrdinalIgnoreCase)));
        if (hls is null) return null;
        var selected = Copy(hls);
        // A combined m4s,ts profile can also advertise AV1/VP9/Opus. Those
        // capabilities must not be transferred blindly to the TS demux path.
        selected.VideoCodec = SelectCodecs(hls.VideoCodec, "h264", "hevc");
        selected.AudioCodec = SelectCodecs(hls.AudioCodec, "aac", "mp3", "mp2", "ac3", "eac3");
        if (selected.VideoCodec.Length == 0 || (source.MediaStreams!.Any(t => t.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio) && selected.AudioCodec.Length == 0)) return null;
        selected.Container = "ts";
        selected.ManifestSubtitles = null;
        selected.MaxManifestSubtitles = 0;
        var result = Copy(profile);
        result.TranscodingProfiles = new[] { selected };
        // Keep the original profile untouched for other media versions. Supported
        // text is rendered by the same Web libass in every browser, not an HLS track.
        result.SubtitleProfiles = new[] { "ass", "ssa", "srt", "subrip" }
            .Select(TextProfile)
            .Concat((profile.SubtitleProfiles ?? Array.Empty<SubtitleProfile>()).Where(p => p.Method != SubtitleDeliveryMethod.Hls))
            .ToArray();
        return result;
    }

    private static string SelectCodecs(string? declared, params string[] supported) => string.Join(",",
        (declared ?? "").Split(',').Select(c => c.Trim()).Where(c => supported.Contains(c, StringComparer.OrdinalIgnoreCase)));

    private static SubtitleProfile TextProfile(string format)
    {
        var profile = new SubtitleProfile { Format = format, Method = SubtitleDeliveryMethod.External };
        // The released host has this property; the reference SDK predates it.
        // It permits incremental ASS delivery without requiring native extraction.
        typeof(SubtitleProfile).GetProperty("AllowChunkedResponse")?.SetValue(profile, true);
        return profile;
    }

    private BaseItem? ResolveItem(string id) => long.TryParse(id, out var numeric) ? library.GetItemById(numeric) :
        Guid.TryParse(id, out var guid) ? library.GetItemById(guid) : null;

    private static T Copy<T>(T source) where T : new()
    {
        var clone = new T();
        // Host versions add profile fields. Preserve every public data property;
        // only replace arrays/properties owned by this negotiation.
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.GetMethod is not null && property.SetMethod is not null && property.GetIndexParameters().Length == 0)
                property.SetValue(clone, property.GetValue(source));
        return clone;
    }
}
