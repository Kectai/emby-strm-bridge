using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.StrmBridge.Playback;

internal readonly struct TransportStreamFormat
{
    public TransportStreamFormat(int packetStride, int syncOffset, long packetOrigin)
    {
        PacketStride = packetStride;
        SyncOffset = syncOffset;
        PacketOrigin = packetOrigin;
    }

    public int PacketStride { get; }

    public int SyncOffset { get; }

    public long PacketOrigin { get; }
}

internal readonly struct PcrSample
{
    public PcrSample(long packetOffset, int pid, long clock27Mhz)
    {
        PacketOffset = packetOffset;
        Pid = pid;
        Clock27Mhz = clock27Mhz;
    }

    public long PacketOffset { get; }

    public int Pid { get; }

    public long Clock27Mhz { get; }
}

internal readonly struct RandomAccessSample
{
    public RandomAccessSample(long packetOffset, int pid)
    {
        PacketOffset = packetOffset;
        Pid = pid;
    }

    public long PacketOffset { get; }

    public int Pid { get; }
}

internal sealed class TransportStreamAnalysis
{
    public TransportStreamAnalysis(
        TransportStreamFormat format,
        IReadOnlyList<PcrSample> pcrSamples,
        IReadOnlyList<RandomAccessSample> randomAccessSamples,
        int programClockPid,
        int videoPid,
        bool ambiguousProgram,
        int programCount,
        IReadOnlyList<TransportProgramMap> programs,
        bool programChanged)
    {
        Format = format;
        PcrSamples = pcrSamples ?? throw new ArgumentNullException(nameof(pcrSamples));
        RandomAccessSamples = randomAccessSamples ??
            throw new ArgumentNullException(nameof(randomAccessSamples));
        ProgramClockPid = programClockPid;
        VideoPid = videoPid;
        AmbiguousProgram = ambiguousProgram;
        ProgramCount = programCount;
        Programs = programs;
        ProgramChanged = programChanged;
    }

    public TransportStreamFormat Format { get; }

    public IReadOnlyList<PcrSample> PcrSamples { get; }

    public IReadOnlyList<RandomAccessSample> RandomAccessSamples { get; }

    public int ProgramClockPid { get; }

    public int VideoPid { get; }

    public bool AmbiguousProgram { get; }

    private int ProgramCount { get; }
    private IReadOnlyList<TransportProgramMap> Programs { get; }
    private bool ProgramChanged { get; }

    public bool TrySelectVideo(FastSeekVideoSelection? selection,
        out int videoPid, out int clockPid, out TransportProgramMap? program)
    {
        videoPid = clockPid = -1;
        program = null;
        if (ProgramChanged) return false;
        if (selection is null)
        {
            if (AmbiguousProgram) return false;
            clockPid = SelectClockPid();
            videoPid = VideoPid >= 0 ? VideoPid : clockPid;
            program = Programs.Count == 1 ? Programs[0] : null;
            return true;
        }
        // FFmpeg creates video streams in PMT declaration order, not numeric PID order.
        // Emby's stream metadata has indices but no PID; multiple programs cannot be correlated safely here.
        if (ProgramCount != 1 || Programs.Count != 1 || !selection.Matches(Programs[0])) return false;
        program = Programs[0];
        videoPid = program.Videos[selection.Ordinal].Pid;
        clockPid = program.ClockPid;
        return true;
    }

    public bool MatchesProgram(TransportProgramMap? program) => !ProgramChanged &&
        (program is null ? !AmbiguousProgram :
            ProgramCount <= 1 && Programs.All(candidate => candidate.Matches(program)));

    public int SelectClockPid()
    {
        if (AmbiguousProgram) return -1;
        if (ProgramClockPid >= 0) return ProgramClockPid;
        var selected = PcrSamples
            .GroupBy(sample => sample.Pid)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => group.Key)
            .Take(1)
            .ToArray();
        return selected.Length == 0 ? -1 : selected[0];
    }

    public bool TryGetFirstPcr(int pid, out PcrSample sample)
    {
        foreach (var candidate in PcrSamples)
        {
            if (candidate.Pid != pid) continue;
            sample = candidate;
            return true;
        }
        sample = default;
        return false;
    }

    public bool TryGetFirstRandomAccess(int preferredPid, out RandomAccessSample sample)
    {
        foreach (var candidate in RandomAccessSamples)
        {
            if (candidate.Pid != preferredPid) continue;
            sample = candidate;
            return true;
        }
        if (RandomAccessSamples.Count > 0)
        {
            sample = RandomAccessSamples[0];
            return true;
        }
        sample = default;
        return false;
    }
}

