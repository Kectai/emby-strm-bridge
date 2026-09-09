using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class FastSeekVideoSelectionTests
{
    [TestMethod]
    [DataRow(0x01, "mpeg1video")]
    [DataRow(0x01, "mpeg2video")]
    [DataRow(0x02, "mpeg2video")]
    [DataRow(0x1b, "h264")]
    [DataRow(0x20, "h264")]
    [DataRow(0x24, "hevc")]
    [DataRow(0x42, "cavs")]
    [DataRow(0xea, "vc1")]
    public void Selection_MatchesStandardTransportStreamCodecTypes(int streamType, string codec)
    {
        var video = new MediaStream { Type = MediaStreamType.Video, Index = 7, Codec = codec };
        Assert.IsTrue(FastSeekVideoSelection.TryCreate(new MediaSourceInfo { MediaStreams = new() { video } },
            video, out var selection));
        Assert.IsTrue(selection!.Matches(new TransportProgramMap(100, 1, 901, new[] { (701, streamType) })));
    }

    [TestMethod]
    [DataRow(188)]
    [DataRow(192)]
    [DataRow(204)]
    public void Parser_MapsSelectedVideoInDeclarationOrderToItsProgramClock(int stride)
    {
        var bytes = MultiVideoFastSeekProbeClient.Sample(stride, 0, 512 * 1024);
        Assert.IsTrue(TransportStreamClockParser.TryAnalyze(bytes, 0, null, out var analysis));
        Assert.IsFalse(analysis!.TrySelectVideo(null, out _, out _, out _));
        foreach (var ordinal in new[] { 0, 1 })
        {
            var selection = Selection(ordinal);
            Assert.IsTrue(analysis.TrySelectVideo(selection, out var videoPid, out var clockPid, out var program));
            Assert.AreEqual(ordinal == 0 ? 701 : 257, videoPid);
            Assert.AreEqual(900, clockPid);
            Assert.IsTrue(analysis.MatchesProgram(program));
        }
    }

    [TestMethod]
    public void Selection_RejectsIncompleteMismatchingOrDuplicateVideoMetadata()
    {
        var source = new MediaSourceInfo { MediaStreams = MultiVideoFastSeekProbeClient.Streams() };
        var selected = source.MediaStreams[1];
        Assert.IsFalse(FastSeekVideoSelection.TryCreate(source,
            new MediaStream { Type = MediaStreamType.Video, Index = 99, Codec = "hevc" }, out _));
        source.MediaStreams[0].Index = selected.Index;
        Assert.IsFalse(FastSeekVideoSelection.TryCreate(source, selected, out _));
        source.MediaStreams = new() { selected };
        Assert.IsTrue(FastSeekVideoSelection.TryCreate(source, selected, out var incomplete));
        Assert.IsTrue(TransportStreamClockParser.TryAnalyze(
            MultiVideoFastSeekProbeClient.Sample(188, 0, 8192), 0, null, out var analysis));
        Assert.IsFalse(analysis!.TrySelectVideo(incomplete, out _, out _, out _));
        source.MediaStreams = MultiVideoFastSeekProbeClient.Streams();
        source.MediaStreams[1].Codec = "h264";
        Assert.IsTrue(FastSeekVideoSelection.TryCreate(source, source.MediaStreams[1], out var mismatch));
        Assert.IsFalse(analysis.TrySelectVideo(mismatch, out _, out _, out _));
    }

    [TestMethod]
    public void Parser_RequiresValidatedProgramTablesForExplicitSelection()
    {
        var bytes = MultiVideoFastSeekProbeClient.Sample(188, 0, 8192);
        bytes[188 + 5 + 12] ^= 1;
        Assert.IsTrue(TransportStreamClockParser.TryAnalyze(bytes, 0, null, out var corrupt));
        Assert.IsFalse(corrupt!.TrySelectVideo(Selection(0), out _, out _, out _));
    }

    [TestMethod]
    public async Task Coordinator_SeparatesPlansAndNegativeCacheBySelectedVideo()
    {
        var probe = new MultiVideoFastSeekProbeClient(192);
        var coordinator = new FastSeekCoordinator(probe, new ManualClock(), FastSeekCoordinatorTests.CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(50).Ticks;
        var duration = TimeSpan.FromSeconds(100).Ticks;
        var options = new PluginConfiguration();
        Assert.IsFalse(await coordinator.PrepareAsync(source, "media", target, duration, 1,
            options, CancellationToken.None));
        var offsets = new List<long>();
        foreach (var ordinal in new[] { 0, 1, 0 })
        {
            var selection = Selection(ordinal);
            Assert.IsTrue(await coordinator.PrepareAsync(source, "media", target, duration, 1,
                options, CancellationToken.None, selection));
            Assert.IsTrue(coordinator.TryBindInput(source, "media", target, duration, 1,
                "http://127.0.0.1/job/" + ordinal, options, selection));
            Assert.IsTrue(coordinator.TryGetBoundPlan("http://127.0.0.1/job/" + ordinal, 1, target,
                CancellationToken.None, out var plan));
            Assert.AreEqual(selection.StreamIndex, plan!.VideoStreamIndex);
            Assert.AreEqual(2, plan.ProbeCount);
            Assert.AreEqual(4, plan.RelativeSeek.TotalSeconds, 0.01);
            offsets.Add(plan.ByteOffset);
        }
        Assert.AreNotEqual(offsets[0], offsets[1]);
        Assert.AreEqual(offsets[0], offsets[2]);
        Assert.AreEqual(5, probe.ReadCount);
        Assert.IsFalse(coordinator.TryBindInput(source, "media", target, duration, 1,
            "http://127.0.0.1/no-selection", options));
    }

    [TestMethod]
    public async Task Coordinator_RejectsChangedProgramAndMissingSelectedKeyframes()
    {
        foreach (var changeProgram in new[] { false, true })
        {
            var probe = new MultiVideoFastSeekProbeClient(188)
            {
                ChangeProgramAtTarget = changeProgram,
                OmitSecondVideoKeyframes = !changeProgram,
            };
            var coordinator = new FastSeekCoordinator(probe, new ManualClock(), FastSeekCoordinatorTests.CreateLogger());
            Assert.IsFalse(await coordinator.PrepareAsync(TestSources.Create(), "media",
                TimeSpan.FromSeconds(50).Ticks, TimeSpan.FromSeconds(100).Ticks, 0,
                new PluginConfiguration(), CancellationToken.None, Selection(1)));
        }
    }

    [TestMethod]
    public async Task Coordinator_ConcurrentTracksShareOnlyTheirOwnPendingPreparation()
    {
        var probe = new MultiVideoFastSeekProbeClient(188) { Delay = true };
        var coordinator = new FastSeekCoordinator(probe, new ManualClock(), FastSeekCoordinatorTests.CreateLogger());
        var source = TestSources.Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(index => coordinator.PrepareAsync(
            source, "media", TimeSpan.FromSeconds(50).Ticks, TimeSpan.FromSeconds(100).Ticks, 0,
            new PluginConfiguration(), CancellationToken.None, Selection(index % 2))));
        Assert.IsTrue(results.All(result => result));
        Assert.AreEqual(4, probe.ReadCount);
    }

    internal static FastSeekVideoSelection Selection(int ordinal)
    {
        var source = new MediaSourceInfo { MediaStreams = MultiVideoFastSeekProbeClient.Streams() };
        Assert.IsTrue(FastSeekVideoSelection.TryCreate(source, source.MediaStreams[ordinal], out var selection));
        return selection!;
    }
}

