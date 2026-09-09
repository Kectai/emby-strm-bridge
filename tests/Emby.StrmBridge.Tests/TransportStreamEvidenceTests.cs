using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class TransportStreamEvidenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Parser_RequiresPesStartAfterAnUnknownStartOrContinuityGap(bool gap)
    {
        var continuation = new byte[] { 0, 0, 1, 0x65, 0, 0 };
        var first = gap ? new byte[] { 0, 0, 1, 0xe0, 0, 0, 0x80, 0, 0 } : new byte[] { 0x22 };
        var bytes = Packet(Pat(false), 0, true, 0)
            .Concat(Packet(Pmt(256, 1), 100, true, 0))
            .Concat(Packet(first, 256, gap, 0))
            .Concat(Packet(continuation, 256, false, gap ? 2 : 1))
            .Concat(ClockPackets(256)).ToArray();
        Assert.IsTrue(TransportStreamClockParser.TryAnalyze(bytes, 0, null, out var analysis));
        Assert.IsEmpty(analysis!.RandomAccessSamples);
    }

    [TestMethod]
    public void Parser_PairsProgramClocksAndRejectsAmbiguousMultiProgramPlans()
    {
        var bytes = Packet(Pat(true), 0, true, 0)
            .Concat(Packet(Pmt(256, 1), 100, true, 0))
            .Concat(Packet(Pmt(512, 2), 101, true, 0))
            .Concat(ClockPackets(256)).Concat(ClockPackets(512)).ToArray();
        Assert.IsTrue(TransportStreamClockParser.TryAnalyze(bytes, 0, null, out var analysis));
        Assert.AreEqual(256, analysis!.VideoPid);
        Assert.AreEqual(256, analysis.ProgramClockPid);
        Assert.IsTrue(analysis.AmbiguousProgram);
        Assert.AreEqual(-1, analysis.SelectClockPid());
    }

    [TestMethod]
    public async Task Coordinator_RejectsAVbrAnchorThatIsActuallyAfterTheTarget()
    {
        var logger = TestProxy.Create<ILogger>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
        var coordinator = new FastSeekCoordinator(new VbrProbe(), new ManualClock(), logger);
        Assert.IsFalse(await coordinator.PrepareAsync(TestSources.Create(), "media",
            TimeSpan.FromSeconds(50).Ticks, TimeSpan.FromSeconds(100).Ticks, 0,
            new PluginConfiguration(), CancellationToken.None));
    }

    [TestMethod]
    public async Task Coordinator_RepresentationChangeInvalidatesOnlyAffectedSourcePlans()
    {
        var logger = TestProxy.Create<ILogger>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
        var coordinator = new FastSeekCoordinator(new SyntheticFastSeekProbeClient(), new ManualClock(), logger);
        var first = TestSources.Create();
        var target = TimeSpan.FromSeconds(50).Ticks;
        var duration = TimeSpan.FromSeconds(100).Ticks;
        var options = new PluginConfiguration();
        Assert.IsTrue(await coordinator.PrepareAsync(first, "media", target, duration, 0, options, CancellationToken.None));
        Assert.IsTrue(coordinator.TryBindInput(first, "media", target, duration, 0, "http://127.0.0.1/job", options));
        coordinator.InvalidateSource(new Uri("https://unrelated.invalid/media"));
        Assert.IsTrue(coordinator.TryGetBoundPlan("http://127.0.0.1/job", 0, target, CancellationToken.None, out _));
        coordinator.InvalidateSource(first.SourceUri);
        Assert.IsFalse(coordinator.TryGetBoundPlan("http://127.0.0.1/job", 0, target, CancellationToken.None, out _));
    }

    private static byte[] ClockPackets(int pid)
    {
        var bytes = TransportStreamTestData.Create(188, 0, 5, pid, 27_000_000L);
        for (var i = 0; i < bytes.Length; i += 188) bytes[i + 5] &= 0xbf;
        return bytes;
    }

    private static byte[] Pat(bool multi) => WithCrc(multi
        ? new byte[] { 0, 0, 0xb0, 17, 0, 1, 0xc1, 0, 0, 0, 1, 0xe0, 100, 0, 2, 0xe0, 101, 0, 0, 0, 0 }
        : new byte[] { 0, 0, 0xb0, 13, 0, 1, 0xc1, 0, 0, 0, 1, 0xe0, 100, 0, 0, 0, 0 });

    private static byte[] Pmt(int pid, int program) => WithCrc(new byte[]
    {
        0, 2, 0xb0, 18, 0, (byte)program, 0xc1, 0, 0, (byte)(0xe0 | (pid >> 8)), (byte)pid,
        0xf0, 0, 0x1b, (byte)(0xe0 | (pid >> 8)), (byte)pid, 0xf0, 0, 0, 0, 0, 0,
    });

    private static byte[] WithCrc(byte[] payload)
    {
        uint crc = uint.MaxValue;
        for (var i = 1; i < payload.Length - 4; i++)
        {
            crc ^= (uint)payload[i] << 24;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
        }
        for (var i = 0; i < 4; i++) payload[payload.Length - 4 + i] = (byte)(crc >> (24 - 8 * i));
        return payload;
    }

    private static byte[] Packet(byte[] payload, int pid, bool pusi, int cc)
    {
        var packet = Enumerable.Repeat((byte)0xff, 188).ToArray();
        packet[0] = 0x47;
        packet[1] = (byte)((pid >> 8) | (pusi ? 0x40 : 0));
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | cc);
        payload.CopyTo(packet, 4);
        return packet;
    }

    private sealed class VbrProbe : IFastSeekProbeClient
    {
        private const long MiB = 1024 * 1024;
        private const long Length = 100 * MiB;
        private static readonly long Anchor = 48 * MiB / 192 * 192;

        public Task<FastSeekProbeResult> ReadAsync(SourceIdentity source, long offset, int maximumBytes,
            PluginConfiguration options, CancellationToken cancellationToken)
        {
            var count = (int)(Math.Min(maximumBytes, Length - offset) / 192);
            var bytes = TransportStreamTestData.Create(192, offset, count, 256, position =>
            {
                var x = position / (double)MiB;
                var seconds = x <= 46 ? x : x <= 48 ? 46 + (x - 46) * 2.5 : 51 + (x - 48) * 49 / 52;
                return (long)(seconds * 27_000_000);
            });
            for (var i = 0; i < count; i++)
                if (offset + i * 192 != Anchor) bytes[i * 192 + 9] &= 0xbf;
            return Task.FromResult(new FastSeekProbeResult(offset, Length, bytes, new FastSeekRepresentation("\"synthetic\"", Length)));
        }
    }
}
