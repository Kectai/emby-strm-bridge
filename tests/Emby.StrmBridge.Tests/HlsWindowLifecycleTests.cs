using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class HlsWindowLifecycleTests
{
    [TestMethod]
    public async Task MaximumManifest_ReusesTenThousandResourcesWithoutPerUriGlobalSweeps()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock);
        var root = CreateRoot(store, out var payload);
        var resources = Enumerable.Range(0, HlsPlaylistRewriter.MaximumUris)
            .Select(i => new Uri("https://source.invalid/segment" + i + ".ts")).ToArray();
        var before = store.ExpirationSweepCount;
        string[] children;
        using (await store.AcquireHlsMutationAsync(root, CancellationToken.None))
        {
            children = resources.Select(uri => store.IssueHlsResource(root, root, payload, uri, out _)).ToArray();
            store.CommitHlsManifest(root, root, children, TimeSpan.FromMinutes(2));
        }
        using (await store.AcquireHlsMutationAsync(root, CancellationToken.None))
        {
            for (var i = 0; i < resources.Length; i++)
                Assert.AreEqual(children[i], store.IssueHlsResource(root, root, payload, resources[i], out _));
            store.CommitHlsManifest(root, root, children, TimeSpan.FromMinutes(2));
        }
        Assert.AreEqual(2L, store.ExpirationSweepCount - before,
            "Global cleanup must scale with manifest transactions, not URI count.");
        Assert.AreEqual(10001, store.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExpiredResourceInsideSweepInterval_IsNeverReusedAndCapacityIsReclaimed(bool sameUri)
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock, hlsCapacity: 1);
        var root = CreateRoot(store, out var payload);
        var uri = new Uri("https://source.invalid/old.ts");
        var old = store.IssueHlsResource(root, root, payload, uri, out _);
        store.CommitHlsManifest(root, root, new[] { old }, TimeSpan.FromMilliseconds(10));
        store.CommitHlsManifest(root, root, Array.Empty<string>(), TimeSpan.FromMilliseconds(10));
        clock.Advance(TimeSpan.FromMilliseconds(20));
        var replacement = store.IssueHlsResource(root, root, payload,
            sameUri ? uri : new Uri("https://source.invalid/new.ts"), out _);
        Assert.AreNotEqual(old, replacement);
        Assert.IsFalse(store.TryInspect(old, out _));
        Assert.IsTrue(store.TryInspect(replacement, out _));
        Assert.AreEqual(2, store.Count);
    }

    [TestMethod]
    public void ExpiredRootInsideSweepInterval_CannotIssueOrphanResources()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock);
        var root = store.IssuePlayback(Guid.NewGuid(), "source", null, TestSources.Create(),
            PlaybackTicketPurpose.ExtractionProbe, 0, TimeSpan.FromMilliseconds(10));
        store.TryInspect(root, out var payload);
        clock.Advance(TimeSpan.FromMilliseconds(20));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.IssueHlsResource(root, root, payload!,
            new Uri("https://source.invalid/segment.ts"), out _));
    }

    [TestMethod]
    public void RollingWindows_RecycleAfterGraceWithoutRedeemExtendingRetirement()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock, hlsCapacity: 32);
        var root = CreateRoot(store, out var payload);
        string? first = null;
        for (var i = 0; i < 1000; i++)
        {
            var children = Enumerable.Range(i, 3).Select(n => store.IssueHlsResource(
                root, root, payload, new Uri("https://source.invalid/" + n + ".ts"), out _)).ToArray();
            first ??= children[0];
            store.CommitHlsManifest(root, root, children, TimeSpan.FromSeconds(18));
            if (i == 2) Assert.IsTrue(store.TryRedeem(first, null, out _));
            if (i == 5) Assert.IsFalse(store.TryRedeem(first, null, out _));
            Assert.IsTrue(store.Count <= 8);
            clock.Advance(TimeSpan.FromSeconds(6));
        }
        store.Revoke(root);
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public void SharedResource_RemainsUntilEveryManifestReferenceRetires()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock);
        var root = CreateRoot(store, out var payload);
        var a = store.IssueHlsResource(root, root, payload, new Uri("https://source.invalid/a.m3u8"), out _);
        var b = store.IssueHlsResource(root, root, payload, new Uri("https://source.invalid/b.m3u8"), out _);
        store.TryInspect(a, out var pa);
        store.TryInspect(b, out var pb);
        var segment = store.IssueHlsResource(root, a, pa!, new Uri("https://source.invalid/shared.ts"), out _);
        Assert.AreEqual(segment, store.IssueHlsResource(root, b, pb!, new Uri("https://source.invalid/shared.ts"), out _));
        var grace = TimeSpan.FromMinutes(2);
        store.CommitHlsManifest(root, a, new[] { segment }, grace);
        store.CommitHlsManifest(root, b, new[] { segment }, grace);
        store.CommitHlsManifest(root, a, Array.Empty<string>(), grace);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.IsTrue(store.TryRedeem(segment, null, out _));
        store.CommitHlsManifest(root, b, Array.Empty<string>(), grace);
        clock.Advance(grace);
        Assert.IsFalse(store.TryInspect(segment, out _));
    }

    [TestMethod]
    public void RemovedNestedManifest_RetiresItsDescendantsWithTheirOwnGrace()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock);
        var root = CreateRoot(store, out var payload);
        var playlist = store.IssueHlsResource(root, root, payload, new Uri("https://source.invalid/live.m3u8"), out _);
        store.TryInspect(playlist, out var parent);
        var segment = store.IssueHlsResource(root, playlist, parent!, new Uri("https://source.invalid/segment.ts"), out _);
        var grace = TimeSpan.FromMinutes(2);
        store.CommitHlsManifest(root, root, new[] { playlist }, grace);
        store.CommitHlsManifest(root, playlist, new[] { segment }, grace);
        store.CommitHlsManifest(root, root, Array.Empty<string>(), grace);
        clock.Advance(grace);
        store.RemoveExpired();
        Assert.IsFalse(store.TryInspect(playlist, out _));
        Assert.IsTrue(store.TryInspect(segment, out _));
        clock.Advance(grace);
        Assert.IsFalse(store.TryInspect(segment, out _));
    }

    [TestMethod]
    public void VodAndOlderLongerWindow_AreNotShortenedByLaterManifest()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock);
        var root = CreateRoot(store, out var payload);
        var segment = store.IssueHlsResource(root, root, payload, new Uri("https://source.invalid/segment.ts"), out _);
        store.CommitHlsManifest(root, root, new[] { segment }, TimeSpan.FromMinutes(10));
        store.CommitHlsManifest(root, root, Array.Empty<string>(), TimeSpan.FromMinutes(2));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.IsTrue(store.TryInspect(segment, out _));
        store.CommitHlsManifest(root, root, new[] { segment }, null);
        store.CommitHlsManifest(root, root, Array.Empty<string>(), TimeSpan.FromMinutes(2));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.IsTrue(store.TryInspect(segment, out _));
    }

    [TestMethod]
    public void RootQuota_LeavesCapacityForOtherPlaybackSessions()
    {
        var store = new TicketStore(new ManualClock(), hlsCapacity: 4, hlsRootCapacity: 2);
        var root = CreateRoot(store, out var payload);
        for (var i = 0; i < 2; i++) store.IssueHlsResource(root, root, payload, new Uri("https://source.invalid/" + i), out _);
        Assert.ThrowsExactly<TicketCapacityException>(() => store.IssueHlsResource(root, root, payload,
            new Uri("https://source.invalid/full"), out _));
        var other = CreateRoot(store, out var otherPayload);
        var child = store.IssueHlsResource(other, other, otherPayload, new Uri("https://source.invalid/other"), out _);
        Assert.IsTrue(store.TryInspect(child, out _));
        store.Clear();
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public void Retention_UsesPlaylistPlusSegmentDurationAndPreservesStaticVod()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(360), HlsPlaylistRewriter.GetResourceRetention(
            "#EXTM3U\n#EXT-X-TARGETDURATION:90\n#EXTINF:90,\na.ts\n#EXTINF:90,\nb.ts\n#EXTINF:90,\nc.ts"));
        Assert.AreEqual(TimeSpan.FromMinutes(2), HlsPlaylistRewriter.GetResourceRetention("#EXTM3U\nvariant.m3u8"));
        Assert.IsNull(HlsPlaylistRewriter.GetResourceRetention("#EXTM3U\n#EXT-X-ENDLIST"));
        Assert.ThrowsExactly<InvalidOperationException>(() => HlsPlaylistRewriter.GetResourceRetention("#EXTM3U\n#EXTINF:NaN,"));
    }

    private static string CreateRoot(TicketStore store, out TicketPayload payload)
    {
        var root = store.IssuePlayback(Guid.NewGuid(), "source", null, TestSources.Create(), PlaybackTicketPurpose.DirectClient, 0);
        Assert.IsTrue(store.TryRedeem(root, null, out var found));
        payload = found!;
        return root;
    }
}
