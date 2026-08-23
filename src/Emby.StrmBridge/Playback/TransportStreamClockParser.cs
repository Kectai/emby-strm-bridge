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

internal sealed class TransportStreamAnalysis
{
    public TransportStreamAnalysis(TransportStreamFormat format, IReadOnlyList<PcrSample> pcrSamples)
    {
        Format = format;
        PcrSamples = pcrSamples ?? throw new ArgumentNullException(nameof(pcrSamples));
    }

    public TransportStreamFormat Format { get; }

    public IReadOnlyList<PcrSample> PcrSamples { get; }

    public int SelectClockPid()
    {
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
        for (var sync = firstSync; sync + 188 <= bytes.Length; sync += format.PacketStride)
        {
            if (bytes[sync] != 0x47) break;
            if (!TryReadPcr(bytes.Slice(sync, 188), out var pid, out var clock)) continue;
            var packetOffset = checked(rangeStart + sync - format.SyncOffset);
            if (packetOffset < format.PacketOrigin) continue;
            samples.Add(new PcrSample(packetOffset, pid, clock));
        }
        if (samples.Count == 0) return false;
        analysis = new TransportStreamAnalysis(format, samples);
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

    private static bool TryReadPcr(ReadOnlySpan<byte> packet, out int pid, out long clock)
    {
        pid = 0;
        clock = 0;
        if (packet.Length < 12 || packet[0] != 0x47 || (packet[1] & 0x80) != 0) return false;
        pid = (packet[1] & 0x1f) << 8 | packet[2];
        var adaptationControl = packet[3] >> 4 & 0x03;
        if (adaptationControl is not 2 and not 3) return false;
        var adaptationLength = packet[4];
        if (adaptationLength < 7 || adaptationLength + 5 > packet.Length || (packet[5] & 0x10) == 0)
            return false;
        var pcrBase = (long)packet[6] << 25 |
                      (long)packet[7] << 17 |
                      (long)packet[8] << 9 |
                      (long)packet[9] << 1 |
                      (long)packet[10] >> 7;
        var extension = (packet[10] & 0x01) << 8 | packet[11];
        clock = checked(pcrBase * 300L + extension);
        return clock >= 0 && clock < ClockWrap;
    }

    private static long PositiveMod(long value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