internal sealed class TransportProgramMap
{
    public TransportProgramMap(int pmtPid, int number, int clockPid, IReadOnlyList<(int Pid, int StreamType)> videos)
    {
        PmtPid = pmtPid;
        Number = number;
        ClockPid = clockPid;
        Videos = videos;
    }

    public int PmtPid { get; }
    public int Number { get; }
    public int ClockPid { get; }
    public IReadOnlyList<(int Pid, int StreamType)> Videos { get; }

    public bool Matches(TransportProgramMap other) => PmtPid == other.PmtPid && Number == other.Number &&
        ClockPid == other.ClockPid && Videos.SequenceEqual(other.Videos);
}

internal static class TransportStreamClockParser
{
    internal const long ClockFrequency = 27_000_000;
    internal const long ClockWrap = (1L << 33) * 300L;
    private const int MinimumSyncPackets = 5;
    private static readonly int[] PacketStrides = { 188, 192, 204 };

    public static bool TryAnalyze(
        ReadOnlySpan<byte> bytes,
        long rangeStart,
        TransportStreamFormat? expectedFormat,
        out TransportStreamAnalysis? analysis)
    {
        analysis = null;
        if (rangeStart < 0 || bytes.Length < MinimumSyncPackets * 188) return false;
        if (!TryFindFormat(bytes, rangeStart, expectedFormat, out var format, out var firstSync)) return false;

        var samples = new List<PcrSample>();
        var randomAccessSamples = new List<RandomAccessSample>();
        var programMapPids = new HashSet<int>();
        var videoStreams = new Dictionary<int, int>();
        var videoClockPids = new Dictionary<int, int>();
        var programs = new Dictionary<int, TransportProgramMap>();
        var programChanged = false;
        var codecPayloadStates = new Dictionary<int, CodecPayloadState>();
        for (var sync = firstSync; sync + 188 <= bytes.Length; sync += format.PacketStride)
        {
            if (bytes[sync] != 0x47) break;
            var packetOffset = checked(rangeStart + sync - format.SyncOffset);
            if (packetOffset < format.PacketOrigin) continue;
            var packet = bytes.Slice(sync, 188);
            if (!TryReadPacket(packet, out var pid, out var randomAccess, out var hasPcr, out var clock))
                continue;
            var randomAccessOffset = packetOffset;
            var adaptationRandomAccess = randomAccess;
            randomAccess = false;
            if (TryGetPayload(packet, out var payload, out var payloadUnitStart))
            {
                randomAccess = adaptationRandomAccess && payloadUnitStart && IsVideoPesStart(payload);
                if (pid == 0 && payloadUnitStart)
                    ReadProgramAssociation(payload, programMapPids);
                else if (programMapPids.Contains(pid) && payloadUnitStart)
                {
                    var program = ReadProgramMap(payload, pid);
                    if (program is not null)
                    {
                        if (programs.TryGetValue(pid, out var previous) && !previous.Matches(program))
                            programChanged = true;
                        programs[pid] = program;
                        foreach (var entry in program.Videos)
                        {
                            videoStreams[entry.Pid] = entry.StreamType;
                            videoClockPids[entry.Pid] = program.ClockPid;
                        }
                    }
                }
                if (videoStreams.TryGetValue(pid, out var streamType))
                {
                    if (!codecPayloadStates.TryGetValue(pid, out var state))
                    {
                        state = new CodecPayloadState();
                        codecPayloadStates.Add(pid, state);
                    }
                    if (state.Observe(
                            payload,
                            payloadUnitStart,
                            packetOffset,
                            streamType,
                            packet[3] & 0x0f,
                            out var codecAnchorOffset))
                    {
                        randomAccess = true;
                        randomAccessOffset = codecAnchorOffset;
                    }
                }
            }
            if (randomAccess &&
                (randomAccessSamples.Count == 0 ||
                 randomAccessSamples[^1].PacketOffset != randomAccessOffset ||
                 randomAccessSamples[^1].Pid != pid))
                randomAccessSamples.Add(new RandomAccessSample(randomAccessOffset, pid));
            if (hasPcr) samples.Add(new PcrSample(packetOffset, pid, clock));
        }
        if (samples.Count == 0) return false;
        var video = videoStreams.OrderBy(pair => pair.Key).FirstOrDefault();
        analysis = new TransportStreamAnalysis(
            format,
            samples,
            randomAccessSamples,
            videoClockPids.TryGetValue(video.Key, out var clockPid) ? clockPid : -1,
            videoStreams.Count == 0 ? -1 : video.Key,
            videoStreams.Count > 1 || programMapPids.Count > 1 || programChanged,
            programMapPids.Count,
            programs.Values.ToArray(),
            programChanged);
        return true;
    }

