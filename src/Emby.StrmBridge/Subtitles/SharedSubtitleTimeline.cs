using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.StrmBridge.Subtitles;

// Native HLS anchors a newly requested segment at its playlist time, even when
// stream copy retained an earlier keyframe. Read the SAME local video output to
// map original ASS timestamps to that presentation timeline. No media input.
internal sealed class SharedSubtitleTimeline
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly string firstSegment;
    private readonly long playlistStart;
    private readonly long muxDelay;
    private readonly bool audio;
    private readonly SemaphoreSlim gate = new(1);
    private long? nativeOffset;

    internal SharedSubtitleTimeline(string firstSegment, long playlistStart, long muxDelay, bool audio)
    {
        this.firstSegment = firstSegment; this.playlistStart = playlistStart; this.muxDelay = muxDelay; this.audio = audio;
    }

    internal static SharedSubtitleTimeline? Create(string arguments, string? output, long start, bool audio)
    {
        if (!Regex.IsMatch(arguments, @"(^|\s)-segment_format\s+mpegts(\s|$)")) return null;
        var number = Regex.Match(arguments, @"(^|\s)-segment_start_number\s+(\d+)(\s|$)");
        var delay = Regex.Match(arguments, @"(^|\s)-max_delay\s+(\d+)(\s|$)");
        if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output) || !number.Success || !delay.Success ||
            !long.TryParse(delay.Groups[2].Value, out var microseconds) || microseconds > 60_000_000 ||
            Regex.IsMatch(arguments, @"(^|\s)-(segment_format_options|output_ts_offset|reset_timestamps|initial_offset)(\s|$)"))
            throw new SubtitleProblem("timeline-unavailable");
        var pattern = Regex.Match(Path.GetFileName(output), @"%0?(\d*)d");
        if (!pattern.Success || Regex.Matches(output, "%").Count != 1) throw new SubtitleProblem("timeline-unavailable");
        var width = pattern.Groups[1].Length == 0 ? 0 : int.Parse(pattern.Groups[1].Value, CultureInfo.InvariantCulture);
        if (width > 9) throw new SubtitleProblem("timeline-unavailable");
        var name = Path.GetFileName(output).Replace(pattern.Value, number.Groups[2].Value.PadLeft(width, '0'));
        return new SharedSubtitleTimeline(Path.Combine(Path.GetDirectoryName(output)!, name), start, microseconds * 20, audio);
    }

    internal async Task<long> ReadOffsetAsync(CancellationToken token, bool nativeHlsClock = true, long? mseTimestampOffsetTicks = null, bool waitForCreation = true)
    {
        if (!nativeHlsClock)
        {
            if (!mseTimestampOffsetTicks.HasValue) throw new SubtitleProblem("timeline-unavailable");
            return MapMseOffset(muxDelay, mseTimestampOffsetTicks.Value);
        }
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false)) throw new SubtitleProblem("timeline-unavailable");
        try
        {
            var cached = nativeOffset;
            if (cached.HasValue) return cached.Value;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var file = new FileStream(firstSegment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                        4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var bytes = new byte[MaximumBytes]; var count = 0;
                    while (count < bytes.Length)
                    {
                        var read = await file.ReadAsync(bytes, count, Math.Min(512 * 1024, bytes.Length - count), token).ConfigureAwait(false);
                        if (read == 0) break;
                        count += read;
                        if (TryFirstTimestamp(bytes, count, audio, playlistStart + muxDelay, out _, nativeHlsClock)) break;
                    }
                    if (TryFirstTimestamp(bytes, count, audio, playlistStart + muxDelay, out var first, nativeHlsClock))
                    {
                        var shift = playlistStart - (first - muxDelay);
                        if (Math.Abs(shift) > TimeSpan.FromMinutes(2).Ticks) throw new SubtitleProblem("timeline-unavailable");
                        // ASS carries centiseconds; use the same rounded shift for both ends.
                        var offset = (long)Math.Round(shift / 100000.0, MidpointRounding.AwayFromZero) * 100000;
                        nativeOffset = offset;
                        return offset;
                    }
                    if (count == MaximumBytes) throw new SubtitleProblem("timeline-unavailable");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (!waitForCreation) throw new SubtitleProblem("timeline-unavailable");
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            throw new SubtitleProblem("timeline-unavailable");
        }
        finally { gate.Release(); }
    }

    internal static long MapMseOffset(long muxDelay, long timestampOffset)
    {
        // hls.js keeps initPTS for a continuity across FFmpeg runner restarts.
        // ASS time -> TS time adds muxDelay; MSE subtracts its actual initPTS.
        // Do not infer a new MSE origin from each runner's retained keyframe.
        if (timestampOffset < -TimeSpan.FromDays(7).Ticks || timestampOffset > TimeSpan.FromDays(7).Ticks)
            throw new ArgumentException("Subtitle timestamp offset invalid.");
        var shift = muxDelay + timestampOffset;
        if (Math.Abs(shift) > TimeSpan.FromMinutes(2).Ticks) throw new SubtitleProblem("timeline-unavailable");
        return (long)Math.Round(shift / 100000.0, MidpointRounding.AwayFromZero) * 100000;
    }

    internal static bool TryFirstTimestamp(byte[] bytes, int count, bool needsAudio, long expectedTicks, out long firstTicks, bool nativeHlsClock = false)
    {
        var first = new Dictionary<int, long>(); bool video = false, audio = false;
        for (var at = 0; at + 188 <= count; at += 188)
        {
            if (bytes[at] != 0x47) { firstTicks = 0; return false; }
            if ((bytes[at + 1] & 0xc0) != 0x40 || (bytes[at + 3] & 0xc0) != 0) continue;
            var control = (bytes[at + 3] >> 4) & 3;
            if (control is 0 or 2) continue;
            var p = at + 4 + (control == 3 ? 1 + bytes[at + 4] : 0);
            if (p + 14 > at + 188 || bytes[p] != 0 || bytes[p + 1] != 0 || bytes[p + 2] != 1) continue;
            var id = bytes[p + 3]; bool isVideo = id >= 0xe0 && id <= 0xef, isAudio = (id >= 0xc0 && id <= 0xdf) || id == 0xbd;
            if ((!isVideo && !isAudio) || (bytes[p + 7] & 0x80) == 0 || bytes[p + 8] < 5) continue;
            var t = p + 9;
            if ((bytes[t] & 1) == 0 || (bytes[t + 2] & 1) == 0 || (bytes[t + 4] & 1) == 0) continue;
            long pts = ((long)(bytes[t] & 14) << 29) | ((long)bytes[t + 1] << 22) | ((long)(bytes[t + 2] & 254) << 14) |
                ((long)bytes[t + 3] << 7) | ((long)bytes[t + 4] >> 1);
            const long wrap = 1L << 33;
            var expectedPts = expectedTicks * 9 / 1000;
            pts += (long)Math.Round((expectedPts - pts) / (double)wrap) * wrap;
            var pid = ((bytes[at + 1] & 31) << 8) | bytes[at + 2];
            if (!first.ContainsKey(pid)) first.Add(pid, pts * 1000 / 9);
            video |= isVideo; audio |= isAudio;
            // Native HLS anchors the picture to its first video PTS. hls.js
            // remuxes with an initPTS shared by the earliest audio/video sample.
            if (nativeHlsClock && isVideo) { firstTicks = first[pid]; return true; }
            if (!nativeHlsClock && video && (!needsAudio || audio)) { firstTicks = first.Values.Min(); return true; }
        }
        firstTicks = 0; return false;
    }
}
