using Emby.StrmBridge.Playback;
using MediaBrowser.Model.Dto;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class TranscodeJobCoordinatorTests
{
    [TestMethod]
    public async Task FailedStartup_BoundsRetriesWithoutExtendingCooldown()
    {
        using var workspace = new TestWorkspace();
        var clock = new ManualClock();
        var jobs = Create(clock);
        var releases = 0;
        var attempt = jobs.Register(
            new MediaSourceInfo(), Output(workspace), "target", 1, clock.UtcNow, () => releases++);
        await Assert.ThrowsAsync<IOException>(() => jobs.ObserveStartAsync(Task.FromException<object>(new IOException()), attempt));
        Assert.AreEqual(1, releases);
        await jobs.CleanupAsync(attempt, 0, 0, _ => { }, _ => false, _ => Task.CompletedTask);
        Assert.AreEqual(1, releases);
        var blocked = Assert.Throws<TranscodeStartRejectedException>(() => jobs.CheckRetry("target", 1));
        Assert.AreEqual(30, blocked.RetryAfterSeconds);
        clock.Advance(TimeSpan.FromSeconds(29));
        blocked = Assert.Throws<TranscodeStartRejectedException>(() => jobs.CheckRetry("target", 1));
        Assert.AreEqual(1, blocked.RetryAfterSeconds);
        clock.Advance(TimeSpan.FromSeconds(1));
        jobs.CheckRetry("target", 1);
        var retry = jobs.Register(new MediaSourceInfo(), Output(workspace), "target", 1, clock.UtcNow);
        Assert.AreEqual(42, await jobs.ObserveStartAsync(Task.FromResult(42), retry));
        jobs.CheckRetry("target", 1);
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(10, true)]
    public async Task CancelledStartup_DistinguishesPromptUserCancellationFromStartupTimeout(int seconds, bool blocked)
    {
        using var workspace = new TestWorkspace();
        var clock = new ManualClock();
        var jobs = Create(clock);
        var releases = 0;
        var attempt = jobs.Register(
            new MediaSourceInfo(), Output(workspace), "target", 0, clock.UtcNow, () => releases++);
        clock.Advance(TimeSpan.FromSeconds(seconds));
        await Assert.ThrowsAsync<OperationCanceledException>(() => jobs.ObserveStartAsync(
            Task.FromCanceled<object>(new CancellationToken(true)), attempt));
        Assert.AreEqual(1, releases);
        if (blocked) Assert.Throws<TranscodeStartRejectedException>(() => jobs.CheckRetry("target", 0));
        else jobs.CheckRetry("target", 0);
    }

    [TestMethod]
    public async Task SuccessfulStart_KeepsResourcesUntilInactiveCleanupAndReleasesOnlyOnce()
    {
        using var workspace = new TestWorkspace();
        var clock = new ManualClock();
        var jobs = Create(clock);
        var releases = 0;
        var attempt = jobs.Register(
            new MediaSourceInfo(), Output(workspace), null, 0, clock.UtcNow, () => releases++);

        Assert.AreEqual(42, await jobs.ObserveStartAsync(Task.FromResult(42), attempt));
        Assert.AreEqual(0, releases);
        await jobs.CleanupAsync(attempt, 0, 0, _ => Assert.Fail("An active job must not be deleted."),
            _ => true, _ => Task.CompletedTask);
        Assert.AreEqual(0, releases);

        await jobs.CleanupAsync(attempt, 0, 0, _ => { }, _ => false, _ => Task.CompletedTask);
        Assert.AreEqual(1, releases);
        await jobs.CleanupAsync(attempt, 0, 0, _ => { }, _ => false, _ => Task.CompletedTask);
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public async Task ReusedDirectory_CleansOnlyTheEndedAttemptAndPreservesTheActiveOwner()
    {
        using var workspace = new TestWorkspace();
        var clock = new ManualClock();
        var jobs = Create(clock);
        var path = Output(workspace);
        var firstReleases = 0;
        var secondReleases = 0;
        var first = jobs.Register(
            new MediaSourceInfo(), path, null, 0, clock.UtcNow, () => firstReleases++);
        var second = jobs.Register(
            new MediaSourceInfo(), path, null, 0, clock.UtcNow, () => secondReleases++);

        await jobs.CleanupAsync(first, 0, 0, _ => Assert.Fail("A reused directory must not be deleted."),
            _ => true, _ => Task.CompletedTask);
        Assert.AreEqual(1, firstReleases);
        Assert.AreEqual(0, secondReleases);

        await jobs.CleanupAsync(second, 0, 0, _ => Assert.Fail("An active job must not be deleted."),
            _ => true, _ => Task.CompletedTask);
        Assert.AreEqual(0, secondReleases);
        await jobs.CleanupAsync(second, 0, 0, _ => { }, _ => false, _ => Task.CompletedTask);
        Assert.AreEqual(1, secondReleases);
    }

    [TestMethod]
    public async Task ConcurrentTarget_IsAdmittedOnceAndOtherTargetsAndGenerationsRemainIndependent()
    {
        using var workspace = new TestWorkspace();
        var clock = new ManualClock();
        var jobs = Create(clock);
        var admitted = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            try { return jobs.Register(new MediaSourceInfo(), Output(workspace), "one", 1, clock.UtcNow); }
            catch (TranscodeStartRejectedException) { return null; }
        })));
        Assert.AreEqual(1, admitted.Count(attempt => attempt is not null));
        jobs.CheckRetry("two", 1);
        jobs.CheckRetry("one", 2);
        jobs.Complete(admitted.Single(attempt => attempt is not null)!, true, false);
        jobs.CheckRetry("one", 2);
    }

    [TestMethod]
    public async Task DelayedCleanup_RechecksOwnerAfterDelayAndLatestJobReclaimsDirectory()
    {
        using var workspace = new TestWorkspace();
        var clock = new ManualClock();
        var jobs = Create(clock);
        var path = Output(workspace);
        var firstSource = new MediaSourceInfo();
        var first = jobs.Register(firstSource, path, null, 0, clock.UtcNow);
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletes = 0;
        var cleanup = jobs.CleanupAsync(first, 0, 1500, _ => deletes++, _ => false, _ => delay.Task);
        var secondSource = new MediaSourceInfo();
        var second = jobs.Register(secondSource, path, null, 0, clock.UtcNow);
        delay.SetResult();
        await cleanup;
        Assert.AreEqual(0, deletes);
        Assert.IsTrue(jobs.TryGetOwner(firstSource, path, out var preserved));
        Assert.AreSame(first, preserved);
        Assert.IsFalse(jobs.TryGetOwner(new MediaSourceInfo(firstSource), path, out _));
        Assert.IsFalse(jobs.TryGetOwner(firstSource, path + "other", out _));
        await jobs.CleanupAsync(second, 0, 0, _ => deletes++, _ => false, _ => Task.CompletedTask);
        Assert.AreEqual(1, deletes);
        GC.KeepAlive(secondSource);
    }

    [TestMethod]
    public async Task CleanupAndRegistration_AreSerializedOnlyForTheirDirectory()
    {
        using var workspace = new TestWorkspace();
        var clock = new ManualClock();
        var jobs = Create(clock);
        var source = new MediaSourceInfo();
        var attempt = jobs.Register(source, Output(workspace), null, 0, clock.UtcNow);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cleanup = Task.Run(() => jobs.CleanupAsync(attempt, 0, 0, _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }, _ => false, _ => Task.CompletedTask));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        var nextSource = new MediaSourceInfo();
        var registration = Task.Run(() => jobs.Register(nextSource, Output(workspace), null, 0, clock.UtcNow));
        try
        {
            Assert.IsFalse(registration.IsCompleted);
            var independent = Task.Run(() => jobs.Register(new MediaSourceInfo(),
                Path.Combine(workspace.Path, "other", "stream.m3u8"), null, 0, clock.UtcNow));
            Assert.IsNotNull(await independent.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); }
        await cleanup;
        Assert.IsNotNull(await registration);
    }

    [TestMethod]
    public async Task Cleanup_SkipsNativeReuseOrActiveJobAndRetriesBoundedly()
    {
        using var workspace = new TestWorkspace();
        var clock = new ManualClock();
        var jobs = Create(clock);
        var source = new MediaSourceInfo();
        var attempt = jobs.Register(source, Output(workspace), null, 0, clock.UtcNow);
        var deletes = 0;
        await jobs.CleanupAsync(attempt, 0, 0, _ => deletes++, _ => true, _ => Task.CompletedTask);
        Assert.AreEqual(0, deletes);
        var delays = new List<int>();
        await jobs.CleanupAsync(attempt, 0, 1500, _ => { deletes++; throw new IOException(); },
            _ => false, ms => { delays.Add(ms); return Task.CompletedTask; });
        Assert.AreEqual(10, deletes);
        CollectionAssert.AreEqual(new[] { 1500 }.Concat(Enumerable.Repeat(500, 9)).ToArray(), delays);
        jobs.ObserveUnmanagedStart(Output(workspace));
        await jobs.CleanupAsync(attempt, 0, 0, _ => deletes++, _ => false, _ => Task.CompletedTask);
        Assert.AreEqual(10, deletes);
    }

    [TestMethod]
    public void Registration_RejectsRootOrRelativeOutputs()
    {
        var clock = new ManualClock();
        var jobs = Create(clock);
        Assert.Throws<ArgumentException>(() => jobs.Register(new MediaSourceInfo(), "output.m3u8", null, 0, clock.UtcNow));
        Assert.Throws<ArgumentException>(() => jobs.Register(new MediaSourceInfo(),
            Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "output.m3u8"), null, 0, clock.UtcNow));
    }

    private static TranscodeJobCoordinator Create(ManualClock clock) =>
        new(clock, FastSeekCoordinatorTests.CreateLogger());

    private static string Output(TestWorkspace workspace) => Path.Combine(workspace.Path, "job", "stream.m3u8");
}