    public static double SecondsBetween(long firstClock, long laterClock)
    {
        var delta = laterClock - firstClock;
        if (delta < 0) delta += ClockWrap;
        return delta / (double)ClockFrequency;
    }

    public static long AlignDown(long absoluteOffset, TransportStreamFormat format)
    {
        if (absoluteOffset <= format.PacketOrigin) return format.PacketOrigin;
        var relative = absoluteOffset - format.PacketOrigin;
        return format.PacketOrigin + relative / format.PacketStride * format.PacketStride;
    }

    private static bool TryFindFormat(
        ReadOnlySpan<byte> bytes,
        long rangeStart,
        TransportStreamFormat? expected,
        out TransportStreamFormat format,
        out int firstSync)
    {
        if (expected.HasValue)
        {
            var candidate = expected.Value;
            if (!PacketStrides.Contains(candidate.PacketStride) ||
                candidate.SyncOffset < 0 || candidate.SyncOffset >= candidate.PacketStride)
            {
                format = default;
                firstSync = -1;
                return false;
            }
            var limit = Math.Min(candidate.PacketStride, bytes.Length);
            for (var index = 0; index < limit; index++)
            {
                var absoluteSync = rangeStart + index;
                var expectedSync = candidate.PacketOrigin + candidate.SyncOffset;
                if (PositiveMod(absoluteSync - expectedSync, candidate.PacketStride) != 0 ||
                    CountSyncPackets(bytes, index, candidate.PacketStride) < MinimumSyncPackets)
                    continue;
                format = candidate;
                firstSync = index;
                return true;
            }
            format = default;
            firstSync = -1;
            return false;
        }

        var bestCount = 0;
        var bestStride = 0;
        var bestSync = -1;
        foreach (var stride in PacketStrides)
        {
            var limit = Math.Min(stride, bytes.Length);
            for (var index = 0; index < limit; index++)
            {
                var count = CountSyncPackets(bytes, index, stride);
                if (count < MinimumSyncPackets || count <= bestCount) continue;
                bestCount = count;
                bestStride = stride;
                bestSync = index;
            }
        }
        if (bestSync < 0)
        {
            format = default;
            firstSync = -1;
            return false;
        }

        var syncOffset = bestStride == 192 ? 4 : 0;
        var origin = rangeStart + bestSync - syncOffset;
        if (origin < 0)
        {
            format = default;
            firstSync = -1;
            return false;
        }
        format = new TransportStreamFormat(bestStride, syncOffset, origin);
        firstSync = bestSync;
        return true;
    }

    private static int CountSyncPackets(ReadOnlySpan<byte> bytes, int firstSync, int stride)
    {
        var count = 0;
        for (var index = firstSync; index < bytes.Length; index += stride)
        {
            if (bytes[index] != 0x47) break;
            count++;
        }
        return count;
    }

