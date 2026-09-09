using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Emby.StrmBridge.Domain;

[DataContract]
public sealed class MediaInfoSnapshot
{
    public const int CurrentSchemaVersion = 3;
    public const int MaximumMediaStreams = 256;

    [DataMember(Order = 1)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [DataMember(Order = 2)]
    public string SourceFingerprint { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public long LocalFileLength { get; set; }

    [DataMember(Order = 4)]
    public long LocalLastWriteUtcTicks { get; set; }

    [DataMember(Order = 5)]
    public long ExtractedAtUtcTicks { get; set; }

    [DataMember(Order = 6)]
    public string? Container { get; set; }

    [DataMember(Order = 7)]
    public long? Size { get; set; }

    [DataMember(Order = 8)]
    public int? Bitrate { get; set; }

    [DataMember(Order = 9)]
    public long? RunTimeTicks { get; set; }

    [DataMember(Order = 10)]
    public int? DefaultAudioStreamIndex { get; set; }

    [DataMember(Order = 11)]
    public int? DefaultSubtitleStreamIndex { get; set; }

    [DataMember(Order = 12)]
    public List<MediaStreamSnapshot> MediaStreams { get; set; } = new();

    public static MediaInfoSnapshot FromMediaSource(
        SourceIdentity source,
        MediaSourceInfo mediaSource,
        DateTimeOffset extractedAtUtc)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (mediaSource is null) throw new ArgumentNullException(nameof(mediaSource));
        return new MediaInfoSnapshot
        {
            SourceFingerprint = source.SourceFingerprint,
            LocalFileLength = source.LocalFileLength,
            LocalLastWriteUtcTicks = source.LocalLastWriteUtc.UtcDateTime.Ticks,
            ExtractedAtUtcTicks = extractedAtUtc.UtcDateTime.Ticks,
            Container = NullIfSensitive(mediaSource.Container),
            Size = NonNegative(mediaSource.Size),
            Bitrate = NonNegative(mediaSource.Bitrate),
            RunTimeTicks = mediaSource.RunTimeTicks,
            DefaultAudioStreamIndex = mediaSource.DefaultAudioStreamIndex,
            DefaultSubtitleStreamIndex = mediaSource.DefaultSubtitleStreamIndex,
            MediaStreams = SelectInternalStreams(mediaSource.MediaStreams)
                .Select(MediaStreamSnapshot.FromMediaStream)
                .ToList(),
        };
    }

    public static bool IsSupportedStreamType(MediaStreamType type) =>
        type == MediaStreamType.Video ||
        type == MediaStreamType.Audio ||
        type == MediaStreamType.Subtitle;

    internal static List<MediaStream> SelectInternalStreams(IEnumerable<MediaStream>? mediaStreams)
    {
        var supported = (mediaStreams ?? Enumerable.Empty<MediaStream>())
            .Where(stream => !stream.IsExternal && IsSupportedStreamType(stream.Type))
            .ToList();
        if (supported.Count <= MaximumMediaStreams) return supported;

        var selected = supported.Take(MaximumMediaStreams).ToList();
        if (selected.Any(stream => stream.Type == MediaStreamType.Video)) return selected;
        var video = supported.FirstOrDefault(stream => stream.Type == MediaStreamType.Video);
        if (video is not null) selected[selected.Count - 1] = video;
        return selected;
    }

    public bool Matches(SourceIdentity source) =>
        SchemaVersion == CurrentSchemaVersion &&
        string.Equals(SourceFingerprint, source.SourceFingerprint, StringComparison.Ordinal) &&
        LocalFileLength == source.LocalFileLength &&
        TechnicalMediaInfo.IsComplete(
            Container,
            RunTimeTicks,
            MediaStreams?.Any(stream =>
                stream is not null &&
                (stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio)) == true);

    public MediaSourceInfo ToMediaSource(string id)
    {
        return new MediaSourceInfo
        {
            Id = id,
            Container = Container,
            Size = Size,
            Bitrate = Bitrate,
            RunTimeTicks = RunTimeTicks,
            DefaultAudioStreamIndex = DefaultAudioStreamIndex,
            DefaultSubtitleStreamIndex = DefaultSubtitleStreamIndex,
            MediaStreams = MediaStreams.Select(stream => stream.ToMediaStream()).ToList(),
        };
    }

    private static string? NullIfSensitive(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsControl) ||
        value.IndexOfAny(new[] { '/', '\\', '?', '#', '@' }) >= 0
            ? null
            : value;

    private static long? NonNegative(long? value) => value is >= 0 ? value : null;

    private static int? NonNegative(int? value) => value is >= 0 ? value : null;
}

#pragma warning disable CS0612 // Preserve the SDK's legacy AVC negotiation field across snapshots.
[DataContract]
public sealed class MediaStreamSnapshot
{
    [DataMember(Order = 1)] public MediaStreamType Type { get; set; }
    [DataMember(Order = 2)] public int Index { get; set; }
    [DataMember(Order = 3)] public string? Codec { get; set; }
    [DataMember(Order = 4)] public string? Profile { get; set; }
    [DataMember(Order = 5)] public double? Level { get; set; }
    [DataMember(Order = 6)] public string? PixelFormat { get; set; }
    [DataMember(Order = 7)] public int? BitRate { get; set; }
    [DataMember(Order = 8)] public int? Channels { get; set; }
    [DataMember(Order = 9)] public int? SampleRate { get; set; }
    [DataMember(Order = 10)] public string? ChannelLayout { get; set; }
    [DataMember(Order = 11)] public int? BitDepth { get; set; }
    [DataMember(Order = 12)] public int? Width { get; set; }
    [DataMember(Order = 13)] public int? Height { get; set; }
    [DataMember(Order = 14)] public float? AverageFrameRate { get; set; }
    [DataMember(Order = 15)] public float? RealFrameRate { get; set; }
    [DataMember(Order = 16)] public string? AspectRatio { get; set; }
    [DataMember(Order = 18)] public string? ColorSpace { get; set; }
    [DataMember(Order = 19)] public string? ColorTransfer { get; set; }
    [DataMember(Order = 20)] public string? ColorPrimaries { get; set; }
    [DataMember(Order = 21)] public bool IsInterlaced { get; set; }
    [DataMember(Order = 22)] public bool IsDefault { get; set; }
    [DataMember(Order = 23)] public bool IsForced { get; set; }
    [DataMember(Order = 24)] public string? Language { get; set; }