internal sealed class MultiVideoFastSeekProbeClient(int stride) : IFastSeekProbeClient
{
    private const long Length = 100L * 1024 * 1024;
    private int readCount;
    public int ReadCount => readCount;
    public bool ChangeProgramAtTarget { get; init; }
    public bool OmitSecondVideoKeyframes { get; init; }
    public bool Delay { get; init; }

    public static List<MediaStream> Streams() => new()
    {
        new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
        new() { Type = MediaStreamType.Video, Index = 3, Codec = "hevc" },
    };

    public async Task<FastSeekProbeResult> ReadAsync(SourceIdentity source, long offset, int maximumBytes,
        PluginConfiguration options, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref readCount);
        if (Delay) await Task.Delay(20, cancellationToken);
        return new FastSeekProbeResult(offset, Length, Sample(stride, offset, maximumBytes,
            ChangeProgramAtTarget && offset > 0, OmitSecondVideoKeyframes),
            new FastSeekRepresentation("\"synthetic\"", Length));
    }

    internal static byte[] Sample(int stride, long offset, int maximumBytes,
        bool changeProgram = false, bool omitSecondKeyframes = false)
    {
        var count = (int)(Math.Min(maximumBytes, Length - offset) / stride);
        var bytes = TransportStreamTestData.Create(stride, offset, count,
            packet => packet % 3 == 0 ? 900 : packet % 3 == 1 ? 701 : 257,
            packet => (long)((offset + packet * stride) * (100d / Length) * 27_000_000));
        var sync = stride == 192 ? 4 : 0;
        for (var packet = 0; packet < count; packet++)
        {
            var index = packet * stride + sync;
            if (packet % 3 == 0) bytes[index + 5] &= 0xbf;
            else bytes[index + 5] &= 0xef;
            if (omitSecondKeyframes && packet % 3 == 2) bytes[index + 5] &= 0xbf;
        }
        WritePsi(bytes.AsSpan(sync, 188), 0, new byte[]
            { 0, 0xb0, 13, 0, 1, 0xc1, 0, 0, 0, 1, 0xe0, 100, 0, 0, 0, 0 });
        var clockPid = changeProgram ? 901 : 900;
        WritePsi(bytes.AsSpan(stride + sync, 188), 100, new byte[]
        {
            2, 0xb0, 23, 0, 1, 0xc1, 0, 0, (byte)(0xe0 | (clockPid >> 8)), (byte)clockPid,
            0xf0, 0, 0x1b, 0xe2, 0xbd, 0xf0, 0, 0x24, 0xe1, 1, 0xf0, 0, 0, 0, 0, 0,
        });
        return bytes;
    }

    private static void WritePsi(Span<byte> packet, int pid, byte[] section)
    {
        uint crc = uint.MaxValue;
        for (var i = 0; i < section.Length - 4; i++)
        {
            crc ^= (uint)section[i] << 24;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
        }
        for (var i = 0; i < 4; i++) section[section.Length - 4 + i] = (byte)(crc >> (24 - i * 8));
        packet.Fill(0xff);
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | (pid >> 8));
        packet[2] = (byte)pid;
        packet[3] = 0x10;
        packet[4] = 0;
        section.CopyTo(packet[5..]);
    }
}
