using System.Collections.Concurrent;
using System.Diagnostics;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class FastSeekDynamicBudgetTests
{
    private const int MiB = 1024 * 1024;
    private const int PacketStride = 192;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan Target = TimeSpan.FromSeconds(50);

    [TestMethod]
    [DataRow(4, 512 * 1024)]
    [DataRow(100, 4 * MiB)]
    [DataRow(800, 24 * MiB)]
    public async Task Coordinator_ExhaustsOnlyTheBitrateSizedCandidateWindow(
        int totalMiB,
        int expectedWindowBytes)
    {
        var client = new MeasuredProbeClient(totalMiB * (long)MiB) { AnchorByteOffset = -1 };
        var coordinator = CreateCoordinator(client);

        Assert.IsFalse(await PrepareAsync(coordinator));

        var requests = client.Requests.ToArray();
        Assert.AreEqual(FastSeekCoordinator.InitialProbeBytes, requests[0].MaximumBytes);
        var targetRequests = requests.Skip(1).ToArray();
        Assert.IsTrue(targetRequests.Length > 0);
        Assert.IsTrue(targetRequests.All(request =>
            request.MaximumBytes <= FastSeekCoordinator.MaximumProbeBytes &&
            request.MaximumBytes % PacketStride == 0));
        var scannedBytes = targetRequests.Sum(request => request.MaximumBytes);
        var alignedWindow = AlignDown(expectedWindowBytes);
        Assert.IsTrue(scannedBytes <= alignedWindow);
        Assert.IsTrue(scannedBytes > alignedWindow - PacketStride * 5,
            "Only a final fragment too short for transport framing may remain unread.");
        for (var index = 1; index < targetRequests.Length; index++)
            Assert.AreEqual(targetRequests[index - 1].Offset + targetRequests[index - 1].MaximumBytes,
                targetRequests[index].Offset, "Chunks must cover one consecutive candidate window.");
        Assert.AreEqual(0, coordinator.Count);
        Assert.AreEqual(0, client.ActiveReads);
    }

    [TestMethod]
    [DataRow(100, 4 * MiB, 6d)]
    [DataRow(800, 24 * MiB, 7d)]
    public async Task Coordinator_VbrCorrectionsRespectTheBitrateAndGlobalByteBudgets(
        int totalMiB,
        int windowBytes,
        double curvePower)
    {
        var client = new MeasuredProbeClient(totalMiB * (long)MiB)
        {
            SecondsAtNormalizedOffset = fraction => Math.Pow(fraction, curvePower) * Duration.TotalSeconds,
        };
        var coordinator = CreateCoordinator(client);
        var target = TimeSpan.FromSeconds(90);

        Assert.IsTrue(await PrepareAsync(coordinator, target));

        var requests = client.Requests.ToArray();
        Assert.IsTrue(requests.Length >= 4, "The fixture must exercise multiple corrections.");
        var expectedBudget = Math.Min(FastSeekCoordinator.MaximumPreparationBytes,
            FastSeekCoordinator.InitialProbeBytes + 4 * AlignDown(windowBytes));
        Assert.IsTrue(client.ReturnedBytes <= expectedBudget,
            "The header and all corrected windows share the same byte budget.");
        if (totalMiB == 800)
        {
            Assert.AreEqual(5, requests.Length);
            Assert.IsTrue(client.ReturnedBytes >= expectedBudget - PacketStride,
                "The last correction must be truncated at the shared 32 MiB cap.");
        }
        Assert.IsTrue(requests.All(request => request.MaximumBytes <= FastSeekCoordinator.MaximumProbeBytes));
        var plan = BindPlan(coordinator, target);
        var anchorSeconds = Math.Pow(plan.ByteOffset / (double)client.TotalLength, curvePower) * Duration.TotalSeconds;
        Assert.AreEqual(target.TotalSeconds, anchorSeconds + plan.RelativeSeek.TotalSeconds, 0.1);
    }

    [TestMethod]
    public async Task Coordinator_SlowMeasuredBodyShrinksTheNextRangeAndStillFindsAnAnchor()
    {
        var client = new MeasuredProbeClient(100L * MiB)
        {
            SetupDuration = TimeSpan.FromMilliseconds(20),
            BodyDuration = TimeSpan.FromSeconds(2),
            AnchorByteOffset = (long)(100L * MiB * 0.46) + 256 * 1024,
        };
        var coordinator = CreateCoordinator(client);

        Assert.IsTrue(await PrepareAsync(coordinator));

        var targetRequest = client.Requests.ToArray()[1];
        Assert.IsTrue(targetRequest.MaximumBytes < AlignDown(4 * MiB),
            "Body throughput must reduce a range that is unlikely to fit the remaining deadline.");
        Assert.IsTrue(targetRequest.MaximumBytes >= AlignDown(FastSeekCoordinator.InitialProbeBytes));
        Assert.AreEqual(0, targetRequest.MaximumBytes % PacketStride);
        var plan = BindPlan(coordinator, Target);
        Assert.IsTrue(Math.Abs(plan.ByteOffset - client.AnchorByteOffset!.Value) < PacketStride);
        Assert.AreEqual(Target.TotalSeconds,
            plan.ByteOffset / (double)MiB + plan.RelativeSeek.TotalSeconds, 0.1);
    }

    [TestMethod]
    public async Task Coordinator_SetupLatencyDoesNotBecomeBodyTransferTime()
    {
        var client = new MeasuredProbeClient(100L * MiB)
        {
            SetupDuration = TimeSpan.FromSeconds(2),
            BodyDuration = TimeSpan.FromMilliseconds(1),
        };
        var coordinator = CreateCoordinator(client);

        Assert.IsTrue(await PrepareAsync(coordinator));

        Assert.AreEqual(AlignDown(4 * MiB), client.Requests.ToArray()[1].MaximumBytes,
            "A slow header followed by a fast body must not be classified as a slow body link.");
        Assert.AreEqual(2, BindPlan(coordinator, Target).ProbeCount);
    }

    [TestMethod]
    public async Task Coordinator_MissingTimingRetainsTheExistingRangeSize()
    {
        var client = new MeasuredProbeClient(100L * MiB);
        var coordinator = CreateCoordinator(client);

        Assert.IsTrue(await PrepareAsync(coordinator));

        Assert.AreEqual(2, client.Requests.Count);
        Assert.AreEqual(AlignDown(4 * MiB), client.Requests.ToArray()[1].MaximumBytes);
        Assert.AreEqual(4, BindPlan(coordinator, Target).RelativeSeek.TotalSeconds, 0.01);
    }

    [TestMethod]
    public async Task Coordinator_NetworkSizedBoundaryMissDoesNotNegativelyCacheTheTarget()
    {
        const long totalLength = 100L * MiB;
        var estimatedOffset = (long)(totalLength * 0.46) / PacketStride * PacketStride;
        var anchorOffset = estimatedOffset + AlignDown(FastSeekCoordinator.InitialProbeBytes) - PacketStride;
        var client = new MeasuredProbeClient(totalLength)
        {
            SetupDuration = TimeSpan.FromMilliseconds(1),
            BodyDuration = TimeSpan.FromMinutes(2),
            AnchorByteOffset = anchorOffset,
            AnchorWithoutPcr = true,
        };
        var coordinator = CreateCoordinator(client);

        Assert.IsFalse(await PrepareAsync(coordinator),
            "An anchor at a chunk's last packet has no following PCR in the same sample.");
        var requestsAfterMiss = client.Requests.Count;
        Assert.IsTrue(requestsAfterMiss > 2);
        Assert.AreEqual(anchorOffset + PacketStride,
            client.Requests.ToArray()[1].Offset + client.Requests.ToArray()[1].MaximumBytes);
        Assert.AreEqual(0, coordinator.Count);

        client.BodyDuration = TimeSpan.FromMilliseconds(1);
        Assert.IsTrue(await PrepareAsync(coordinator),
            "The same target must be retried when a faster sample can contain both anchor and PCR.");
        Assert.AreEqual(requestsAfterMiss + 2, client.Requests.Count);
        Assert.AreEqual(AlignDown(4 * MiB), client.Requests.ToArray()[^1].MaximumBytes);
        Assert.AreEqual(anchorOffset, BindPlan(coordinator, Target).ByteOffset);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Coordinator_VerySlowEstimateStillAttemptsTheMinimumUntilCompletionOrClear(bool clear)
    {
        var targetStarted = NewSignal();
        var releaseTarget = NewSignal();
        var client = new MeasuredProbeClient(100L * MiB)
        {
            SetupDuration = TimeSpan.FromMilliseconds(1),
            BodyDuration = TimeSpan.FromMinutes(2),
            BeforeRead = async (request, token) =>
            {
                if (request.Call == 1) return;
                targetStarted.TrySetResult(true);
                await releaseTarget.Task.WaitAsync(token);
            },
        };
        var coordinator = CreateCoordinator(client);
        var preparation = PrepareAsync(coordinator);
        try
        {
            await targetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(AlignDown(FastSeekCoordinator.InitialProbeBytes),
                client.Requests.ToArray()[1].MaximumBytes);
            Assert.IsFalse(preparation.IsCompleted,
                "A bandwidth prediction cannot replace the actual cancellation or completion signal.");
            Assert.AreEqual(1, client.ActiveReads);

            if (clear) coordinator.Clear();
            else releaseTarget.TrySetResult(true);

            Assert.AreEqual(!clear, await preparation.WaitAsync(TimeSpan.FromSeconds(2)));
            await WaitUntilAsync(() => client.ActiveReads == 0);
            Assert.AreEqual(clear ? 0 : 1, coordinator.Count);
        }
        finally
        {
            coordinator.Clear();
            releaseTarget.TrySetResult(true);
        }
    }

    [TestMethod]
    public async Task Coordinator_AbsoluteDeadlineCleansUpAndRejectsAnIgnoredCancellationLateResult()
    {
        var waitingRead = NewSignal();
        var cancellationObserved = NewSignal();
        var cooperative = new MeasuredProbeClient(800L * MiB)
        {
            AnchorByteOffset = -1,
            BeforeRead = async (request, token) =>
            {
                if (request.Call <= 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5), token);
                    return;
                }
                waitingRead.TrySetResult(true);
                using var registration = token.Register(() => cancellationObserved.TrySetResult(true));
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        var lateCancellationObserved = NewSignal();
        var releaseLateRead = NewSignal();
        var lateClient = new MeasuredProbeClient(100L * MiB)
        {
            BeforeRead = async (request, token) =>
            {
                if (request.Call == 1) return;
                using var registration = token.Register(() => lateCancellationObserved.TrySetResult(true));
                await releaseLateRead.Task;
            },
        };
        var coordinator = CreateCoordinator(cooperative);
        var lateFinished = NewSignal();
        var lateCoordinator = CreateCoordinator(lateClient, message =>
        {
            if (releaseLateRead.Task.IsCompleted &&
                message.StartsWith("STRM_BRIDGE_FAST_SEEK_SKIPPED", StringComparison.Ordinal))
                lateFinished.TrySetResult(true);
        });
        var elapsed = Stopwatch.StartNew();
        var preparation = PrepareAsync(coordinator);
        var latePreparation = PrepareAsync(lateCoordinator);
        try
        {
            await waitingRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(preparation.IsCompleted);
            Assert.IsFalse(latePreparation.IsCompleted);
            await Task.WhenAll(cancellationObserved.Task, lateCancellationObserved.Task)
                .WaitAsync(TimeSpan.FromSeconds(8));
            Assert.IsTrue(elapsed.Elapsed >= FastSeekCoordinator.PreparationTimeout - TimeSpan.FromMilliseconds(300));
            Assert.IsTrue(elapsed.Elapsed < FastSeekCoordinator.PreparationTimeout + TimeSpan.FromSeconds(2),
                "Successful earlier ranges must not restart the seven-second deadline.");
            Assert.IsFalse(await preparation.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(await latePreparation.WaitAsync(TimeSpan.FromSeconds(2)),
                "An uncooperative probe must not keep the preparation caller waiting past cancellation.");
            await WaitUntilAsync(() => cooperative.ActiveReads == 0);
            Assert.AreEqual(0, coordinator.Count);
            Assert.AreEqual(0, lateCoordinator.Count);

            releaseLateRead.TrySetResult(true);
            await WaitUntilAsync(() => lateClient.ActiveReads == 0);
            // The caller has already returned; wait for the background attempt to discard its result.
            await lateFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(lateCoordinator.TryBindInput(TestSources.Create(), "dynamic-budget", Target.Ticks,
                Duration.Ticks, 0, "http://127.0.0.1/late", new PluginConfiguration()));
            Assert.AreEqual(0, lateCoordinator.Count);

            Assert.IsTrue(await PrepareAsync(lateCoordinator),
                "A cancelled attempt must not poison the exact target with a negative cache entry.");
            Assert.AreEqual(4, lateClient.Requests.Count,
                "A fresh attempt must obtain both header and target evidence again.");
        }
        finally
        {
            coordinator.Clear();
            lateCoordinator.Clear();
            releaseLateRead.TrySetResult(true);
        }
    }

    private static FastSeekCoordinator CreateCoordinator(IFastSeekProbeClient client, Action<string>? observeLog = null) =>
        new(client, new ManualClock(), TestProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name == nameof(ILogger.Debug) && arguments?[0] is string message)
                observeLog?.Invoke(message);
            return null;
        }));

    private static Task<bool> PrepareAsync(FastSeekCoordinator coordinator, TimeSpan? target = null) =>
        coordinator.PrepareAsync(TestSources.Create(), "dynamic-budget", (target ?? Target).Ticks,
            Duration.Ticks, 0, new PluginConfiguration(), CancellationToken.None);

    private static FastSeekPlan BindPlan(FastSeekCoordinator coordinator, TimeSpan target)
    {
        const string input = "http://127.0.0.1/dynamic-budget";
        Assert.IsTrue(coordinator.TryBindInput(TestSources.Create(), "dynamic-budget", target.Ticks,
            Duration.Ticks, 0, input, new PluginConfiguration()));
        Assert.IsTrue(coordinator.TryGetBoundPlan(input, 0, target.Ticks, CancellationToken.None, out var plan));
        return plan!;
    }

    private static int AlignDown(int bytes) => bytes / PacketStride * PacketStride;

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private readonly record struct ProbeRequest(int Call, long Offset, int MaximumBytes);

    private sealed class MeasuredProbeClient : IFastSeekProbeClient
    {
        private int calls;
        private int activeReads;
        private long returnedBytes;

        public MeasuredProbeClient(long totalLength) => TotalLength = totalLength;

        public long TotalLength { get; }
        public long? AnchorByteOffset { get; init; }
        public bool AnchorWithoutPcr { get; init; }
        public TimeSpan? SetupDuration { get; init; }
        public TimeSpan? BodyDuration { get; set; }
        public Func<double, double>? SecondsAtNormalizedOffset { get; init; }
        public Func<ProbeRequest, CancellationToken, Task>? BeforeRead { get; init; }
        public ConcurrentQueue<ProbeRequest> Requests { get; } = new();
        public int ActiveReads => Volatile.Read(ref activeReads);
        public long ReturnedBytes => Interlocked.Read(ref returnedBytes);

        public async Task<FastSeekProbeResult> ReadAsync(SourceIdentity source, long offset, int maximumBytes,
            PluginConfiguration options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ProbeRequest(Interlocked.Increment(ref calls), offset, maximumBytes);
            Requests.Enqueue(request);
            Interlocked.Increment(ref activeReads);
            try
            {
                if (BeforeRead is not null) await BeforeRead(request, cancellationToken);
                var packetCount = (int)Math.Min(maximumBytes, TotalLength - offset) / PacketStride;
                var bytes = TransportStreamTestData.Create(PacketStride, offset, packetCount, 256,
                    packetOffset => (long)Math.Round(
                        (SecondsAtNormalizedOffset?.Invoke(packetOffset / (double)TotalLength) ??
                         packetOffset / (double)TotalLength * Duration.TotalSeconds) *
                        TransportStreamClockParser.ClockFrequency));
                if (offset > 0 && AnchorByteOffset.HasValue)
                {
                    for (var packet = 0; packet < packetCount; packet++)
                        bytes[packet * PacketStride + 4 + 5] &= 0xbf;
                    var relativeAnchor = AnchorByteOffset.Value - offset;
                    if (relativeAnchor >= 0 && relativeAnchor / PacketStride < packetCount)
                    {
                        var flagsOffset = (int)(relativeAnchor / PacketStride) * PacketStride + 4 + 5;
                        bytes[flagsOffset] |= 0x40;
                        if (AnchorWithoutPcr) bytes[flagsOffset] &= 0xef;
                    }
                }
                Interlocked.Add(ref returnedBytes, bytes.Length);
                return new FastSeekProbeResult(offset, TotalLength, bytes,
                    new FastSeekRepresentation("\"dynamic-synthetic\"", TotalLength), SetupDuration, BodyDuration);
            }
            finally
            {
                Interlocked.Decrement(ref activeReads);
            }
        }
    }
}
