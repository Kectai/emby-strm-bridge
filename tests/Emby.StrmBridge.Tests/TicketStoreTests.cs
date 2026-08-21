using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class TicketStoreTests
{
    [TestMethod]
    public void Ticket_IsOpaqueScopedAndExpiresAtBoundary()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock, 2);
        var ticket = store.Issue(
            TicketScope.PlaybackRedirect,
            Guid.NewGuid(),
            "source-id",
            TestSources.Create(),
            TimeSpan.FromSeconds(30));

        Assert.AreEqual(43, ticket.Length);
        Assert.IsTrue(store.TryInspect(ticket, TicketScope.PlaybackRedirect, out var inspected));
        Assert.IsNotNull(inspected);
        Assert.AreEqual(0, inspected.BoundUserId);
        Assert.IsTrue(store.TryRedeem(ticket, TicketScope.PlaybackRedirect, 42, out var payload));
        Assert.IsNotNull(payload);
        Assert.IsFalse(store.TryRedeem(ticket, TicketScope.PlaybackRedirect, 43, out _));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.IsTrue(store.TryRedeem(ticket, TicketScope.PlaybackRedirect, 42, out _));
        clock.Advance(TicketStore.BoundPlaybackLifetime - TimeSpan.FromSeconds(30));
        Assert.IsFalse(store.TryRedeem(ticket, TicketScope.PlaybackRedirect, 42, out _));
    }

    [TestMethod]
    public void TicketStore_EnforcesCapacityAndClear()
    {
        var store = new TicketStore(new ManualClock(), 1);
        var ticket = store.Issue(TicketScope.PlaybackRedirect, Guid.NewGuid(), "one", TestSources.Create());
        Assert.ThrowsExactly<TicketCapacityException>(() =>
            store.Issue(TicketScope.PlaybackRedirect, Guid.NewGuid(), "two", TestSources.Create("two")));
        store.Clear();
        Assert.IsFalse(store.TryRedeem(ticket, TicketScope.PlaybackRedirect, 42, out _));
    }

    [TestMethod]
    public void TicketStore_BoundsKnownRuntimeWithReconnectGrace()
    {
        Assert.AreEqual(
            TimeSpan.FromHours(4),
            TicketStore.ComputeBoundPlaybackLifetime(TimeSpan.FromHours(2).Ticks));
        Assert.AreEqual(
            TicketStore.BoundPlaybackLifetime,
            TicketStore.ComputeBoundPlaybackLifetime(TimeSpan.FromDays(3).Ticks));
        Assert.AreEqual(
            TicketStore.BoundPlaybackLifetime,
            TicketStore.ComputeBoundPlaybackLifetime(null));
    }

    [TestMethod]
    public void LocalServerRedemption_ExtendsTicketWithoutPreventingAuthorizedUserBinding()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock, 2);
        var ticket = store.Issue(
            TicketScope.PlaybackRedirect,
            Guid.NewGuid(),
            "source-id",
            TestSources.Create(),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(3));

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.IsTrue(store.TryRedeemLocalServer(ticket, TicketScope.PlaybackRedirect, out var local));
        Assert.IsNotNull(local);
        Assert.AreEqual(0, local.BoundUserId);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsTrue(store.TryRedeem(ticket, TicketScope.PlaybackRedirect, 42, out _));
        Assert.IsFalse(store.TryRedeem(ticket, TicketScope.PlaybackRedirect, 43, out _));
        Assert.IsTrue(store.TryRedeemLocalServer(ticket, TicketScope.PlaybackRedirect, out _));
    }

    [TestMethod]
    public void LocalServerRedemption_DoesNotSlideTheBoundExpiration()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock, 2);
        var ticket = store.Issue(
            TicketScope.PlaybackRedirect,
            Guid.NewGuid(),
            "source-id",
            TestSources.Create(),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(3));

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.IsTrue(store.TryRedeemLocalServer(ticket, TicketScope.PlaybackRedirect, out _));
        clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(59));
        Assert.IsTrue(store.TryRedeemLocalServer(ticket, TicketScope.PlaybackRedirect, out _));
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.IsFalse(store.TryRedeemLocalServer(ticket, TicketScope.PlaybackRedirect, out _));
    }
}