    [DataMember(Order = 25)] public ExtendedVideoTypes ExtendedVideoType { get; set; }
    [DataMember(Order = 26)] public ExtendedVideoSubTypes ExtendedVideoSubType { get; set; }
    [DataMember(Order = 27)] public int? Rotation { get; set; }
    [DataMember(Order = 28)] public string? CodecTag { get; set; }
    [DataMember(Order = 29)] public int? RefFrames { get; set; }
    [DataMember(Order = 30)] public bool? IsAVC { get; set; }
    [DataMember(Order = 31)] public string? NalLengthSize { get; set; }
    [DataMember(Order = 32)] public long? StreamStartTimeTicks { get; set; }
    [DataMember(Order = 33)] public bool? IsAnamorphic { get; set; }
    [DataMember(Order = 34)] public bool IsHearingImpaired { get; set; }
    [DataMember(Order = 35)] public string? TimeBase { get; set; }

    public static MediaStreamSnapshot FromMediaStream(MediaStream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        return new MediaStreamSnapshot
        {
            Type = stream.Type,
            Index = stream.Index,
            Codec = TechnicalString(stream.Codec),
            Profile = TechnicalString(stream.Profile),
            Level = NonNegativeFinite(stream.Level),
            PixelFormat = TechnicalString(stream.PixelFormat),
            BitRate = NonNegative(stream.BitRate),
            Channels = NonNegative(stream.Channels),
            SampleRate = NonNegative(stream.SampleRate),
            ChannelLayout = TechnicalString(stream.ChannelLayout),
            BitDepth = NonNegative(stream.BitDepth),
            Width = NonNegative(stream.Width),
            Height = NonNegative(stream.Height),
            AverageFrameRate = NonNegativeFinite(stream.AverageFrameRate),
            RealFrameRate = NonNegativeFinite(stream.RealFrameRate),
            AspectRatio = TechnicalString(stream.AspectRatio),
            ColorSpace = TechnicalString(stream.ColorSpace),
            ColorTransfer = TechnicalString(stream.ColorTransfer),
            ColorPrimaries = TechnicalString(stream.ColorPrimaries),
            IsInterlaced = stream.IsInterlaced,
            IsDefault = stream.IsDefault,
            IsForced = stream.IsForced,
            Language = TechnicalString(stream.Language),
            ExtendedVideoType = stream.ExtendedVideoType,
            ExtendedVideoSubType = stream.ExtendedVideoSubType,
            Rotation = stream.Rotation,
            CodecTag = TechnicalString(stream.CodecTag),
            RefFrames = NonNegative(stream.RefFrames),
            IsAVC = stream.IsAVC,
            NalLengthSize = TechnicalString(stream.NalLengthSize),
            StreamStartTimeTicks = stream.StreamStartTimeTicks,
            IsAnamorphic = stream.IsAnamorphic,
            IsHearingImpaired = stream.IsHearingImpaired,
            TimeBase = IsTimeBase(stream.TimeBase) ? stream.TimeBase : null,
        };
    }

