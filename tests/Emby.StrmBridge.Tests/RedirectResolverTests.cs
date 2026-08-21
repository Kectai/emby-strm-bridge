using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class RedirectResolverTests
{
    [TestMethod]
    public async Task Resolve_CachesForThirtySecondsAndSeparatesUserAgents()
    {
        var clock = new ManualClock();
        var client = SuccessClient();
        using var resolver = new RedirectResolver(client, TrustedPolicy(), clock);

        var first = await resolver.ResolveAsync(TestSources.Create(), "Player/1", CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(29));
        var cached = await resolver.ResolveAsync(TestSources.Create(), "Player/1", CancellationToken.None);
        Assert.AreEqual(first.GetLocation(), cached.GetLocation());
        Assert.AreEqual(1, client.Calls);

        await resolver.ResolveAsync(TestSources.Create(), "Player/2", CancellationToken.None);
        Assert.AreEqual(2, client.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        await resolver.ResolveAsync(TestSources.Create(), "Player/1", CancellationToken.None);
        Assert.AreEqual(3, client.Calls);
    }

    [TestMethod]
    public async Task Resolve_CoalescesConcurrentRequestsIntoSingleFlight()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubRedirectClient(async (_, _, _, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return SuccessResponse();
        });
        using var resolver = new RedirectResolver(client, TrustedPolicy(), new ManualClock());

        var first = resolver.ResolveAsync(TestSources.Create(), "same-agent", CancellationToken.None);
        await entered.Task;
        var second = resolver.ResolveAsync(TestSources.Create(), "same-agent", CancellationToken.None);
        release.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, client.Calls);
    }

    [TestMethod]
    public async Task Resolve_SharesFailureBackoffAcrossUserAgents()
    {
        var clock = new ManualClock();
        var client = new StubRedirectClient((call, _, _, _) => Task.FromResult(
            call == 1 ? new RedirectSourceResponse(503, null, 10) : SuccessResponse()));
        using var resolver = new RedirectResolver(client, TrustedPolicy(), clock);

        var first = await Assert.ThrowsExactlyAsync<RedirectSourceUnavailableException>(() =>
            resolver.ResolveAsync(TestSources.Create(), "Agent/A", CancellationToken.None));
        var second = await Assert.ThrowsExactlyAsync<RedirectSourceUnavailableException>(() =>
            resolver.ResolveAsync(TestSources.Create(), "Agent/B", CancellationToken.None));
        Assert.AreEqual(10, first.RetryAfterSeconds);
        Assert.IsTrue(second.RetryAfterSeconds <= 10);
        Assert.AreEqual(1, client.Calls);

        clock.Advance(TimeSpan.FromSeconds(10));
        await resolver.ResolveAsync(TestSources.Create(), "Agent/B", CancellationToken.None);
        Assert.AreEqual(2, client.Calls);
    }

    [TestMethod]
    public async Task Resolve_EnforcesPerSourceBurstBudget()
    {
        var client = SuccessClient();
        using var resolver = new RedirectResolver(client, TrustedPolicy(), new ManualClock());
        for (var index = 0; index < 12; index++)
        {
            await resolver.ResolveAsync(TestSources.Create(), "Agent/" + index, CancellationToken.None);
        }
        await Assert.ThrowsExactlyAsync<RedirectThrottledException>(() =>
            resolver.ResolveAsync(TestSources.Create(), "Agent/overflow", CancellationToken.None));
        Assert.AreEqual(12, client.Calls);
    }

    [TestMethod]
    public async Task Clear_PreventsLateCompletionFromPublishingLease()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubRedirectClient(async (_, _, _, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return SuccessResponse();
        });
        using var resolver = new RedirectResolver(client, TrustedPolicy(), new ManualClock());
        var resolving = resolver.ResolveAsync(TestSources.Create(), "Agent/1", CancellationToken.None);
        await entered.Task;

        resolver.Clear();
        release.SetResult(true);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await resolving);
    }

    [TestMethod]
    public async Task Resolve_CancelsSourceRequestWhenLastWaiterCancels()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubRedirectClient(async (_, _, _, cancellationToken) =>
        {
            entered.TrySetResult(true);
            using var registration = cancellationToken.Register(() => sourceCanceled.TrySetResult(true));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SuccessResponse();
        });
        using var resolver = new RedirectResolver(client, TrustedPolicy(), new ManualClock());
        using var cancellation = new CancellationTokenSource();
        var resolving = resolver.ResolveAsync(TestSources.Create(), "Agent/1", cancellation.Token);
        await entered.Task;

        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await resolving);
        await sourceCanceled.Task;
    }

    [TestMethod]
    public async Task Resolve_AbandonedSourceThatIgnoresCancellationCannotPublishLease()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubRedirectClient(async (call, _, _, _) =>
        {
            if (call == 1)
            {
                entered.TrySetResult(true);
                await release.Task;
                oldReturned.TrySetResult(true);
                return new RedirectSourceResponse(302, "https://media.invalid/old", null);
            }
            return SuccessResponse();
        });
        using var resolver = new RedirectResolver(client, TrustedPolicy(), new ManualClock());
        using var cancellation = new CancellationTokenSource();
        var abandoned = resolver.ResolveAsync(TestSources.Create(), "Agent/1", cancellation.Token);
        await entered.Task;

        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await abandoned);
        release.TrySetResult(true);
        await oldReturned.Task;

        var replacement = await resolver.ResolveAsync(TestSources.Create(), "Agent/1", CancellationToken.None);
        Assert.AreEqual("https://media.invalid/video?signature=temporary", replacement.GetLocation());
        Assert.AreEqual(2, client.Calls);
    }

    [TestMethod]
    public async Task Probe_AcceptsDirectMediaButGatewayRequiresRedirect()
    {
        var client = new StubRedirectClient((_, _, _, _) =>
            Task.FromResult(new RedirectSourceResponse(206, null, null)));
        using var resolver = new RedirectResolver(client, TrustedPolicy(), new ManualClock());

        var lease = await resolver.ResolveForProbeAsync(
            TestSources.Create(),
            "Probe/1",
            CancellationToken.None);

        Assert.AreEqual("https://source.invalid/entry?opaque=source-value", lease.GetLocation());
        var rejected = await Assert.ThrowsExactlyAsync<RedirectRejectedException>(() =>
            resolver.ResolveAsync(TestSources.Create(), "Probe/1", CancellationToken.None));
        Assert.AreEqual(RedirectRejectionReason.UnexpectedStatus, rejected.Reason);
        Assert.AreEqual(2, client.Calls);
    }

    [TestMethod]
    public async Task Resolve_RejectsUnsupportedUserAgentWithoutContactingSource()
    {
        var client = SuccessClient();
        using var resolver = new RedirectResolver(client, TrustedPolicy(), new ManualClock());

        var rejected = await Assert.ThrowsExactlyAsync<RedirectRejectedException>(() =>
            resolver.ResolveAsync(TestSources.Create(), new string('x', 257), CancellationToken.None));

        Assert.AreEqual(RedirectRejectionReason.InvalidUserAgent, rejected.Reason);
        Assert.AreEqual(0, client.Calls);
    }

    [TestMethod]
    public async Task Resolve_EnforcesGlobalPendingCapacity()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubRedirectClient(async (_, _, _, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return SuccessResponse();
        });
        using var resolver = new RedirectResolver(client, TrustedPolicy(), new ManualClock());
        var tasks = Enumerable.Range(1, RedirectResolver.MaximumPendingResolutions)
            .Select(index => resolver.ResolveAsync(CreateUniqueSource(index), "Agent", CancellationToken.None))
            .ToArray();

        await Assert.ThrowsExactlyAsync<RedirectThrottledException>(() =>
            resolver.ResolveAsync(CreateUniqueSource(tasks.Length + 1), "Agent", CancellationToken.None));

        release.SetResult(true);
        await Task.WhenAll(tasks);
    }

    [TestMethod]
    public async Task Resolve_EnforcesGlobalSourceStateCapacity()
    {
        var client = new StubRedirectClient((_, _, _, _) =>
            Task.FromResult(new RedirectSourceResponse(206, null, null)));
        using var resolver = new RedirectResolver(client, TrustedPolicy(), new ManualClock());
        for (var index = 1; index <= RedirectResolver.MaximumSourceStates; index++)
        {
            await resolver.ResolveForProbeAsync(CreateUniqueSource(index), "Probe", CancellationToken.None);
        }

        await Assert.ThrowsExactlyAsync<RedirectThrottledException>(() =>
            resolver.ResolveForProbeAsync(
                CreateUniqueSource(RedirectResolver.MaximumSourceStates + 1),
                "Probe",
                CancellationToken.None));
        Assert.AreEqual(RedirectResolver.MaximumSourceStates, client.Calls);
    }

    private static StubRedirectClient SuccessClient() =>
        new((_, _, _, _) => Task.FromResult(SuccessResponse()));

    private static RedirectPolicy TrustedPolicy() => new(() => new[] { "media.invalid" });

    private static RedirectSourceResponse SuccessResponse() =>
        new(302, "https://media.invalid/video?signature=temporary", null);

    private static Emby.StrmBridge.Domain.SourceIdentity CreateUniqueSource(int index) => new(
        (index + 1000).ToString("x64"),
        index.ToString("x64"),
        new Uri("https://source.invalid/entry"),
        "/synthetic/item.strm",
        32,
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
}
