using Emby.StrmBridge.Runtime;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class PluginRuntimeTests
{
    [TestMethod]
    public void DetectedRedirectHost_IsIgnoredAfterDisableOrDispose()
    {
        using var workspace = new TestWorkspace();
        var runtime = new PluginRuntime();
        runtime.Initialize(workspace.Path, new ManualClock(), new StubRedirectClient((_, _, _, _) =>
            Task.FromResult(new RedirectSourceResponse(404, null, null))));
        runtime.UpdateOptions(new PluginConfiguration { Enabled = false }, invalidateSensitiveState: true);

        runtime.RecordDetectedRedirectHost("disabled.invalid");
        Assert.IsEmpty(runtime.GetDetectedRedirectHosts());

        runtime.Dispose();
        runtime.RecordDetectedRedirectHost("disposed.invalid");
        Assert.IsEmpty(runtime.GetDetectedRedirectHosts());
    }

    [TestMethod]
    public void DetectedRedirectHosts_AreBoundedRecentAndExcludeAlreadyTrustedHosts()
    {
        using var runtime = new PluginRuntime();
        for (var index = 0; index <= PluginRuntime.MaximumDetectedRedirectHosts; index++)
            runtime.RecordDetectedRedirectHost($"host-{index}.invalid");

        var detected = runtime.GetDetectedRedirectHosts(new[]
        {
            $"host-{PluginRuntime.MaximumDetectedRedirectHosts}.invalid",
        });
        Assert.AreEqual(PluginRuntime.MaximumDetectedRedirectHosts - 1, detected.Length);
        Assert.DoesNotContain("host-0.invalid", detected);
        Assert.DoesNotContain($"host-{PluginRuntime.MaximumDetectedRedirectHosts}.invalid", detected);
        Assert.AreEqual($"host-{PluginRuntime.MaximumDetectedRedirectHosts - 1}.invalid", detected[0]);
    }

    [TestMethod]
    public async Task RedirectRejection_PublishesOnlyTheTargetHostToConfigurationCandidates()
    {
        using var workspace = new TestWorkspace();
        using var runtime = new PluginRuntime();
        runtime.Initialize(
            workspace.Path,
            new ManualClock(),
            new StubRedirectClient((_, _, _, _) => Task.FromResult(
                new Emby.StrmBridge.Playback.RedirectSourceResponse(
                    302,
                    "https://detected.invalid/private/path?credential=not-retained",
                    null))));

        var exception = await Assert.ThrowsExactlyAsync<Emby.StrmBridge.Policy.RedirectRejectedException>(() =>
            runtime.Redirects!.ResolveForProbeAsync(TestSources.Create(), null, CancellationToken.None));

        Assert.AreEqual(Emby.StrmBridge.Policy.RedirectRejectionReason.UntrustedTargetHost, exception.Reason);
        CollectionAssert.AreEqual(new[] { "detected.invalid" }, runtime.GetDetectedRedirectHosts());
    }

    [TestMethod]
    public void ClearSensitiveState_CancelsOldGenerationAndRejectsLateCommit()
    {
        using var workspace = new TestWorkspace();
        using var runtime = new PluginRuntime();
        runtime.Initialize(
            workspace.Path,
            new ManualClock(),
            new StubRedirectClient((_, _, _, _) =>
                Task.FromResult(new Emby.StrmBridge.Playback.RedirectSourceResponse(404, null, null))));
        var operation = runtime.BeginOperation();
        var committed = false;

        runtime.ClearSensitiveState();

        Assert.IsTrue(operation.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(runtime.TryCommit(operation.Generation, () => true, () => committed = true));
        Assert.IsFalse(committed);
        Assert.IsTrue(runtime.IsOperationCurrent(runtime.BeginOperation().Generation));
    }

    [TestMethod]
    public void UpdateOptions_PublishesSnapshotWhileInvalidatingTicketsAndOperations()
    {
        using var workspace = new TestWorkspace();
        using var runtime = new PluginRuntime();
        runtime.Initialize(
            workspace.Path,
            new ManualClock(),
            new StubRedirectClient((_, _, _, _) =>
                Task.FromResult(new Emby.StrmBridge.Playback.RedirectSourceResponse(404, null, null))));
        var operation = runtime.BeginOperation();
        var ticket = runtime.Tickets.IssuePlayback(
            Guid.NewGuid(),
            "source",
            "user",
            TestSources.Create(),
            Emby.StrmBridge.Domain.PlaybackTicketPurpose.DirectClient,
            runtime.Generation);

        runtime.UpdateOptions(
            new PluginConfiguration { Enabled = false },
            invalidateSensitiveState: true);

        Assert.IsFalse(runtime.GetOptionsSnapshot().Enabled);
        Assert.IsTrue(operation.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(runtime.Tickets.TryRedeem(ticket, "user", out _));
    }

    [TestMethod]
    public void UpdateOptions_RetainsDetectedHostsUntilPluginIsDisabled()
    {
        using var runtime = new PluginRuntime();
        runtime.RecordDetectedRedirectHost("detected.invalid");

        runtime.UpdateOptions(
            new PluginConfiguration { Enabled = true },
            invalidateSensitiveState: true);

        CollectionAssert.AreEqual(new[] { "detected.invalid" }, runtime.GetDetectedRedirectHosts());

        runtime.UpdateOptions(
            new PluginConfiguration { Enabled = false },
            invalidateSensitiveState: true);

        Assert.IsEmpty(runtime.GetDetectedRedirectHosts());
    }

    [TestMethod]
    public async Task SensitiveStateInvalidation_ClearsPreparedFastSeekPlans()
    {
        using var workspace = new TestWorkspace();
        using var runtime = new PluginRuntime();
        runtime.Initialize(
            workspace.Path,
            new ManualClock(),
            new StubRedirectClient((_, _, _, _) =>
                Task.FromResult(new RedirectSourceResponse(404, null, null))));
        runtime.InitializeFastSeek(
            FastSeekCoordinatorTests.CreateLogger(),
            new SyntheticFastSeekProbeClient());
        Assert.IsTrue(await runtime.FastSeek!.PrepareAsync(
            TestSources.Create(),
            "source",
            TimeSpan.FromSeconds(50).Ticks,
            TimeSpan.FromSeconds(100).Ticks,
            runtime.Generation,
            new PluginConfiguration(),
            CancellationToken.None));
        Assert.AreEqual(1, runtime.FastSeek.Count);

        runtime.UpdateOptions(new PluginConfiguration(), invalidateSensitiveState: true);

        Assert.AreEqual(0, runtime.FastSeek.Count);
    }

    [TestMethod]
    public void PendingHostNotification_RearmsOnlyForANewDetectedHost()
    {
        using var runtime = new PluginRuntime();
        runtime.RecordDetectedRedirectHost("first.invalid");

        Assert.IsTrue(runtime.TryBeginPendingHostNotification());
        Assert.IsFalse(runtime.TryBeginPendingHostNotification());

        runtime.RecordDetectedRedirectHost("first.invalid");
        Assert.IsFalse(runtime.TryBeginPendingHostNotification());

        runtime.RecordDetectedRedirectHost("second.invalid");
        Assert.IsTrue(runtime.TryBeginPendingHostNotification());
    }

    [TestMethod]
    public void DetectedRedirectHosts_ExcludeHostsCoveredByExplicitSubdomainRule()
    {
        using var runtime = new PluginRuntime();
        runtime.RecordDetectedRedirectHost("edge.cdn.example.invalid");
        runtime.RecordDetectedRedirectHost("pending.invalid");

        CollectionAssert.AreEqual(
            new[] { "pending.invalid" },
            runtime.GetDetectedRedirectHosts(new[] { "*.cdn.example.invalid" }));
    }
}