    private static bool TryReadPacket(
        ReadOnlySpan<byte> packet,
        out int pid,
        out bool randomAccess,
        out bool hasPcr,
        out long clock)
    {
        pid = 0;
        randomAccess = false;
        hasPcr = false;
        clock = 0;
        if (packet.Length < 5 || packet[0] != 0x47 || (packet[1] & 0x80) != 0) return false;
        pid = (packet[1] & 0x1f) << 8 | packet[2];
        var adaptationControl = packet[3] >> 4 & 0x03;
        if (adaptationControl == 0) return false;
        if (adaptationControl == 1) return true;
        var adaptationLength = packet[4];
        if (adaptationLength + 5 > packet.Length) return false;
        if (adaptationLength == 0) return true;
        randomAccess = (packet[5] & 0x40) != 0;
        if (adaptationLength < 7 || (packet[5] & 0x10) == 0) return true;
        var pcrBase = (long)packet[6] << 25 |
                      (long)packet[7] << 17 |
                      (long)packet[8] << 9 |
                      (long)packet[9] << 1 |
                      (long)packet[10] >> 7;
        var extension = (packet[10] & 0x01) << 8 | packet[11];
        clock = checked(pcrBase * 300L + extension);
        hasPcr = clock >= 0 && clock < ClockWrap;
        return true;
    }

    private static bool TryGetPayload(
        ReadOnlySpan<byte> packet,
        out ReadOnlySpan<byte> payload,
        out bool payloadUnitStart)
    {
        payload = default;
        payloadUnitStart = false;
        if (packet.Length < 4 || packet[0] != 0x47 || (packet[1] & 0x80) != 0) return false;
        payloadUnitStart = (packet[1] & 0x40) != 0;
        var adaptationControl = packet[3] >> 4 & 0x03;
        if (adaptationControl is not 1 and not 3) return false;
        var offset = 4;
        if (adaptationControl == 3)
        {
            if (offset >= packet.Length) return false;
            offset += packet[offset] + 1;
        }
        if (offset >= packet.Length) return false;
        payload = packet.Slice(offset);
        return true;
    }

    private static void ReadProgramAssociation(ReadOnlySpan<byte> payload, ISet<int> programMapPids)
    {
        if (!TryGetPsiSection(payload, 0x00, out var section) || section.Length < 12) return;
        var end = section.Length - 4;
        for (var offset = 8; offset + 4 <= end; offset += 4)
        {
            var programNumber = section[offset] << 8 | section[offset + 1];
            if (programNumber == 0) continue;
            programMapPids.Add((section[offset + 2] & 0x1f) << 8 | section[offset + 3]);
        }
    }

    private static TransportProgramMap? ReadProgramMap(ReadOnlySpan<byte> payload, int pmtPid)
    {
        if (!TryGetPsiSection(payload, 0x02, out var section) || section.Length < 16) return null;
        var programClockPid = (section[8] & 0x1f) << 8 | section[9];
        if (programClockPid == 0x1fff) return null;
        var programInfoLength = (section[10] & 0x0f) << 8 | section[11];
        var offset = 12 + programInfoLength;
        var end = section.Length - 4;
        var videos = new List<(int Pid, int StreamType)>();
        while (offset + 5 <= end)
        {
            var streamType = section[offset];
            var pid = (section[offset + 1] & 0x1f) << 8 | section[offset + 2];
            var infoLength = (section[offset + 3] & 0x0f) << 8 | section[offset + 4];
            if (offset + 5 + infoLength > end) return null;
            if (IsVideoStreamType(streamType))
            {
                if (videos.Any(video => video.Pid == pid)) return null;
                videos.Add((pid, streamType));
            }
            offset += 5 + infoLength;
        }
        return offset == end
            ? new TransportProgramMap(pmtPid, section[3] << 8 | section[4], programClockPid, videos.ToArray())
            : null;
    }

