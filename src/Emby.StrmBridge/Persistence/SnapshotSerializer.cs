using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Policy;
using MediaBrowser.Model.Entities;

namespace Emby.StrmBridge.Persistence;

public sealed class SnapshotSerializer
{
    public const int MaximumSerializedBytes = 512 * 1024;

    public byte[] Serialize(MediaInfoSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        Validate(snapshot);
        using var stream = new MemoryStream();
        CreateSerializer().WriteObject(stream, snapshot);
        if (stream.Length > MaximumSerializedBytes)
            throw new InvalidDataException("The snapshot exceeds the configured size limit.");
        return stream.ToArray();
    }

    public MediaInfoSnapshot Deserialize(byte[] data) => Deserialize(data, allowLegacyReplacement: false);

    internal MediaInfoSnapshot Deserialize(byte[] data, bool allowLegacyReplacement)
    {
        if (data is null || data.Length == 0) throw new InvalidDataException("The snapshot is empty.");
        if (data.Length > MaximumSerializedBytes)
            throw new InvalidDataException("The snapshot exceeds the configured size limit.");
        using var stream = new MemoryStream(data, writable: false);
        var result = CreateSerializer().ReadObject(stream) as MediaInfoSnapshot
            ?? throw new InvalidDataException("The snapshot has an invalid shape.");
        Validate(result, allowLegacyReplacement);
        return result;
    }

    private static void Validate(MediaInfoSnapshot snapshot, bool allowLegacyReplacement = false)
    {
        if (snapshot.SchemaVersion != MediaInfoSnapshot.CurrentSchemaVersion &&
            !(allowLegacyReplacement && snapshot.SchemaVersion == 2) ||
            !IsIdentity(snapshot.SourceFingerprint) ||
            snapshot.LocalFileLength < 1 ||
            snapshot.LocalFileLength > StrmSourcePolicy.DefaultMaximumFileSize ||
            !IsDateTimeTicks(snapshot.LocalLastWriteUtcTicks) ||
            !IsDateTimeTicks(snapshot.ExtractedAtUtcTicks) ||
            !IsContainer(snapshot.Container) ||
            snapshot.Size is < 0 ||
            snapshot.Bitrate is < 0 ||
            snapshot.DefaultAudioStreamIndex is < 0 ||
            snapshot.DefaultSubtitleStreamIndex is < 0 ||
            snapshot.MediaStreams is null ||
            snapshot.MediaStreams.Count == 0 ||
            snapshot.MediaStreams.Count > MediaInfoSnapshot.MaximumMediaStreams ||
            snapshot.MediaStreams.Any(stream => !IsMediaStream(stream)) ||
            snapshot.MediaStreams.Select(stream => stream!.Index).Distinct().Count() !=
            snapshot.MediaStreams.Count ||
            !TechnicalMediaInfo.IsComplete(
                snapshot.Container,
                snapshot.RunTimeTicks,
                snapshot.MediaStreams.Any(stream =>
                    stream!.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio)) ||
            snapshot.DefaultAudioStreamIndex.HasValue && !snapshot.MediaStreams.Any(stream =>
                stream!.Type == MediaStreamType.Audio &&
                stream.Index == snapshot.DefaultAudioStreamIndex.Value) ||
            snapshot.DefaultSubtitleStreamIndex.HasValue && !snapshot.MediaStreams.Any(stream =>
                stream!.Type == MediaStreamType.Subtitle &&
                stream.Index == snapshot.DefaultSubtitleStreamIndex.Value))
        {
            throw new InvalidDataException("The snapshot schema or field values are invalid.");
        }
    }

    private static bool IsMediaStream(MediaStreamSnapshot? stream)
    {
        if (stream is null ||
            !MediaInfoSnapshot.IsSupportedStreamType(stream.Type) ||
            stream.Index < 0 ||
            !Enum.IsDefined(typeof(ExtendedVideoTypes), stream.ExtendedVideoType) ||
            !Enum.IsDefined(typeof(ExtendedVideoSubTypes), stream.ExtendedVideoSubType) ||
            stream.RefFrames is < 0 ||
            !MediaStreamSnapshot.IsTimeBase(stream.TimeBase) ||
            stream.Level is double level && (double.IsNaN(level) || double.IsInfinity(level) || level < 0) ||
            stream.BitRate is < 0 ||
            stream.Channels is < 0 ||
            stream.SampleRate is < 0 ||
            stream.BitDepth is < 0 ||
            stream.Width is < 0 ||
            stream.Height is < 0 ||
            !IsRate(stream.AverageFrameRate) ||
            !IsRate(stream.RealFrameRate))
        {
            return false;
        }

        return IsTechnicalString(stream.Codec) &&
               IsTechnicalString(stream.Profile) &&
               IsTechnicalString(stream.PixelFormat) &&
               IsTechnicalString(stream.ChannelLayout) &&
               IsTechnicalString(stream.AspectRatio) &&
               IsTechnicalString(stream.ColorSpace) &&
               IsTechnicalString(stream.ColorTransfer) &&
               IsTechnicalString(stream.ColorPrimaries) &&
               IsTechnicalString(stream.Language) &&
               IsTechnicalString(stream.CodecTag) &&
               IsTechnicalString(stream.NalLengthSize);
    }

    private static bool IsIdentity(string? value) =>
        value?.Length == 64 && value.All(character =>
            character >= '0' && character <= '9' || character >= 'a' && character <= 'f');

    private static bool IsDateTimeTicks(long value) =>
        value >= DateTime.MinValue.Ticks && value <= DateTime.MaxValue.Ticks;

    private static bool IsContainer(string? value) =>
        value is null ||
        (!string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
         !value.Any(character => char.IsControl(character) ||
                                 character == '/' || character == '\\' ||
                                 character == '?' || character == '#' || character == '@'));

    private static bool IsTechnicalString(string? value) =>
        value is null ||
        (!string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
         !value.Any(character => char.IsControl(character) ||
                                 character == '/' || character == '\\' ||
                                 character == '?' || character == '#'));

    private static bool IsRate(float? value) =>
        !value.HasValue ||
        (!float.IsNaN(value.Value) && !float.IsInfinity(value.Value) && value.Value >= 0);

    private static DataContractJsonSerializer CreateSerializer() => new(typeof(MediaInfoSnapshot));
}
