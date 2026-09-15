using System.Text;
using Emby.StrmBridge.Subtitles;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class SharedSubtitleTimelineTests
{
    [TestMethod]
    public async Task CompletedRunnerDoesNotWaitForADeletedFirstSegment()
    {
        using var workspace = new TestWorkspace();
        var timeline = new SharedSubtitleTimeline(Path.Combine(workspace.Path, "deleted.ts"), 0, 0, false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsExactlyAsync<SubtitleProblem>(() => timeline.ReadOffsetAsync(cancellation.Token, waitForCreation: false));
        Assert.AreEqual("timeline-unavailable", error.Reason);
    }

    [TestMethod]
    public async Task ColdKeyframeStartMapsAssToTheHlsPresentationClock()
    {
        using var workspace = new TestWorkspace();
        // HLS segment 3 claims 18 s; copied keyframe is 10 s; TS adds 10 s.
        var ts = workspace.Write("3.ts", ""); File.WriteAllBytes(ts, Packet(20 * 90000, 0xe0, 256));
        var timeline = SharedSubtitleTimeline.Create("-segment_format mpegts -segment_start_number 3 -max_delay 5000000",
            Path.Combine(workspace.Path, "%d.ts"), TimeSpan.FromSeconds(18).Ticks, false)!;
        var offset = await timeline.ReadOffsetAsync(CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromSeconds(8).Ticks, offset);
        var cue = "Dialogue: 0,0:00:11.00,0:00:13.00,Default,,0,0,0,,{\\fad(500,500)}cue\n";
        var ass = workspace.Write("cue.ass", "[Events]\n" + cue);
        using var stream = new SharedSubtitleStream(ass, new SubtitleRequestContext { Start = TimeSpan.FromSeconds(18).Ticks, End = TimeSpan.FromSeconds(24).Ticks },
            () => true, CancellationToken.None, CancellationToken.None, offset);
        using var reader = new StreamReader(stream);
        StringAssert.Contains(await reader.ReadToEndAsync(), "0:00:19.00,0:00:21.00,Default,,0,0,0,,{\\fad(500,500)}cue");
        // The earlier original cue must survive window filtering after the shift.
    }

    [TestMethod]
    public async Task MuxDelayAloneDoesNotShiftSubtitles()
    {
        using var workspace = new TestWorkspace();
        var ts = workspace.Write("0.ts", ""); File.WriteAllBytes(ts, Packet(10 * 90000, 0xe0, 256));
        var timeline = new SharedSubtitleTimeline(ts, 0, TimeSpan.FromSeconds(10).Ticks, false);
        Assert.AreEqual(0, await timeline.ReadOffsetAsync(CancellationToken.None));
    }

    [TestMethod]
    public void AudioPrerollAndPtsWrapUseTheEarliestPresentationTimestamp()
    {
        const long wrap = 1L << 33;
        var packets = Packet(wrap - 90000, 0xe0, 256).Concat(Packet(wrap - 180000, 0xc0, 257)).ToArray();
        Assert.IsFalse(SharedSubtitleTimeline.TryFirstTimestamp(packets, 188, true, -TimeSpan.TicksPerSecond, out _));
        Assert.IsTrue(SharedSubtitleTimeline.TryFirstTimestamp(packets, packets.Length, true, -TimeSpan.TicksPerSecond, out var ticks));
        Assert.AreEqual(-2 * TimeSpan.TicksPerSecond, ticks);
    }

    [TestMethod]
    public async Task LargeVideoPrefixStillFindsTheFirstAudioTimestamp()
    {
        using var workspace = new TestWorkspace();
        var bytes = new List<byte>(188 * 4002);
        bytes.AddRange(Packet(20 * 90000, 0xe0, 256));
        var padding = Enumerable.Repeat((byte)0xff, 188).ToArray();
        padding[0] = 0x47; padding[1] = 0x1f; padding[2] = 0xff; padding[3] = 0x10;
        for (var i = 0; i < 4000; i++) bytes.AddRange(padding);
        bytes.AddRange(Packet(20 * 90000 - 900, 0xc0, 257));
        var path = workspace.Write("3.ts", ""); File.WriteAllBytes(path, bytes.ToArray());
        var timeline = new SharedSubtitleTimeline(path, TimeSpan.FromSeconds(18).Ticks, TimeSpan.FromSeconds(10).Ticks, true);
        Assert.IsTrue(SharedSubtitleTimeline.TryFirstTimestamp(bytes.ToArray(), bytes.Count, true, TimeSpan.FromSeconds(28).Ticks, out var first));
        Assert.AreEqual(TimeSpan.FromMilliseconds(19990).Ticks, first);
        Assert.AreEqual(TimeSpan.FromSeconds(8).Ticks, await timeline.ReadOffsetAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task NativeHlsUsesTheVideoAnchorWithoutReusingTheMseAudioAnchor()
    {
        using var workspace = new TestWorkspace();
        // An audio packet leads video by 600 ms. Safari displays the first
        // video frame at playlist zero; hls.js uses the earlier shared initPTS.
        var bytes = Packet(10 * 90000, 0xc0, 257).Concat(Packet(10 * 90000 + 54000, 0xe0, 256)).ToArray();
        var path = workspace.Write("0.ts", ""); File.WriteAllBytes(path, bytes);
        var timeline = new SharedSubtitleTimeline(path, 0, TimeSpan.FromSeconds(10).Ticks, true);
        Assert.AreEqual(0, await timeline.ReadOffsetAsync(CancellationToken.None, false, -TimeSpan.FromSeconds(10).Ticks));
        Assert.AreEqual(-TimeSpan.FromMilliseconds(600).Ticks, await timeline.ReadOffsetAsync(CancellationToken.None, true));
        Assert.AreEqual(0, await timeline.ReadOffsetAsync(CancellationToken.None, false, -TimeSpan.FromSeconds(10).Ticks));
        StringAssert.Contains(SharedSubtitleStream.Shift("Dialogue: 0,0:00:02.00,0:00:04.00,Default,,0,0,0,,cue", -TimeSpan.FromMilliseconds(600).Ticks),
            "0:00:01.40,0:00:03.40");
    }

    [TestMethod]
    public async Task MissingLocalVideoTimelineHonorsCancellation()
    {
        using var workspace = new TestWorkspace();
        var timeline = new SharedSubtitleTimeline(Path.Combine(workspace.Path, "absent.ts"), 0, 0, false);
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAsync<OperationCanceledException>(() => timeline.ReadOffsetAsync(cancel.Token));
    }

    [TestMethod]
    public void InvalidTransportAndUnknownTimestampOptionsAreRejected()
    {
        Assert.IsFalse(SharedSubtitleTimeline.TryFirstTimestamp(new byte[188], 188, false, 0, out _));
        using var workspace = new TestWorkspace();
        Assert.ThrowsExactly<SubtitleProblem>(() => SharedSubtitleTimeline.Create(
            "-segment_format mpegts -segment_start_number 3 -max_delay 5000000 -reset_timestamps 1", Path.Combine(workspace.Path, "%d.ts"), 0, false));
        Assert.AreEqual(503, SubtitleRequestProcessor.ProblemStatus("timeline-unavailable"));
    }

    [TestMethod]
    public async Task MseRunnerRestartUsesActualBrowserInitPtsInsteadOfNewKeyframeOffset()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("39.ts", "");
        // A restarted runner retains keyframe 231.06 s for playlist 234 s.
        File.WriteAllBytes(path, Packet((long)(241.06 * 90000), 0xe0, 256));
        var timeline = new SharedSubtitleTimeline(path, TimeSpan.FromSeconds(234).Ticks, TimeSpan.FromSeconds(10).Ticks, false);
        Assert.AreEqual(TimeSpan.FromSeconds(2.94).Ticks, await timeline.ReadOffsetAsync(CancellationToken.None));
        // The same MSE continuity still uses initPTS 9.1 s from the first runner.
        Assert.AreEqual(TimeSpan.FromSeconds(.9).Ticks,
            await timeline.ReadOffsetAsync(CancellationToken.None, false, -TimeSpan.FromSeconds(9.1).Ticks));
        // A real continuity reset can establish a new origin. It must not reuse cache.
        Assert.AreEqual(TimeSpan.FromMilliseconds(2260).Ticks,
            await timeline.ReadOffsetAsync(CancellationToken.None, false, -TimeSpan.FromSeconds(7.74).Ticks));
        Assert.ThrowsExactly<ArgumentException>(() => SharedSubtitleTimeline.MapMseOffset(0, long.MinValue));
        Assert.ThrowsExactly<SubtitleProblem>(() => SharedSubtitleTimeline.MapMseOffset(0, TimeSpan.FromMinutes(3).Ticks));
    }

    internal static byte[] Packet(long pts, int id, int pid)
    {
        var p = Enumerable.Repeat((byte)0xff, 188).ToArray();
        p[0] = 0x47; p[1] = (byte)(0x40 | (pid >> 8)); p[2] = (byte)pid; p[3] = 0x10;
        p[4] = 0; p[5] = 0; p[6] = 1; p[7] = (byte)id; p[8] = 0; p[9] = 0; p[10] = 0x80; p[11] = 0x80; p[12] = 5;
        p[13] = (byte)(0x21 | ((pts >> 29) & 14)); p[14] = (byte)(pts >> 22);
        p[15] = (byte)(((pts >> 14) & 254) | 1); p[16] = (byte)(pts >> 7); p[17] = (byte)(((pts << 1) & 254) | 1);
        return p;
    }
}
