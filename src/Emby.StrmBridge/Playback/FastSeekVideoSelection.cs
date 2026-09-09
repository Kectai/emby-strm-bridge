using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Emby.StrmBridge.Playback;

internal sealed class FastSeekVideoSelection
{
    private readonly string[] codecs;

    private FastSeekVideoSelection(int streamIndex, int ordinal, MediaStream[] videos)
    {
        StreamIndex = streamIndex;
        Ordinal = ordinal;
        codecs = videos.Select(video => video.Codec.ToLowerInvariant()).ToArray();
        CacheKey = streamIndex.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",",
            videos.Select(video => video.Index.ToString(CultureInfo.InvariantCulture) + "=" +
                                   video.Codec.ToLowerInvariant()));
    }

    public int StreamIndex { get; }
    public int Ordinal { get; }
    public string CacheKey { get; }

    public static bool TryCreate(MediaSourceInfo source, MediaStream? selected, out FastSeekVideoSelection? selection)
    {
        selection = null;
        if (selected is null) return true;
        if (selected.Type != MediaStreamType.Video || selected.IsExternal || selected.Index < 0) return false;
        var videos = ((IEnumerable<MediaStream>?)source.MediaStreams ?? Array.Empty<MediaStream>())
            .Where(stream => stream.Type == MediaStreamType.Video && !stream.IsExternal)
            .OrderBy(stream => stream.Index).ToArray();
        if (videos.Length == 0 || videos.Length > 64 ||
            videos.Any(video => video.Index < 0 || string.IsNullOrWhiteSpace(video.Codec)) ||
            videos.Select(video => video.Index).Distinct().Count() != videos.Length)
            return false;
        var ordinal = Array.FindIndex(videos, video => video.Index == selected.Index);
        if (ordinal < 0 || !string.Equals(videos[ordinal].Codec, selected.Codec, StringComparison.OrdinalIgnoreCase))
            return false;
        selection = new FastSeekVideoSelection(selected.Index, ordinal, videos);
        return true;
    }

    public bool Matches(TransportProgramMap program) => program.Videos.Count == codecs.Length &&
        program.Videos.Select((video, index) => CodecForType(video.StreamType) == codecs[index] ||
            video.StreamType == 0x01 && codecs[index] == "mpeg2video").All(match => match);

    private static string? CodecForType(int type) => type switch
    {
        0x01 => "mpeg1video",
        0x02 => "mpeg2video",
        0x10 => "mpeg4",
        0x1b => "h264",
        0x20 => "h264",
        0x24 => "hevc",
        0x42 => "cavs",
        0xea => "vc1",
        _ => null,
    };
}