    public MediaStream ToMediaStream() => new()
    {
        Type = Type,
        Index = Index,
        Codec = Codec,
        Profile = Profile,
        Level = Level,
        PixelFormat = PixelFormat,
        BitRate = BitRate,
        Channels = Channels,
        SampleRate = SampleRate,
        ChannelLayout = ChannelLayout,
        BitDepth = BitDepth,
        Width = Width,
        Height = Height,
        AverageFrameRate = AverageFrameRate,
        RealFrameRate = RealFrameRate,
        AspectRatio = AspectRatio,
        ColorSpace = ColorSpace,
        ColorTransfer = ColorTransfer,
        ColorPrimaries = ColorPrimaries,
        IsInterlaced = IsInterlaced,
        IsDefault = IsDefault,
        IsForced = IsForced,
        Language = Language,
        ExtendedVideoType = ExtendedVideoType,
        ExtendedVideoSubType = ExtendedVideoSubType,
        Rotation = Rotation,
        CodecTag = CodecTag,
        RefFrames = RefFrames,
        IsAVC = IsAVC,
        NalLengthSize = NalLengthSize,
        StreamStartTimeTicks = StreamStartTimeTicks,
        IsAnamorphic = IsAnamorphic,
        IsHearingImpaired = IsHearingImpaired,
        TimeBase = TimeBase,
    };

    internal static bool IsTimeBase(string? value)
    {
        if (value is null) return true;
        var parts = value.Split('/');
        return value.Length <= 32 && parts.Length == 2 &&
               long.TryParse(parts[0], System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var numerator) && numerator > 0 &&
               long.TryParse(parts[1], System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var denominator) && denominator > 0;
    }

    private static string? TechnicalString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(character => char.IsControl(character) || character == '/' || character == '\\' || character == '?' || character == '#'))
        {
            return null;
        }
        return value;
    }

    private static int? NonNegative(int? value) => value is >= 0 ? value : null;

    private static double? NonNegativeFinite(double? value) =>
        value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value >= 0
            ? value
            : null;

    private static float? NonNegativeFinite(float? value) =>
        value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value) && value.Value >= 0
            ? value
            : null;
}

#pragma warning restore CS0612
