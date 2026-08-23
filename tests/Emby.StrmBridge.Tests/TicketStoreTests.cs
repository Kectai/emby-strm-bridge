using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class TicketStoreTests
{
    [TestMethod]
    public void PlaybackTicket_IsOpaqueUserBoundAndCapabilityAccessible()
    {
        var store = new TicketStore(new ManualClock(), 2, 2);
        var ticket = store.IssuePlayback(
            Guid.NewGuid(),
            "source-id",
            "ABC-123",
            TestSources.Create(),
            PlaybackTicketPurpose.DirectClient,
            runtimeGeneration: 4,
            playbackLifetime: TimeSpan.FromHours(3));

        Assert.AreEqual(43, ticket.Length);
        Assert.IsTrue(store.TryInspect(ticket, out var inspected));
        Assert.AreEqual(TicketScope.Playback, inspected!.Scope);
        Assert.AreEqual(PlaybackTicketPurpose.DirectClient, inspected.Purpose);
        Assert.AreEqual(4, inspected.RuntimeGeneration);
        Assert.IsTrue(store.TryRedeem(ticket, null, out _));
        Assert.IsTrue(store.TryRedeem(ticket, "abc123", out _));
        Assert.IsFalse(store.TryRedeem(ticket, "another-user", out _));
    }

    [TestMethod]
    public void PreviewAndPlaybackLifetimes_AreBounded()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock, 2, 2);
        var ticket = store.IssuePlayback(
            Guid.NewGuid(),
            "source-id",
            null,
            TestSources.Create(),
            PlaybackTicketPurpose.DirectClient,
            runtimeGeneration: 1,
            playbackLifetime: TimeSpan.FromHours(3));

        clock.Advance(TicketStore.PreviewLifetime - TimeSpan.FromSeconds(1));
        Assert.IsTrue(store.TryRedeem(ticket, null, out _));
        clock.Advance(TimeSpan.FromHours(2));
        Assert.IsTrue(store.TryRedeem(ticket, null, out _));
        clock.Advance(TicketStore.MaximumLifetime);
        Assert.IsFalse(store.TryRedeem(ticket, null, out _));
    }

    [TestMethod]
    public void ScopeCapacitiesAndChildBinding_AreIndependent()
    {
        var store = new TicketStore(new ManualClock(), 1, 1);
        var parentTicket = store.IssuePlayback(
            Guid.NewGuid(), "source-id", "user", TestSources.Create(),
            PlaybackTicketPurpose.ServerFfmpeg, runtimeGeneration: 1);
        Assert.ThrowsExactly<TicketCapacityException>(() => store.IssuePlayback(
            Guid.NewGuid(), "other", "user", TestSources.Create("two"),
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1));
        Assert.IsTrue(store.TryInspect(parentTicket, out var parent));
        var child = store.IssueHlsResource(
            parentTicket,
            parentTicket,
            parent!,
            new Uri("https://source.invalid/segment.ts"),
            out var childCreated);
        Assert.IsTrue(childCreated);
        Assert.IsTrue(store.TryInspect(child, out var childPayload));
        Assert.AreEqual(TicketScope.HlsResource, childPayload!.Scope);
        Assert.AreEqual(PlaybackTicketPurpose.ServerFfmpeg, childPayload.Purpose);
        Assert.AreEqual(parent!.ItemId, childPayload.ItemId);
        Assert.ThrowsExactly<TicketCapacityException>(() =>
            store.IssueHlsResource(
                parentTicket,
                parentTicket,
                parent!,
                new Uri("https://source.invalid/second.ts"),
                out _));
    }

    [TestMethod]
    public void HlsResource_ReusesParentUriMappingAndRevokesWithParent()
    {
        var store = new TicketStore(new ManualClock(), 1, 2);
        var parentTicket = store.IssuePlayback(
            Guid.NewGuid(), "source-id", null, TestSources.Create(),
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1);
        Assert.IsTrue(store.TryInspect(parentTicket, out var parent));
        var resource = new Uri("https://source.invalid/segment.ts?opaque=value");

        var first = store.IssueHlsResource(
            parentTicket, parentTicket, parent!, resource, out var firstCreated);
        var second = store.IssueHlsResource(
            parentTicket, parentTicket, parent!, resource, out var secondCreated);

        Assert.AreEqual(first, second);
        Assert.IsTrue(firstCreated);
        Assert.IsFalse(secondCreated);
        Assert.AreEqual(2, store.Count);
        store.Revoke(parentTicket);
        Assert.AreEqual(0, store.Count);
        Assert.IsFalse(store.TryInspect(first, out _));
    }

    [TestMethod]
    public void HlsResource_RejectsAChildFromAnotherRoot()
    {
        var store = new TicketStore(new ManualClock(), 2, 3);
        var itemId = Guid.NewGuid();
        var source = TestSources.Create();
        var firstRoot = store.IssuePlayback(
            itemId, "source-id", null, source, PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1);
        var secondRoot = store.IssuePlayback(
            itemId, "source-id", null, source, PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1);
        Assert.IsTrue(store.TryInspect(firstRoot, out var firstParent));
        var child = store.IssueHlsResource(
            firstRoot,
            firstRoot,
            firstParent!,
            new Uri("https://source.invalid/segment.ts"),
            out _);
        Assert.IsTrue(store.TryInspect(child, out var childPayload));

        Assert.IsTrue(store.IsHlsResourceOfRoot(firstRoot, child));
        Assert.IsFalse(store.IsHlsResourceOfRoot(secondRoot, child));
        Assert.ThrowsExactly<InvalidOperationException>(() => store.IssueHlsResource(
            secondRoot,
            child,
            childPayload!,
            new Uri("https://source.invalid/nested.ts"),
            out _));

        store.Revoke(firstRoot);
        Assert.IsFalse(store.TryInspect(child, out _));
    }

    [TestMethod]
    public async Task HlsMutation_SerializesRollbackBeforeAnotherRequestCanReuseATicket()
    {
        var store = new TicketStore(new ManualClock(), 1, 2);
        var root = store.IssuePlayback(
            Guid.NewGuid(), "source-id", null, TestSources.Create(),
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1);
        Assert.IsTrue(store.TryInspect(root, out var parent));
        var resource = new Uri("https://source.invalid/segment.ts");
        var firstMutation = await store.AcquireHlsMutationAsync(root, CancellationToken.None);
        string firstTicket;
        Task<IDisposable> waiting;
        try
        {
            firstTicket = store.IssueHlsResource(root, root, parent!, resource, out var created);
            Assert.IsTrue(created);
            waiting = store.AcquireHlsMutationAsync(root, CancellationToken.None);
            await Task.Yield();
            Assert.IsFalse(waiting.IsCompleted);
            store.Revoke(firstTicket);
        }
        finally
        {
            firstMutation.Dispose();
        }

        using var secondMutation = await waiting;
        var secondTicket = store.IssueHlsResource(root, root, parent!, resource, out var secondCreated);
        Assert.IsTrue(secondCreated);
        Assert.AreNotEqual(firstTicket, secondTicket);
        Assert.IsTrue(store.TryInspect(secondTicket, out _));
    }

    [TestMethod]
    public void HlsResource_AllowsADagReferenceAtANewDepth()
    {
        var store = new TicketStore(new ManualClock(), 1, 5);
        var parentTicket = store.IssuePlayback(
            Guid.NewGuid(), "source-id", null, TestSources.Create(),
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1);
        Assert.IsTrue(store.TryInspect(parentTicket, out var parent));
        var firstUri = new Uri("https://source.invalid/first.m3u8");
        var secondUri = new Uri("https://source.invalid/second.m3u8");
        var first = store.IssueHlsResource(parentTicket, parentTicket, parent!, firstUri, out _);
        var second = store.IssueHlsResource(parentTicket, parentTicket, parent!, secondUri, out _);
        Assert.IsTrue(store.TryInspect(second, out var secondPayload));
        var nestedFirst = store.IssueHlsResource(
            parentTicket, second, secondPayload!, firstUri, out var created);

        Assert.IsTrue(created);
        Assert.AreNotEqual(first, nestedFirst);
        Assert.IsTrue(store.TryInspect(nestedFirst, out var nestedPayload));
        Assert.AreEqual(2, nestedPayload!.HlsDepth);
    }

    [TestMethod]
    public void HlsResource_CycleAdvancesDepthUntilTheLimit()
    {
        var store = new TicketStore(new ManualClock(), 1, 9);
        var parentTicket = store.IssuePlayback(
            Guid.NewGuid(), "source-id", null, TestSources.Create(),
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1);
        Assert.IsTrue(store.TryInspect(parentTicket, out var current));
        var resources = new[]
        {
            new Uri("https://source.invalid/first.m3u8"),
            new Uri("https://source.invalid/second.m3u8"),
        };
        string? previousTicket = null;
        for (var depth = 1; depth <= 8; depth++)
        {
            var ticket = store.IssueHlsResource(
                parentTicket,
                depth == 1 ? parentTicket : previousTicket!,
                current!,
                resources[(depth - 1) % resources.Length],
                out var created);
            Assert.IsTrue(created);
            Assert.IsTrue(store.TryInspect(ticket, out current));
            Assert.AreEqual(depth, current!.HlsDepth);
            previousTicket = ticket;
        }

        Assert.ThrowsExactly<TicketCapacityException>(() =>
            store.IssueHlsResource(parentTicket, previousTicket!, current!, resources[0], out _));
    }

    [TestMethod]
    public void RuntimeCalculation_AddsReconnectGraceAndCapsAtMaximum()
    {
        Assert.AreEqual(TimeSpan.FromHours(4),
            TicketStore.ComputePlaybackLifetime(TimeSpan.FromHours(2).Ticks));
        Assert.AreEqual(TicketStore.MaximumLifetime,
            TicketStore.ComputePlaybackLifetime(TimeSpan.FromDays(3).Ticks));
        Assert.AreEqual(TicketStore.MaximumLifetime, TicketStore.ComputePlaybackLifetime(null));
    }
}
