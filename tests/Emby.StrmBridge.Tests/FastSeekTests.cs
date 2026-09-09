using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Model.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class TransportStreamClockParserTests
{
    [TestMethod]
    [DataRow(188)]
    [DataRow(192)]
    [DataRow(204)]
    public void Parser_RecognizesPacketStrideAndPcr(int packetStride)
    {
        var bytes = TransportStreamTestData.Create(packetStride, 0, 64, 256, 4 * 27_000_000L);

        Assert.IsTrue(TransportStreamClockParser.TryAnalyze(bytes, 0, null, out var analysis));
        Assert.AreEqual(packetStride, analysis!.Format.PacketStride);
        Assert.AreEqual(packetStride == 192 ? 4 : 0, analysis.Format.SyncOffset);
        Assert.AreEqual(256, analysis.SelectClockPid());
        Assert.IsTrue(analysis.TryGetFirstPcr(256, out var pcr));
        Assert.IsTrue(analysis.TryGetFirstRandomAccess(256, out var randomAccess));
        Assert.AreEqual(4 * 27_000_000L, pcr.Clock27Mhz);
        Assert.AreEqual(0, pcr.PacketOffset);
        Assert.AreEqual(0, randomAccess.PacketOffset);
    }

    [TestMethod]
    public void Parser_SelectsThePidWithTheMostClockSamples()
    {
        var bytes = TransportStreamTestData.Create(
            188,
            0,
            30,
            packet => packet % 3 == 0 ? 300 : 400,
            packet => packet * 900_000L);

        Assert.IsTrue(TransportStreamClockParser.TryAnalyze(bytes, 0, null, out var analysis));
        Assert.AreEqual(400, analysis!.SelectClockPid());
    }

    [TestMethod]
    public void Parser_RejectsCorruptFramingAndHandlesClockWrap()
    {
        Assert.IsFalse(TransportStreamClockParser.TryAnalyze(
            new byte[4096], 0, null, out _));
        var beforeWrap = TransportStreamClockParser.ClockWrap - 27_000_000L;
        Assert.AreEqual(
            2d,
            TransportStreamClockParser.SecondsBetween(beforeWrap, 27_000_000L),
            0.000001d);
    }

    [TestMethod]
    public void Parser_RecognizesCodecRandomAccessUnits()
    {
        Assert.IsTrue(TransportStreamClockParser.ContainsCodecRandomAccess(
            new byte[] { 0, 0, 1, 0x65, 0, 0 }, 0x1b));
        Assert.IsTrue(TransportStreamClockParser.ContainsCodecRandomAccess(
            new byte[] { 0, 0, 0, 1, 0x20, 1 }, 0x24));
        Assert.IsTrue(TransportStreamClockParser.ContainsCodecRandomAccess(
            new byte[] { 0, 0, 1, 0, 0, 0x08 }, 0x02));
        Assert.IsFalse(TransportStreamClockParser.ContainsCodecRandomAccess(
            new byte[] { 0, 0, 1, 0x41, 0, 0 }, 0x1b));
    }

    [TestMethod]
    public void Parser_RecognizesCodecRandomAccessAcrossPacketBoundaries()
    {
        Assert.IsTrue(TransportStreamClockParser.ContainsCodecRandomAccessAcrossBoundary(
            new byte[] { 0xaa, 0, 0 },
            new byte[] { 1, 0x65, 0, 0 },
            0x1b));
        Assert.IsTrue(TransportStreamClockParser.ContainsCodecRandomAccessAcrossBoundary(
            new byte[] { 0, 0, 0, 1 },
            new byte[] { 0x20, 1 },
            0x24));
        Assert.IsFalse(TransportStreamClockParser.ContainsCodecRandomAccessAcrossBoundary(
            new byte[] { 0, 0 },
            new byte[] { 1, 0x41, 0, 0 },
            0x1b));
    }
}

[TestClass]
public sealed class FastSeekCoordinatorTests
{
    [TestMethod]
    [DataRow(4d)]
    [DataRow(6d)]
    public async Task Coordinator_ConvergesAcrossMultipleMeasuredVbrCorrections(double power)
    {
        var client = new SyntheticFastSeekProbeClient(x => Math.Pow(x, power) * 100);
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(90).Ticks;
        var duration = TimeSpan.FromSeconds(100).Ticks;
        const string input = "http://127.0.0.1/StrmBridge/Playback/v3/test/stream.ts";
        Assert.IsTrue(await coordinator.PrepareAsync(source, "source", target, duration, 0,
            new PluginConfiguration(), CancellationToken.None));
        Assert.IsTrue(client.Offsets.Count >= 4, "The sample must need more than one correction.");
        Assert.IsTrue(client.Offsets.Count <= 2 + FastSeekCoordinator.MaximumCorrections);
        Assert.AreEqual(client.Offsets.Count, client.Offsets.Distinct().Count());
        Assert.IsTrue(client.RequestedBytes.Sum() <= FastSeekCoordinator.MaximumPreparationBytes);
        Assert.IsTrue(coordinator.TryBindInput(source, "source", target, duration, 0, input, new PluginConfiguration()));
        Assert.IsTrue(coordinator.TryGetBoundPlan(input, 0, target, CancellationToken.None, out var plan));
        var actualAnchorSeconds = Math.Pow(plan!.ByteOffset / (100d * 1024 * 1024), power) * 100;
        Assert.AreEqual(90d, actualAnchorSeconds + plan.RelativeSeek.TotalSeconds, 0.1);
        var count = client.Offsets.Count;
        Assert.IsTrue(await coordinator.PrepareAsync(source, "source", target, duration, 0,
            new PluginConfiguration(), CancellationToken.None));
        Assert.AreEqual(count, client.Offsets.Count);
    }

    [TestMethod]
    public async Task Coordinator_ExtremeVbrStillRespectsCorrectionAndByteBudgets()
    {
        var client = new SyntheticFastSeekProbeClient(x => Math.Pow(x, 10) * 100);
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        await coordinator.PrepareAsync(TestSources.Create(), "source", TimeSpan.FromSeconds(90).Ticks,
            TimeSpan.FromSeconds(100).Ticks, 0, new PluginConfiguration(), CancellationToken.None);
        Assert.IsTrue(client.Offsets.Count <= 2 + FastSeekCoordinator.MaximumCorrections);
        Assert.IsTrue(client.RequestedBytes.Sum() <= FastSeekCoordinator.MaximumPreparationBytes);
    }