    private static bool TryGetPsiSection(
        ReadOnlySpan<byte> payload,
        byte expectedTableId,
        out ReadOnlySpan<byte> section)
    {
        section = default;
        if (payload.Length < 4) return false;
        var start = 1 + payload[0];
        if (start < 1 || start + 3 > payload.Length || payload[start] != expectedTableId) return false;
        var sectionLength = (payload[start + 1] & 0x0f) << 8 | payload[start + 2];
        var totalLength = 3 + sectionLength;
        if (sectionLength < 9 || start + totalLength > payload.Length) return false;
        section = payload.Slice(start, totalLength);
        if ((section[5] & 1) == 0 || section[6] != 0 || section[7] != 0) return false;
        uint crc = uint.MaxValue;
        foreach (var value in section)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
        }
        return crc == 0;
    }

    private static bool IsVideoPesStart(ReadOnlySpan<byte> payload) =>
        payload.Length >= 9 && payload[0] == 0 && payload[1] == 0 && payload[2] == 1 &&
        (payload[3] is >= 0xe0 and <= 0xef or 0xfd) && (payload[6] & 0xc0) == 0x80 &&
        9 + payload[8] <= payload.Length;

    internal static bool ContainsCodecRandomAccess(ReadOnlySpan<byte> payload, int streamType)
    {
        for (var index = 0; index + 5 < payload.Length; index++)
        {
            if (payload[index] != 0 || payload[index + 1] != 0) continue;
            var header = -1;
            if (payload[index + 2] == 1) header = index + 3;
            else if (index + 4 < payload.Length && payload[index + 2] == 0 && payload[index + 3] == 1)
                header = index + 4;
            if (header < 0 || header >= payload.Length) continue;
            if (streamType == 0x1b && (payload[header] & 0x1f) == 5) return true;
            if (streamType == 0x24)
            {
                var nalType = payload[header] >> 1 & 0x3f;
                if (nalType is >= 16 and <= 23) return true;
            }
            if (streamType == 0x02 && payload[header] == 0x00 && header + 2 < payload.Length)
            {
                var pictureCodingType = payload[header + 2] >> 3 & 0x07;
                if (pictureCodingType == 1) return true;
            }
        }
        return false;
    }

    internal static bool ContainsCodecRandomAccessAcrossBoundary(
        ReadOnlySpan<byte> previousTail,
        ReadOnlySpan<byte> payload,
        int streamType)
    {
        if (previousTail.Length == 0 || payload.Length == 0) return false;
        var tailLength = Math.Min(4, previousTail.Length);
        Span<byte> boundary = stackalloc byte[10];
        previousTail.Slice(previousTail.Length - tailLength, tailLength).CopyTo(boundary);
        var prefixLength = Math.Min(payload.Length, boundary.Length - tailLength);
        payload.Slice(0, prefixLength).CopyTo(boundary.Slice(tailLength));
        return ContainsCodecRandomAccess(boundary.Slice(0, tailLength + prefixLength), streamType);
    }

    private sealed class CodecPayloadState
    {
        private readonly byte[] tail = new byte[4];
        private int tailLength;
        private int currentStreamType = -1;
        private int lastContinuityCounter = -1;
        private long accessUnitOffset = -1;

        public bool Observe(
            ReadOnlySpan<byte> payload,
            bool payloadUnitStart,
            long packetOffset,
            int streamType,
            int continuityCounter,
            out long anchorOffset)
        {
            if (streamType != currentStreamType)
            {
                currentStreamType = streamType;
                tailLength = 0;
                lastContinuityCounter = -1;
                accessUnitOffset = -1;
            }
            if (!payloadUnitStart && lastContinuityCounter >= 0 &&
                continuityCounter != ((lastContinuityCounter + 1) & 0x0f))
            {
                tailLength = 0;
                accessUnitOffset = -1;
            }
            if (payloadUnitStart)
            {
                tailLength = 0;
                accessUnitOffset = IsVideoPesStart(payload) ? packetOffset : -1;
            }

            var found = ContainsCodecRandomAccess(payload, streamType);
            if (!found)
                found = ContainsCodecRandomAccessAcrossBoundary(
                    tail.AsSpan(0, tailLength),
                    payload,
                    streamType);

            UpdateTail(payload);
            lastContinuityCounter = continuityCounter;
            anchorOffset = accessUnitOffset >= 0 ? accessUnitOffset : packetOffset;
            return found && accessUnitOffset >= 0;
        }

        private void UpdateTail(ReadOnlySpan<byte> payload)
        {
            if (payload.Length >= tail.Length)
            {
                payload.Slice(payload.Length - tail.Length).CopyTo(tail);
                tailLength = tail.Length;
                return;
            }

            Span<byte> combined = stackalloc byte[8];
            tail.AsSpan(0, tailLength).CopyTo(combined);
            payload.CopyTo(combined.Slice(tailLength));
            var combinedLength = tailLength + payload.Length;
            tailLength = Math.Min(tail.Length, combinedLength);
            combined.Slice(combinedLength - tailLength, tailLength).CopyTo(tail);
        }
    }

    private static bool IsVideoStreamType(int streamType) =>
        streamType is 0x01 or 0x02 or 0x10 or 0x1b or 0x20 or 0x24 or 0x42 or 0xea;

    private static long PositiveMod(long value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
