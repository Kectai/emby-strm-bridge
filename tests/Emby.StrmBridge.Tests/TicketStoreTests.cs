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
            runtimeGeneration: 4,
            playbackLifetime: TimeSpan.FromHours(3));

        Assert.AreEqual(43, ticket.Length);
        Assert.IsTrue(store.TryInspect(ticket, out var inspected));
        Assert.AreEqual(TicketScope.Playback, inspected!.Scope);
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
            Guid.NewGuid(), "source-id", "user", TestSources.Create(), runtimeGeneration: 1);
        Assert.ThrowsExactly<TicketCapacityException>(() => store.IssuePlayback(
            Guid.NewGuid(), "other", "user", TestSources.Create("two"), runtimeGeneration: 1));
        Assert.IsTrue(store.TryInspect(parentTicket, out var parent));
        var child = store.IssueHlsResource(
            parentTicket,
            parent!,
            new Uri("https://source.invalid/segment.ts"),
            out var childCreated);
        Assert.IsTrue(childCreated);
        Assert.IsTrue(store.TryInspect(child, out var childPayload));
        Assert.AreEqual(TicketScope.HlsResource, childPayload!.Scope);
        Assert.AreEqual(parent!.ItemId, childPayload.ItemId);
        Assert.ThrowsExactly<TicketCapacityException>(() =>
            store.IssueHlsResource(
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
            Guid.NewGuid(), "source-id", null, TestSources.Create(), runtimeGeneration: 1);
        Assert.IsTrue(store.TryInspect(parentTicket, out var parent));
        var resource = new Uri("https://source.invalid/segment.ts?opaque=value");

        var first = store.IssueHlsResource(parentTicket, parent!, resource, out var firstCreated);
        var second = store.IssueHlsResource(parentTicket, parent!, resource, out var secondCreated);

        Assert.AreEqual(first, second);
        Assert.IsTrue(firstCreated);
        Assert.IsFalse(secondCreated);
        Assert.AreEqual(2, store.Count);
        store.Revoke(parentTicket);
        Assert.AreEqual(0, store.Count);
        Assert.IsFalse(store.TryInspect(first, out _));
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