    [TestMethod]
    public async Task Coordinator_UsesTwoSamplesWhenTheEstimatedPreRollIsAlreadyBounded()
    {
        var clock = new ManualClock();
        var client = new SyntheticFastSeekProbeClient();
        var coordinator = new FastSeekCoordinator(client, clock, CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(50);

        Assert.IsTrue(await coordinator.PrepareAsync(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtimeGeneration: 7,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.AreEqual(2, client.Offsets.Count);
        Assert.IsTrue(coordinator.TryBindInput(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtimeGeneration: 7,
            "http://127.0.0.1:8096/StrmBridge/Playback/v3/ticket/stream.m2ts",
            new PluginConfiguration()));
        Assert.IsTrue(coordinator.TryGetBoundPlan(
            "http://127.0.0.1:8096/StrmBridge/Playback/v3/ticket/stream.m2ts",
            runtimeGeneration: 7,
            target.Ticks,
            CancellationToken.None,
            out var plan));
        Assert.AreEqual(192, plan!.PacketStride);
        Assert.AreEqual(2, plan.ProbeCount);
        Assert.AreEqual(4000d, plan.RelativeSeek.TotalMilliseconds, 5d);
        Assert.IsTrue(plan.ByteOffset > 0);
        Assert.AreEqual(0, plan.ByteOffset % 192);
    }

    [TestMethod]
    public async Task Coordinator_ScalesTheTargetWindowForAHighBitrateTransportStream()
    {
        var client = new HighBitrateFastSeekProbeClient();
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(50);
        var duration = TimeSpan.FromSeconds(100);
        const string inputUrl = "http://127.0.0.1:8096/StrmBridge/Playback/v3/high-bitrate/stream.m2ts";

        Assert.IsTrue(await coordinator.PrepareAsync(
            source,
            "source-one",
            target.Ticks,
            duration.Ticks,
            runtimeGeneration: 7,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.HasCount(2, client.RequestedBytes);
        Assert.AreEqual(FastSeekCoordinator.InitialProbeBytes, client.RequestedBytes[0]);
        Assert.IsGreaterThan(512 * 1024, client.RequestedBytes[1]);
        Assert.IsLessThanOrEqualTo(FastSeekCoordinator.MaximumProbeBytes, client.RequestedBytes[1]);
        Assert.IsLessThanOrEqualTo(
            FastSeekCoordinator.MaximumPreparationBytes,
            client.RequestedBytes.Sum());
        Assert.IsTrue(coordinator.TryBindInput(
            source, "source-one", target.Ticks, duration.Ticks, 7, inputUrl,
            new PluginConfiguration()));
        Assert.IsTrue(coordinator.TryGetBoundPlan(
            inputUrl, 7, target.Ticks, CancellationToken.None, out var plan));
        Assert.IsGreaterThan(2d, plan!.RelativeSeek.TotalSeconds);
        Assert.IsLessThan(4d, plan.RelativeSeek.TotalSeconds);
    }

    [TestMethod]
    public async Task Coordinator_NegativelyCachesDeterministicPreparationFailure()
    {
        var clock = new ManualClock();
        var invalid = new SyntheticFastSeekProbeClient(invalidData: true);
        var coordinator = new FastSeekCoordinator(invalid, clock, CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(50);
        var duration = TimeSpan.FromSeconds(100);

        Assert.IsFalse(await coordinator.PrepareAsync(
            source, "source-one", target.Ticks, duration.Ticks, 7,
            new PluginConfiguration(), CancellationToken.None));
        var probesAfterFirstFailure = invalid.Offsets.Count;
        Assert.IsGreaterThan(0, probesAfterFirstFailure);

        Assert.IsFalse(await coordinator.PrepareAsync(
            source, "source-one", target.Ticks, duration.Ticks, 7,
            new PluginConfiguration(), CancellationToken.None));
        Assert.AreEqual(probesAfterFirstFailure, invalid.Offsets.Count);

        Assert.IsFalse(await coordinator.PrepareAsync(
            source, "source-one", target.Add(TimeSpan.FromSeconds(1)).Ticks, duration.Ticks, 7,
            new PluginConfiguration(), CancellationToken.None));
        Assert.IsGreaterThan(probesAfterFirstFailure, invalid.Offsets.Count);
        var probesAfterDifferentTarget = invalid.Offsets.Count;

        clock.Advance(FastSeekCoordinator.FailureLifetime + TimeSpan.FromMilliseconds(1));
        Assert.IsFalse(await coordinator.PrepareAsync(
            source, "source-one", target.Ticks, duration.Ticks, 7,
            new PluginConfiguration(), CancellationToken.None));
        Assert.IsGreaterThan(probesAfterDifferentTarget, invalid.Offsets.Count);
    }

    [TestMethod]
    public async Task Coordinator_NegativelyCachesAHighBitrateRandomAccessMiss()
    {
        var client = new HighBitrateFastSeekProbeClient(includeRandomAccess: false);
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(50);
        var duration = TimeSpan.FromSeconds(100);

        Assert.IsFalse(await coordinator.PrepareAsync(
            source, "source-one", target.Ticks, duration.Ticks, 7,
            new PluginConfiguration(), CancellationToken.None));
        var requestsAfterFirstFailure = client.RequestedBytes.Count;
        Assert.IsGreaterThan(1, requestsAfterFirstFailure);
        Assert.IsLessThanOrEqualTo(
            FastSeekCoordinator.MaximumPreparationBytes,
            client.RequestedBytes.Sum());

        Assert.IsFalse(await coordinator.PrepareAsync(
            source, "source-one", target.Ticks, duration.Ticks, 7,
            new PluginConfiguration(), CancellationToken.None));
        Assert.AreEqual(requestsAfterFirstFailure, client.RequestedBytes.Count);
    }

    [TestMethod]
    public async Task Coordinator_BoundLookupNeverPreparesAChangedSeekTarget()
    {
        var client = new SyntheticFastSeekProbeClient();
        var coordinator = new FastSeekCoordinator(
            client,
            new ManualClock(),
            CreateLogger());
        var source = TestSources.Create();
        var initialTarget = TimeSpan.FromSeconds(70);
        var changedTarget = TimeSpan.FromSeconds(40);
        const string inputUrl = "http://127.0.0.1:8096/StrmBridge/Playback/v3/ticket/stream.m2ts";

        Assert.IsTrue(await coordinator.PrepareAsync(
            source,
            "source-one",
            initialTarget.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtimeGeneration: 7,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.IsTrue(coordinator.TryBindInput(
            source,
            "source-one",
            initialTarget.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtimeGeneration: 7,
            inputUrl,
            new PluginConfiguration()));
        Assert.IsFalse(coordinator.TryGetBoundPlan(
            inputUrl,
            runtimeGeneration: 7,
            changedTarget.Ticks,
            CancellationToken.None,
            out var changed));
        Assert.IsNull(changed);
        Assert.AreEqual(2, client.Offsets.Count);
    }

    [TestMethod]
    public async Task Coordinator_UsesTheTargetProbeToCorrectVariableBitrateDrift()
    {
        var client = new SyntheticFastSeekProbeClient(
            normalizedOffset => normalizedOffset <= 0.5d
                ? normalizedOffset * 80d
                : 40d + (normalizedOffset - 0.5d) * 120d);
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(70);
        const string inputUrl = "http://127.0.0.1:8096/StrmBridge/Playback/v3/ticket/stream.m2ts";

        Assert.IsTrue(await coordinator.PrepareAsync(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtimeGeneration: 7,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.IsTrue(coordinator.TryBindInput(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtimeGeneration: 7,
            inputUrl,
            new PluginConfiguration()));
        Assert.IsTrue(coordinator.TryGetBoundPlan(
            inputUrl,
            runtimeGeneration: 7,
            target.Ticks,
            CancellationToken.None,
            out var changed));

        Assert.AreEqual(3, client.Offsets.Count);
        Assert.IsGreaterThan(1000d, changed!.RelativeSeek.TotalMilliseconds);
        Assert.IsLessThan(6000d, changed.RelativeSeek.TotalMilliseconds);
    }

    [TestMethod]
    public async Task Coordinator_UsesOneBoundedRetryWhenTheFirstTargetCorrectionIsOutsideTheWindow()
    {
        var client = new SyntheticFastSeekProbeClient(normalizedOffset =>
            normalizedOffset <= 0.26d
                ? normalizedOffset * (10d / 0.26d)
                : normalizedOffset <= 0.42d
                    ? 10d + (normalizedOffset - 0.26d) * 100d
                    : 26d + (normalizedOffset - 0.42d) * (74d / 0.58d));
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(90);
        const string inputUrl = "http://127.0.0.1:8096/StrmBridge/Playback/v3/retry/stream.m2ts";

        Assert.IsTrue(await coordinator.PrepareAsync(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtimeGeneration: 7,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.IsTrue(coordinator.TryBindInput(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtimeGeneration: 7,
            inputUrl,
            new PluginConfiguration()));

        Assert.IsTrue(coordinator.TryGetBoundPlan(
            inputUrl,
            runtimeGeneration: 7,
            target.Ticks,
            CancellationToken.None,
            out var changed));

        Assert.AreEqual(3, client.Offsets.Count);
        Assert.AreEqual(3, changed!.ProbeCount);
        Assert.IsGreaterThan(1000d, changed.RelativeSeek.TotalMilliseconds);
        Assert.IsLessThan(6000d, changed.RelativeSeek.TotalMilliseconds);
    }

    [TestMethod]
    public async Task Coordinator_SharesAValidatedTargetPlanAcrossPlaybackBindings()
    {
        var client = new SyntheticFastSeekProbeClient();
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(40);
        const string firstInput = "http://127.0.0.1:8096/StrmBridge/Playback/v3/first/stream.m2ts";
        const string secondInput = "http://127.0.0.1:8096/StrmBridge/Playback/v3/second/stream.m2ts";

        Assert.IsTrue(await coordinator.PrepareAsync(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtimeGeneration: 7,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.IsTrue(coordinator.TryBindInput(
            source, "source-one", target.Ticks, TimeSpan.FromSeconds(100).Ticks, 7, firstInput,
            new PluginConfiguration()));
        Assert.IsTrue(coordinator.TryGetBoundPlan(
            firstInput, 7, target.Ticks, CancellationToken.None, out var first));
        Assert.AreEqual(2, client.Offsets.Count);

        Assert.IsTrue(await coordinator.PrepareAsync(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtimeGeneration: 7,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.IsTrue(coordinator.TryBindInput(
            source, "source-one", target.Ticks, TimeSpan.FromSeconds(100).Ticks, 7, secondInput,
            new PluginConfiguration()));
        Assert.IsTrue(coordinator.TryGetBoundPlan(
            secondInput, 7, target.Ticks, CancellationToken.None, out var second));

        Assert.AreSame(first, second);
        Assert.AreEqual(2, client.Offsets.Count);
    }

    [TestMethod]
    public async Task Coordinator_OneCancelledWaiterDoesNotCancelSharedPreparation()
    {
        var client = new GatedFastSeekProbeClient();
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(50);
        using var firstCancellation = new CancellationTokenSource();
        var first = coordinator.PrepareAsync(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            7,
            new PluginConfiguration(),
            firstCancellation.Token);
        await client.Started.Task;
        var second = coordinator.PrepareAsync(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            7,
            new PluginConfiguration(),
            CancellationToken.None);

        firstCancellation.Cancel();
        client.Release.TrySetResult(true);

        Assert.IsFalse(await first);
        Assert.IsTrue(await second);
        Assert.AreEqual(2, client.ProbeCount);
    }

    [TestMethod]
    public async Task Coordinator_LastCancelledWaiterCancelsAndDetachesPendingPreparation()
    {
        var client = new FirstReadCancellationProbeClient();
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(50);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var abandoned = coordinator.PrepareAsync(
                source,
                "source-one",
                target.Ticks,
                TimeSpan.FromSeconds(100).Ticks,
                7,
                new PluginConfiguration(),
                cancellation.Token);
            await client.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            cancellation.Cancel();

            Assert.IsFalse(await abandoned.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsTrue(await coordinator.PrepareAsync(
                    source,
                    "source-one",
                    target.Ticks,
                    TimeSpan.FromSeconds(100).Ticks,
                    7,
                    new PluginConfiguration(),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)),
                "A new caller must not attach to the abandoned pending entry.");
            await client.FirstReadCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(3, client.ProbeCount);
        }
        finally
        {
            cancellation.Cancel();
            coordinator.Clear();
        }
    }

    [TestMethod]
    public async Task Coordinator_BoundsPendingWorkAndClearPreventsLatePlans()
    {
        var client = new GatedFastSeekProbeClient();
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        var source = TestSources.Create();
        var options = new PluginConfiguration();
        var preparations = Enumerable.Range(0, FastSeekCoordinator.MaximumPendingPreparations)
            .Select(index => coordinator.PrepareAsync(
                source,
                "source-one",
                TimeSpan.FromSeconds(10 + index).Ticks,
                TimeSpan.FromSeconds(200).Ticks,
                7,
                options,
                CancellationToken.None))
            .ToArray();

        await client.Started.Task;
        Assert.IsFalse(await coordinator.PrepareAsync(
            source,
            "source-one",
            TimeSpan.FromSeconds(100).Ticks,
            TimeSpan.FromSeconds(200).Ticks,
            7,
            options,
            CancellationToken.None));

        coordinator.Clear();
        client.Release.TrySetResult(true);

        Assert.IsTrue((await Task.WhenAll(preparations)).All(result => !result));
        Assert.AreEqual(0, coordinator.Count);
    }

    [TestMethod]
    public async Task Coordinator_DoesNotBindAPlanPreparedForADifferentDuration()
    {
        var client = new SyntheticFastSeekProbeClient();
        var coordinator = new FastSeekCoordinator(client, new ManualClock(), CreateLogger());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(50);

        Assert.IsTrue(await coordinator.PrepareAsync(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            7,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.IsFalse(coordinator.TryBindInput(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(120).Ticks,
            7,
            "http://127.0.0.1/different-duration",
            new PluginConfiguration()));
    }

    [TestMethod]
    public async Task Coordinator_ExpiresPlansAndRejectsInvalidTransportData()
    {
        var clock = new ManualClock();
        var invalid = new SyntheticFastSeekProbeClient(invalidData: true);
        var coordinator = new FastSeekCoordinator(invalid, clock, CreateLogger());
        var source = TestSources.Create();
        Assert.IsFalse(await coordinator.PrepareAsync(
            source,
            "source",
            TimeSpan.FromSeconds(50).Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            1,
            new PluginConfiguration(),
            CancellationToken.None));

        var valid = new FastSeekCoordinator(new SyntheticFastSeekProbeClient(), clock, CreateLogger());
        Assert.IsTrue(await valid.PrepareAsync(
            source,
            "source",
            TimeSpan.FromSeconds(50).Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            1,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.IsTrue(valid.TryBindInput(
            source,
            "source",
            TimeSpan.FromSeconds(50).Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            1,
            "http://127.0.0.1/expired",
            new PluginConfiguration()));
        clock.Advance(FastSeekCoordinator.PlanLifetime + TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(2, valid.RemoveExpired());
        Assert.IsFalse(valid.TryBindInput(
            source,
            "source",
            TimeSpan.FromSeconds(50).Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            1,
            "http://127.0.0.1/input",
            new PluginConfiguration()));
    }

    [TestMethod]
    public async Task Coordinator_CancelledPreparationFallsBackWithoutAPlan()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var coordinator = new FastSeekCoordinator(
            new CancelledFastSeekProbeClient(),
            new ManualClock(),
            CreateLogger());

        Assert.IsFalse(await coordinator.PrepareAsync(
            TestSources.Create(),
            "source",
            TimeSpan.FromSeconds(50).Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            1,
            new PluginConfiguration(),
            cancellation.Token));
        Assert.AreEqual(0, coordinator.Count);
    }

    internal static ILogger CreateLogger() =>
        TestProxy.Create<ILogger>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
}

[TestClass]
public sealed class GatewayFastSeekProbeClientTests
{
    [TestMethod]
    public async Task ProbeClient_AcceptsAnExactBoundedPartialResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var body = TransportStreamTestData.Create(188, 188, 8, 256, 27_000_000L);
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServePartialAsync(listener, observed, 188, 188 + body.Length - 1, 20_000, body);
        using var transport = new GatewayTransport(new Emby.StrmBridge.Policy.RedirectPolicy());
        var client = new GatewayFastSeekProbeClient(transport);
        var source = new Emby.StrmBridge.Domain.SourceIdentity(
            new string('a', 64),
            new string('b', 64),
            new Uri($"http://127.0.0.1:{port}/media"),
            "/library/media.strm",
            1,
            DateTimeOffset.UtcNow);

        var result = await client.ReadAsync(
            source,
            188,
            body.Length,
            new PluginConfiguration { GatewayTimeoutSeconds = 10, RelayConcurrency = 2 },
            CancellationToken.None);

        await server;
        StringAssert.Contains(await observed.Task, "Range: bytes=188-" + (188 + body.Length - 1));
        Assert.AreEqual(188, result.RangeStart);
        Assert.AreEqual(20_000, result.TotalLength);
        CollectionAssert.AreEqual(body, result.Bytes);
    }

    private static async Task ServePartialAsync(
        TcpListener listener,
        TaskCompletionSource<string> observed,
        long from,
        long to,
        long total,
        byte[] body)
    {
        using var connection = await listener.AcceptTcpClientAsync();
        await using var stream = connection.GetStream();
        var buffer = new byte[8192];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count));
            if (read == 0) break;
            count += read;
            if (Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }
        observed.TrySetResult(Encoding.ASCII.GetString(buffer, 0, count));
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 206 Partial Content\r\n" +
            "Content-Type: video/mp2t\r\n" +
            $"Content-Range: bytes {from}-{to}/{total}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
    }
}

[TestClass]
public sealed class FfmpegCommandProcessorTests
{
    [TestMethod]
    public async Task Processor_AppliesOffsetAndSequentialTrimAtomically()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);

        Assert.IsTrue(fixture.Processor.TryApply(command));

        Assert.IsTrue(command.Input0.InputProtocol.offset > 0);
        Assert.AreEqual(false, command.Input0.InputProtocol.seekable);
        Assert.IsNull(command.Input0.Options.ss);
        Assert.AreEqual(4000d, command.Output0.Options.ss!.Value.TotalMilliseconds, 5d);
        Assert.AreEqual(fixture.Target, command.Output0.Options.output_ts_offset);
        Assert.AreEqual(
            -fixture.Target + FfmpegCommandProcessor.MaximumFirstSegmentAdvance,
            command.Output0.Muxer.segment_time_delta);
    }

    [TestMethod]
    public async Task Processor_DisabledFastSeekLeavesAnExistingBoundCommandNative()
    {
        using var fixture = await CreateFixtureAsync();
        var options = fixture.Runtime.GetOptionsSnapshot();
        options.EnableFastSeek = false;
        fixture.Runtime.UpdateOptions(options, invalidateSensitiveState: false);
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);

        Assert.IsFalse(fixture.Processor.TryApply(command));

        Assert.IsNull(command.Input0.InputProtocol.offset);
        Assert.AreEqual(fixture.Target, command.Input0.Options.ss);
        Assert.IsNull(command.Output0.Options.ss);
    }

    [TestMethod]
    public async Task Processor_KeepsUnknownOrNonSegmentCommandsNative()
    {
        using var fixture = await CreateFixtureAsync();
        var unknown = TestFfmpegCommand.Create("http://127.0.0.1/other", fixture.Target);
        Assert.IsFalse(fixture.Processor.TryApply(unknown));
        Assert.IsNull(unknown.Input0.InputProtocol.offset);

        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        command.Output0.Muxer.Key = "hls";
        Assert.IsFalse(fixture.Processor.TryApply(command));
        Assert.IsNull(command.Input0.InputProtocol.offset);
    }

    [TestMethod]
    public async Task Processor_KeepsAnUnpreparedHlsTargetNative()
    {
        using var fixture = await CreateFixtureAsync();
        var changedTarget = TimeSpan.FromSeconds(30);
        var command = TestFfmpegCommand.Create(fixture.InputUrl, changedTarget);

        Assert.IsFalse(fixture.Processor.TryApply(command));

        Assert.IsNull(command.Input0.InputProtocol.offset);
        Assert.IsNull(command.Input0.InputProtocol.seekable);
        Assert.AreEqual(changedTarget, command.Input0.Options.ss);
        Assert.IsNull(command.Output0.Options.ss);
        Assert.IsNull(command.Output0.Options.output_ts_offset);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Processor_ChecksTheMappedVideoForDirectAndFilteredCommands(bool filtered, bool changed)
    {
        var probe = new MultiVideoFastSeekProbeClient(192);
        var selection = FastSeekVideoSelectionTests.Selection(1);
        using var fixture = await CreateFixtureAsync(probe, selection);
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        var video = new TestMappedInput { Stream = new TestMappedStream { Index = changed ? 0 : 3 } };
        if (filtered)
            command.Connections = new object[] { new { StreamSource = video } };
        else
            command.Output0.Video0 = new { InputStream = video };
        Assert.AreEqual(!changed, fixture.Processor.TryApply(command));
        Assert.AreEqual(!changed, fixture.Processor.TryApply(TestFfmpegCommandWithSameMap()));
        Assert.AreEqual(2, probe.ReadCount);
        if (changed)
        {
            Assert.IsNull(command.Input0.InputProtocol.offset);
            Assert.AreEqual(fixture.Target, command.Input0.Options.ss);
        }

        TestFfmpegCommand TestFfmpegCommandWithSameMap()
        {
            var retry = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
            retry.Connections = command.Connections;
            retry.Output0.Video0 = command.Output0.Video0;
            return retry;
        }
    }

    [TestMethod]
    public async Task Processor_CommandHookDoesNotStartTargetPreparation()
    {
        using var workspace = new TestWorkspace();
        using var runtime = new PluginRuntime();
        var logger = FastSeekCoordinatorTests.CreateLogger();
        var probeClient = new GateAfterInitialCalibrationProbeClient();
        runtime.Initialize(workspace.Path, new ManualClock());
        runtime.InitializeFastSeek(logger, probeClient);
        var source = TestSources.Create();
        var duration = TimeSpan.FromSeconds(100);
        var initialTarget = TimeSpan.FromSeconds(50);
        const string inputUrl = "http://127.0.0.1:8096/StrmBridge/Playback/v3/no-io/stream.m2ts";
        Assert.IsTrue(await runtime.FastSeek!.PrepareAsync(
            source,
            "source-one",
            initialTarget.Ticks,
            duration.Ticks,
            runtime.Generation,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.IsTrue(runtime.FastSeek.TryBindInput(
            source,
            "source-one",
            initialTarget.Ticks,
            duration.Ticks,
            runtime.Generation,
            inputUrl,
            new PluginConfiguration()));
        var processor = new FfmpegCommandProcessor(runtime, logger);
        var changedTarget = TimeSpan.FromSeconds(30);
        var command = TestFfmpegCommand.Create(inputUrl, changedTarget);
        Assert.IsFalse(processor.TryApply(command, CancellationToken.None));
        Assert.AreEqual(changedTarget, command.Input0.Options.ss);
        Assert.IsNull(command.Input0.InputProtocol.offset);
        Assert.IsFalse(probeClient.TargetProbeStarted.Task.IsCompleted);
    }

    [TestMethod]
    public async Task Processor_DoesNotReuseAPlanForDifferentTargets()
    {
        using var fixture = await CreateFixtureAsync();
        var firstTarget = TimeSpan.FromSeconds(30);
        var secondTarget = TimeSpan.FromSeconds(80);
        var first = TestFfmpegCommand.Create(fixture.InputUrl, firstTarget);
        var second = TestFfmpegCommand.Create(fixture.InputUrl, secondTarget);

        Assert.IsFalse(fixture.Processor.TryApply(first));
        Assert.IsFalse(fixture.Processor.TryApply(second));

        Assert.AreEqual(firstTarget, first.Input0.Options.ss);
        Assert.AreEqual(secondTarget, second.Input0.Options.ss);
        Assert.IsNull(first.Input0.InputProtocol.offset);
        Assert.IsNull(second.Input0.InputProtocol.offset);
    }

    [TestMethod]
    public async Task Processor_BoundsTheFirstSegmentAdvanceToHalfTheSegmentDuration()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        command.Output0.Muxer.segment_time = TimeSpan.FromSeconds(4);

        Assert.IsTrue(fixture.Processor.TryApply(command));

        Assert.AreEqual(
            -fixture.Target + TimeSpan.FromSeconds(2),
            command.Output0.Muxer.segment_time_delta);
    }

    [TestMethod]
    public async Task Processor_KeepsUnknownTimelineContractNative()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        command.Output0.Muxer.segment_time_delta = null;

        Assert.IsFalse(fixture.Processor.TryApply(command));

        Assert.IsNull(command.Input0.InputProtocol.offset);
        Assert.AreEqual(fixture.Target, command.Input0.Options.ss);
        Assert.IsNull(command.Output0.Options.output_ts_offset);
    }

    [TestMethod]
    public async Task Processor_AppliesAnIndexedTranscodeTimelineWithoutANativeDelta()
    {
        using var fixture = await CreateFixtureAsync();
        var target = fixture.Target;
        var segmentTime = TimeSpan.FromSeconds(2);
        var command = TestFfmpegCommand.CreateIndexed(fixture.InputUrl, target, segmentTime);

        Assert.IsTrue(fixture.Processor.TryApply(command));

        Assert.IsTrue(command.Input0.InputProtocol.offset > 0);
        Assert.AreEqual(false, command.Input0.InputProtocol.seekable);
        Assert.IsNull(command.Input0.Options.ss);
        Assert.AreEqual(target, command.Output0.Options.output_ts_offset);
        Assert.AreEqual(
            -target + TimeSpan.FromTicks(segmentTime.Ticks / 2),
            command.Output0.Muxer.segment_time_delta);
    }

    [TestMethod]
    public async Task Processor_RejectsAnIndexedTimelineThatDoesNotProveTheAbsoluteTarget()
    {
        using var fixture = await CreateFixtureAsync();
        var target = TimeSpan.FromSeconds(48);
        var command = TestFfmpegCommand.CreateIndexed(
            fixture.InputUrl,
            target,
            TimeSpan.FromSeconds(3));
        command.Output0.Muxer.segment_start_number++;

        Assert.IsFalse(fixture.Processor.TryApply(command));

        Assert.IsNull(command.Input0.InputProtocol.offset);
        Assert.AreEqual(target, command.Input0.Options.ss);
        Assert.IsNull(command.Output0.Muxer.segment_time_delta);
    }

    [TestMethod]
    public async Task Processor_RejectsAnIndexedTimelineWithDifferentTimestampSemantics()
    {
        using var fixture = await CreateFixtureAsync();
        var target = TimeSpan.FromSeconds(48);
        var segmentTime = TimeSpan.FromSeconds(3);
        var commands = new[]
        {
            TestFfmpegCommand.CreateIndexed(fixture.InputUrl, target, segmentTime),
            TestFfmpegCommand.CreateIndexed(fixture.InputUrl, target, segmentTime),
            TestFfmpegCommand.CreateIndexed(fixture.InputUrl, target, segmentTime),
            TestFfmpegCommand.CreateIndexed(fixture.InputUrl, target, segmentTime),
            TestFfmpegCommand.CreateIndexed(fixture.InputUrl, target, segmentTime),
        };
        commands[0].Options.copyts = false;
        commands[1].Options.start_at_zero = false;
        commands[2].Output0.Options.avoid_negative_ts = "auto";
        commands[3].Input0.Options.itsoffset = TimeSpan.FromSeconds(1);
        commands[4].Output0.Muxer.reset_timestamps = true;

        foreach (var command in commands)
        {
            Assert.IsFalse(fixture.Processor.TryApply(command));
            Assert.IsNull(command.Input0.InputProtocol.offset);
            Assert.AreEqual(target, command.Input0.Options.ss);
            Assert.IsNull(command.Output0.Muxer.segment_time_delta);
        }
    }

    [TestMethod]
    public async Task Processor_DoesNotOverrideAProtocolThatWasAlreadyCustomized()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        command.Input0.InputProtocol.offset = 188;

        Assert.IsFalse(fixture.Processor.TryApply(command));

        Assert.AreEqual(188L, command.Input0.InputProtocol.offset);
        Assert.IsNull(command.Input0.InputProtocol.seekable);
        Assert.AreEqual(fixture.Target, command.Input0.Options.ss);
    }

    [TestMethod]
    public async Task Processor_RestoresEveryFieldWhenASetterFails()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        command.Output0.Options.ThrowOnTimestampOffset = true;

        Assert.IsFalse(fixture.Processor.TryApply(command));

        Assert.IsNull(command.Input0.InputProtocol.offset);
        Assert.IsNull(command.Input0.InputProtocol.seekable);
        Assert.AreEqual(fixture.Target, command.Input0.Options.ss);
        Assert.IsNull(command.Output0.Options.ss);
        Assert.IsNull(command.Output0.Options.output_ts_offset);
        Assert.AreEqual(-fixture.Target, command.Output0.Muxer.segment_time_delta);
    }

    [TestMethod]
    public async Task Processor_RestoresEveryFieldWhenTheMuxerMutationFails()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        command.Output0.Muxer.ThrowOnDelta = true;

        Assert.IsFalse(fixture.Processor.TryApply(command));

        Assert.IsNull(command.Input0.InputProtocol.offset);
        Assert.IsNull(command.Input0.InputProtocol.seekable);
        Assert.AreEqual(fixture.Target, command.Input0.Options.ss);
        Assert.IsNull(command.Output0.Options.ss);
        Assert.IsNull(command.Output0.Options.output_ts_offset);
        Assert.AreEqual(-fixture.Target, command.Output0.Muxer.segment_time_delta);
    }

    [TestMethod]
    public async Task Processor_RestoresANullDeltaWhenAnIndexedMutationFails()
    {
        using var fixture = await CreateFixtureAsync();
        var target = TimeSpan.FromSeconds(48);
        var command = TestFfmpegCommand.CreateIndexed(
            fixture.InputUrl,
            target,
            TimeSpan.FromSeconds(3));
        command.Output0.Muxer.ThrowOnDelta = true;

        Assert.IsFalse(fixture.Processor.TryApply(command));

        Assert.IsNull(command.Input0.InputProtocol.offset);
        Assert.IsNull(command.Input0.InputProtocol.seekable);
        Assert.AreEqual(target, command.Input0.Options.ss);
        Assert.IsNull(command.Output0.Options.ss);
        Assert.IsNull(command.Output0.Options.output_ts_offset);
        Assert.IsNull(command.Output0.Muxer.segment_time_delta);
    }

    [TestMethod]
    [DataRow("user_agent")]
    [DataRow("headers")]
    [DataRow("cookies")]
    [DataRow("referer")]
    [DataRow("method")]
    public async Task Processor_PreservesExistingRequestProfiles(string property)
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        typeof(TestHttpProtocol).GetProperty(property)!.SetValue(command.Input0.InputProtocol, "custom-value");
        Assert.IsFalse(fixture.Processor.TryApply(command));
        Assert.AreEqual(fixture.Target, command.Input0.Options.ss);
        Assert.IsNull(command.Input0.InputProtocol.offset);
        Assert.AreEqual("custom-value", typeof(TestHttpProtocol).GetProperty(property)!.GetValue(command.Input0.InputProtocol));
    }

    [TestMethod]
    public async Task Processor_BindsStrongRepresentationToActiveTicketBeyondPlanExpiry()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        Assert.IsTrue(fixture.Processor.TryApply(command));
        Assert.AreEqual(GatewayTransport.ProbeUserAgent, command.Input0.InputProtocol.user_agent);
        StringAssert.Contains(command.Input0.InputProtocol.headers!, "If-Match: \"synthetic\"\r\n");
        StringAssert.Contains(command.Input0.InputProtocol.headers!, "Accept-Encoding: identity\r\n");
        var value = fixture.InputUrl.Split('/')[^2];
        Assert.IsTrue(fixture.Runtime.Tickets.TryInspect(value, out var ticket));
        ((ManualClock)fixture.Runtime.Clock).Advance(FastSeekCoordinator.PlanLifetime + TimeSpan.FromSeconds(1));
        Assert.IsFalse(fixture.Runtime.FastSeek!.TryGetBoundPlan(fixture.InputUrl, fixture.Runtime.Generation,
            fixture.Target.Ticks, CancellationToken.None, out _));
        Assert.IsTrue(fixture.Runtime.FastSeek.TryGetInputRepresentation(ticket!, fixture.Runtime.Generation, out var expected));
        Assert.AreEqual("\"synthetic\"", expected!.StrongETag);
    }

    [TestMethod]
    public async Task Processor_FailedOptimizedStartRestoresAllFieldsAndRetriesNativeOnce()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        Assert.IsTrue(fixture.Processor.TryApply(command, CancellationToken.None, out var mutation));
        var nativeStarts = 0;
        var failed = Task.FromResult(false);
        Assert.IsTrue(await mutation!.ObserveStartAsync(failed, retryToken =>
        {
            nativeStarts++;
            Assert.AreEqual(fixture.Target, command.Input0.Options.ss);
            Assert.IsNull(command.Input0.InputProtocol.offset);
            Assert.IsNull(command.Input0.InputProtocol.seekable);
            Assert.IsNull(command.Input0.InputProtocol.user_agent);
            Assert.IsNull(command.Input0.InputProtocol.headers);
            Assert.IsNull(command.Output0.Options.ss);
            Assert.IsNull(command.Output0.Options.output_ts_offset);
            Assert.AreEqual(-fixture.Target, command.Output0.Muxer.segment_time_delta);
            Assert.IsFalse(fixture.Processor.TryApply(command));
            Assert.IsTrue(fixture.Runtime.Tickets.TryInspect(fixture.InputUrl.Split('/')[^2], out var ticket));
            Assert.IsFalse(fixture.Runtime.FastSeek!.TryGetInputRepresentation(ticket!, fixture.Runtime.Generation, out _));
            return Task.FromResult(true);
        }, CancellationToken.None));
        Assert.AreEqual(1, nativeStarts);
    }

    [TestMethod]
    public async Task Processor_ThrownStartLeavesCleanupAndRunningCommandToTheHost()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        Assert.IsTrue(fixture.Processor.TryApply(command, CancellationToken.None, out var mutation));
        var starts = 0;
        await Assert.ThrowsAsync<IOException>(() => mutation!.ObserveStartAsync(
            Task.FromException<bool>(new IOException()), retryToken =>
            {
                starts++;
                return Task.FromResult(true);
            }, CancellationToken.None));
        Assert.AreEqual(0, starts);
        Assert.IsTrue(command.Input0.InputProtocol.offset > 0);
        Assert.IsNull(command.Input0.Options.ss);
        Assert.AreEqual(GatewayTransport.ProbeUserAgent, command.Input0.InputProtocol.user_agent);
        Assert.IsNotNull(command.Input0.InputProtocol.headers);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Processor_DoesNotRetryCancelledOrRevokedJobs(bool generationChanged)
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        using var cancellation = new CancellationTokenSource();
        Assert.IsTrue(fixture.Processor.TryApply(command, cancellation.Token, out var mutation));
        if (generationChanged) fixture.Runtime.ClearSensitiveState();
        else cancellation.Cancel();
        var starts = 0;
        Assert.IsFalse(await mutation!.ObserveStartAsync(Task.FromResult(false), retryToken =>
        {
            starts++;
            return Task.FromResult(true);
        }, cancellation.Token));
        Assert.AreEqual(0, starts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Processor_DoesNotStartNativeWhenCancellationOrRevocationOccursDuringRestore(bool generationChanged)
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        using var cancellation = new CancellationTokenSource();
        Assert.IsTrue(fixture.Processor.TryApply(command, cancellation.Token, out var mutation));
        command.Input0.InputProtocol.OnOffsetReset = () =>
        {
            if (generationChanged) fixture.Runtime.ClearSensitiveState();
            else cancellation.Cancel();
        };
        var starts = 0;
        Assert.IsFalse(await mutation!.ObserveStartAsync(Task.FromResult(false), retryToken =>
        {
            starts++;
            return Task.FromResult(true);
        }, cancellation.Token));
        Assert.AreEqual(0, starts);
    }

    [TestMethod]
    public async Task Processor_CommittedNativeRetryReceivesRuntimeCancellation()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        Assert.IsTrue(fixture.Processor.TryApply(command, CancellationToken.None, out var mutation));
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = mutation!.ObserveStartAsync(Task.FromResult(false), token =>
        {
            started.SetResult(token);
            return released.Task;
        }, CancellationToken.None);
        var nativeToken = await started.Task;
        Assert.IsFalse(nativeToken.IsCancellationRequested);
        fixture.Runtime.ClearSensitiveState();
        Assert.IsTrue(nativeToken.IsCancellationRequested);
        released.SetResult(false);
        Assert.IsFalse(await result);
    }

    [TestMethod]
    public async Task HarmonyCommandHook_RetriesOneNativeStartWithoutReapplyingThePlan()
    {
        using var fixture = await CreateFixtureAsync();
        var command = TestFfmpegCommand.Create(fixture.InputUrl, fixture.Target);
        var runner = new TestFfmpegRunner();
        var harmony = new Harmony("StrmBridge.Tests.FfmpegFallback." + Guid.NewGuid().ToString("N"));
        FfmpegCommandPatchBridge.Attach(fixture.Processor);
        try
        {
            harmony.Patch(typeof(TestFfmpegRunner).GetMethod(nameof(TestFfmpegRunner.Start))!,
                prefix: new HarmonyMethod(typeof(FfmpegCommandPatchBridge).GetMethod(nameof(FfmpegCommandPatchBridge.Prefix))!),
                postfix: new HarmonyMethod(typeof(FfmpegCommandPatchBridge).GetMethod(nameof(FfmpegCommandPatchBridge.Postfix))!));
            Assert.IsFalse(await runner.Start(command, CancellationToken.None));
            Assert.AreEqual(2, runner.Starts);
            Assert.IsTrue(runner.ObservedOptimizedFirst);
            Assert.IsTrue(runner.ObservedNativeSecond);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            FfmpegCommandPatchBridge.Detach(fixture.Processor);
        }
    }

    private sealed class TestFfmpegRunner
    {
        public int Starts { get; private set; }
        public bool ObservedOptimizedFirst { get; private set; }
        public bool ObservedNativeSecond { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task<bool> Start(TestFfmpegCommand command, CancellationToken cancellationToken)
        {
            Starts++;
            if (Starts == 1) ObservedOptimizedFirst = command.Input0.InputProtocol.offset > 0 && command.Input0.Options.ss is null;
            if (Starts == 2) ObservedNativeSecond = command.Input0.InputProtocol.offset is null && command.Input0.Options.ss.HasValue;
            return Task.FromResult(false);
        }
    }

    private static async Task<CommandFixture> CreateFixtureAsync(
        IFastSeekProbeClient? probe = null, FastSeekVideoSelection? selection = null)
    {
        var workspace = new TestWorkspace();
        var runtime = new PluginRuntime();
        var logger = FastSeekCoordinatorTests.CreateLogger();
        runtime.Initialize(workspace.Path, new ManualClock());
        runtime.InitializeFastSeek(logger, probe ?? new SyntheticFastSeekProbeClient());
        var source = TestSources.Create();
        var target = TimeSpan.FromSeconds(50);
        await runtime.FastSeek!.PrepareAsync(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtime.Generation,
            new PluginConfiguration(),
            CancellationToken.None,
            selection);
        var ticket = runtime.Tickets.IssuePlayback(Guid.NewGuid(), "source-one", null, source,
            Emby.StrmBridge.Domain.PlaybackTicketPurpose.ServerFfmpeg, runtime.Generation);
        var inputUrl = "http://127.0.0.1:8096/StrmBridge/Playback/v3/" + ticket + "/stream.m2ts";
        Assert.IsTrue(runtime.FastSeek.TryBindInput(
            source,
            "source-one",
            target.Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtime.Generation,
            inputUrl,
            new PluginConfiguration(),
            selection,
            ticket));
        return new CommandFixture(
            workspace,
            runtime,
            new FfmpegCommandProcessor(runtime, logger),
            inputUrl,
            target);
    }

    private sealed class CommandFixture : IDisposable
    {
        public CommandFixture(
            TestWorkspace workspace,
            PluginRuntime runtime,
            FfmpegCommandProcessor processor,
            string inputUrl,
            TimeSpan target)
        {
            Workspace = workspace;
            Runtime = runtime;
            Processor = processor;
            InputUrl = inputUrl;
            Target = target;
        }

        private TestWorkspace Workspace { get; }
        public PluginRuntime Runtime { get; }
        public FfmpegCommandProcessor Processor { get; }
        public string InputUrl { get; }
        public TimeSpan Target { get; }

        public void Dispose()
        {
            Runtime.Dispose();
            Workspace.Dispose();
        }
    }

    private sealed class TestFfmpegCommand
    {
        public object[] Connections { get; set; } = Array.Empty<object>();
        public TestGlobalOptions Options { get; set; } = new();
        public TestInput Input0 { get; set; } = new();
        public TestOutput Output0 { get; set; } = new();

        public static TestFfmpegCommand Create(string inputUrl, TimeSpan target) => new()
        {
            Input0 = new TestInput
            {
                Url = inputUrl,
                InputProtocol = new TestHttpProtocol(),
                Options = new TestInputOptions { ss = target },
            },
            Output0 = new TestOutput
            {
                Options = new TestOutputOptions(),
                Muxer = new TestSegmentMuxer { segment_time_delta = -target },
            },
        };

        public static TestFfmpegCommand CreateIndexed(
            string inputUrl,
            TimeSpan target,
            TimeSpan segmentTime)
        {
            if (target.Ticks % segmentTime.Ticks != 0)
                throw new ArgumentException("The indexed target must align to the segment duration.");
            var command = Create(inputUrl, target);
            command.Options.copyts = true;
            command.Options.start_at_zero = true;
            command.Output0.Options.avoid_negative_ts = "disabled";
            command.Output0.Muxer.segment_time = segmentTime;
            command.Output0.Muxer.segment_time_delta = null;
            command.Output0.Muxer.segment_start_number = checked((int)(target.Ticks / segmentTime.Ticks));
            return command;
        }
    }

    private sealed class TestGlobalOptions
    {
        public bool copyts { get; set; }
        public bool start_at_zero { get; set; }
    }

    private sealed class TestInput
    {
        public string Url { get; set; } = string.Empty;
        public TestHttpProtocol InputProtocol { get; set; } = new();
        public TestInputOptions Options { get; set; } = new();
    }

    private sealed class TestOutput
    {
        public object? Video0 { get; set; }
        public TestOutputOptions Options { get; set; } = new();
        public TestSegmentMuxer Muxer { get; set; } = new();
    }

    private class TestMappedInputBase
    {
        public object? Stream { get; set; }
        public int InputIndex => 0;
        public string MediaKind => "Video";
    }

    private sealed class TestMappedInput : TestMappedInputBase
    {
        public new TestMappedStream Stream { get; set; } = new();
    }

    private sealed class TestMappedStream
    {
        public int Index { get; set; }
    }

    private sealed class TestHttpProtocol
    {
        public string? user_agent { get; set; }
        public string? headers { get; set; }
        public string? cookies { get; set; }
        public string? referer { get; set; }
        public string? method { get; set; }
        public byte[]? post_data { get; set; }
        public string Key { get; set; } = "http";
        private long? initialOffset;
        public Action? OnOffsetReset { get; set; }
        public long? offset
        {
            get => initialOffset;
            set
            {
                if (value is null && initialOffset.HasValue) OnOffsetReset?.Invoke();
                initialOffset = value;
            }
        }
        public bool? seekable { get; set; }
    }

    private sealed class TestInputOptions
    {
        public TimeSpan? ss { get; set; }
        public TimeSpan? itsoffset { get; set; }
        public TimeSpan? sseof { get; set; }
        public long? skip_initial_bytes { get; set; }
        public bool seek_timestamp { get; set; }
    }

    private sealed class TestOutputOptions
    {
        private TimeSpan? timestampOffset;
        public TimeSpan? ss { get; set; }
        public string? avoid_negative_ts { get; set; }

        public bool ThrowOnTimestampOffset { get; set; }

        public TimeSpan? output_ts_offset
        {
            get => timestampOffset;
            set
            {
                if (ThrowOnTimestampOffset && value != timestampOffset) throw new InvalidOperationException();
                timestampOffset = value;
            }
        }
    }

    private sealed class TestSegmentMuxer
    {
        private TimeSpan? value;
        public string Key { get; set; } = "segment";
        public TimeSpan? initial_offset { get; set; }
        public bool? reset_timestamps { get; set; }
        public int? segment_start_number { get; set; }
        public TimeSpan? segment_time { get; set; } = TimeSpan.FromSeconds(6);
        public bool ThrowOnDelta { get; set; }
        public TimeSpan? segment_time_delta
        {
            get => value;
            set
            {
                if (ThrowOnDelta && value != this.value) throw new InvalidOperationException();
                this.value = value;
            }
        }
    }
}

internal sealed class SyntheticFastSeekProbeClient : IFastSeekProbeClient
{
    private const long TotalLength = 100L * 1024 * 1024;
    private readonly bool invalidData;
    private readonly Func<double, double> secondsAtNormalizedOffset;

    public SyntheticFastSeekProbeClient(bool invalidData = false)
        : this(normalizedOffset => normalizedOffset * 100d, invalidData)
    {
    }

    public SyntheticFastSeekProbeClient(
        Func<double, double> secondsAtNormalizedOffset,
        bool invalidData = false)
    {
        this.secondsAtNormalizedOffset = secondsAtNormalizedOffset ??
            throw new ArgumentNullException(nameof(secondsAtNormalizedOffset));
        this.invalidData = invalidData;
    }

    public List<long> Offsets { get; } = new();
    public List<int> RequestedBytes { get; } = new();

    public Task<FastSeekProbeResult> ReadAsync(
        Emby.StrmBridge.Domain.SourceIdentity source,
        long offset,
        int maximumBytes,
        PluginConfiguration options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Offsets.Add(offset);
        RequestedBytes.Add(maximumBytes);
        var length = (int)Math.Min(maximumBytes, TotalLength - offset);
        var bytes = invalidData
            ? new byte[length]
            : TransportStreamTestData.Create(
                192,
                offset,
                length / 192,
                256,
                packetOffset => (long)Math.Round(
                    secondsAtNormalizedOffset(packetOffset / (double)TotalLength) * 27_000_000d));
        return Task.FromResult(new FastSeekProbeResult(offset, TotalLength, bytes, new FastSeekRepresentation("\"synthetic\"", TotalLength)));
    }
}

internal sealed class HighBitrateFastSeekProbeClient : IFastSeekProbeClient
{
    private const int PacketStride = 192;
    private const int SyncOffset = 4;
    private const long TotalLength = 600L * 1024 * 1024;
    private const long ByteRate = 6L * 1024 * 1024;
    private static readonly long RandomAccessOffset =
        (long)(TotalLength * 0.46d) + 6L * 1024 * 1024;
    private readonly bool includeRandomAccess;

    public HighBitrateFastSeekProbeClient(bool includeRandomAccess = true) =>
        this.includeRandomAccess = includeRandomAccess;

    public List<int> RequestedBytes { get; } = new();

    public Task<FastSeekProbeResult> ReadAsync(
        Emby.StrmBridge.Domain.SourceIdentity source,
        long offset,
        int maximumBytes,
        PluginConfiguration options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedBytes.Add(maximumBytes);
        var length = (int)Math.Min(maximumBytes, TotalLength - offset);
        var packetCount = length / PacketStride;
        var bytes = TransportStreamTestData.Create(
            PacketStride,
            offset,
            packetCount,
            256,
            packetOffset => (long)Math.Round(
                packetOffset / (double)ByteRate * TransportStreamClockParser.ClockFrequency));
        for (var packet = 0; packet < packetCount; packet++)
            bytes[packet * PacketStride + SyncOffset + 5] &= 0xbf;
        var anchorPacket = (RandomAccessOffset - offset) / PacketStride;
        if (includeRandomAccess && anchorPacket >= 0 && anchorPacket < packetCount)
            bytes[(int)anchorPacket * PacketStride + SyncOffset + 5] |= 0x40;
        return Task.FromResult(new FastSeekProbeResult(offset, TotalLength, bytes, new FastSeekRepresentation("\"synthetic\"", TotalLength)));
    }
}

internal sealed class CancelledFastSeekProbeClient : IFastSeekProbeClient
{
    public async Task<FastSeekProbeResult> ReadAsync(
        Emby.StrmBridge.Domain.SourceIdentity source,
        long offset,
        int maximumBytes,
        PluginConfiguration options,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException();
    }
}

internal sealed class GatedFastSeekProbeClient : IFastSeekProbeClient
{
    private readonly SyntheticFastSeekProbeClient inner = new();
    private int probeCount;

    public TaskCompletionSource<bool> Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ProbeCount => Volatile.Read(ref probeCount);

    public async Task<FastSeekProbeResult> ReadAsync(
        Emby.StrmBridge.Domain.SourceIdentity source,
        long offset,
        int maximumBytes,
        PluginConfiguration options,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref probeCount);
        Started.TrySetResult(true);
        await Release.Task.WaitAsync(cancellationToken);
        return await inner.ReadAsync(source, offset, maximumBytes, options, cancellationToken);
    }
}

internal sealed class FirstReadCancellationProbeClient : IFastSeekProbeClient
{
    private readonly SyntheticFastSeekProbeClient inner = new();
    private int probeCount;

    public TaskCompletionSource<bool> FirstReadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> FirstReadCancellationObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ProbeCount => Volatile.Read(ref probeCount);

    public async Task<FastSeekProbeResult> ReadAsync(
        Emby.StrmBridge.Domain.SourceIdentity source,
        long offset,
        int maximumBytes,
        PluginConfiguration options,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref probeCount) == 1)
        {
            FirstReadStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                    FirstReadCancellationObserved.TrySetResult(true);
            }
        }
        return await inner.ReadAsync(source, offset, maximumBytes, options, cancellationToken);
    }
}

internal sealed class GateAfterInitialCalibrationProbeClient : IFastSeekProbeClient
{
    private readonly SyntheticFastSeekProbeClient inner = new();
    private int probeCount;

    public TaskCompletionSource<bool> TargetProbeStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> ReleaseTargetProbe { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<FastSeekProbeResult> ReadAsync(
        Emby.StrmBridge.Domain.SourceIdentity source,
        long offset,
        int maximumBytes,
        PluginConfiguration options,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref probeCount) > 2)
        {
            TargetProbeStarted.TrySetResult(true);
            await ReleaseTargetProbe.Task.WaitAsync(cancellationToken);
        }
        return await inner.ReadAsync(source, offset, maximumBytes, options, cancellationToken);
    }
}

internal static class TransportStreamTestData
{
    public static byte[] Create(int stride, long rangeStart, int packetCount, int pid, long firstClock) =>
        Create(stride, rangeStart, packetCount, _ => pid, packet => firstClock + packet * 900_000L);

    public static byte[] Create(
        int stride,
        long rangeStart,
        int packetCount,
        int pid,
        Func<long, long> clockForPacketOffset) =>
        Create(stride, rangeStart, packetCount, _ => pid, packet =>
            clockForPacketOffset(rangeStart + packet * stride));

    public static byte[] Create(
        int stride,
        long rangeStart,
        int packetCount,
        Func<int, int> pidForPacket,
        Func<int, long> clockForPacket)
    {
        var bytes = Enumerable.Repeat((byte)0xff, packetCount * stride).ToArray();
        var prefix = stride == 192 ? 4 : 0;
        for (var packet = 0; packet < packetCount; packet++)
        {
            var sync = packet * stride + prefix;
            WritePacket(bytes.AsSpan(sync, 188), pidForPacket(packet), clockForPacket(packet));
        }
        return bytes;
    }

    private static void WritePacket(Span<byte> packet, int pid, long clock)
    {
        var normalized = clock % TransportStreamClockParser.ClockWrap;
        if (normalized < 0) normalized += TransportStreamClockParser.ClockWrap;
        var pcrBase = normalized / 300;
        var extension = normalized % 300;
        packet[0] = 0x47;
        packet[1] = (byte)((pid >> 8 & 0x1f) | 0x40);
        packet[2] = (byte)pid;
        packet[3] = 0x30;
        packet[4] = 7;
        packet[5] = 0x50;
        packet[6] = (byte)(pcrBase >> 25);
        packet[7] = (byte)(pcrBase >> 17);
        packet[8] = (byte)(pcrBase >> 9);
        packet[9] = (byte)(pcrBase >> 1);
        packet[10] = (byte)((pcrBase & 1) << 7 | extension >> 8 & 1);
        packet[11] = (byte)extension;
        new byte[] { 0, 0, 1, 0xe0, 0, 0, 0x80, 0, 0 }.CopyTo(packet.Slice(12));
    }
}
