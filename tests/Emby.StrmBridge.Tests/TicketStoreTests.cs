using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class TicketStoreTests
{
    [TestMethod]
    public void TranscodeInput_MatchesExactPurposeUserSourceVersionAndGenerationWithoutExtendingExpiry()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock);
        var item = Guid.NewGuid();
        var source = TestSources.Create();
        var ticket = store.IssuePlayback(item, "media", "user", source, PlaybackTicketPurpose.ServerFfmpeg, 3);
        Assert.IsTrue(store.MatchesTranscodeInput(ticket, item, "media", "user", source, 3));
        Assert.IsFalse(store.MatchesTranscodeInput(ticket, item, "media", null, source, 3));
        Assert.IsFalse(store.MatchesTranscodeInput(ticket, item, "other", "user", source, 3));
        Assert.IsFalse(store.MatchesTranscodeInput(ticket, Guid.NewGuid(), "media", "user", source, 3));
        Assert.IsFalse(store.MatchesTranscodeInput(ticket, item, "media", "user", source, 4));
        var clientTicket = store.IssuePlayback(item, "media", "user", source, PlaybackTicketPurpose.DirectClient, 3);
        Assert.IsFalse(store.MatchesTranscodeInput(clientTicket, item, "media", "user", source, 3));
        var changedSource = new SourceIdentity(source.StorageKey, source.SourceFingerprint, source.SourceUri,
            source.LocalPath, source.LocalFileLength + 1, source.LocalLastWriteUtc);
        Assert.IsFalse(store.MatchesTranscodeInput(ticket, item, "media", "user", changedSource, 3));
        clock.Advance(TicketStore.PreviewLifetime);
        Assert.IsFalse(store.MatchesTranscodeInput(ticket, item, "media", "user", source, 3));
    }

    [TestMethod]
    public void NativePlayback_ReusesSessionButSeparatesUsersDevicesAndNewPlayback()
    {
        var store = new TicketStore(new ManualClock());
        var item = Guid.NewGuid();
        var source = TestSources.Create();
        var lifetime = TimeSpan.FromHours(2);
        var first = store.GetOrIssueNativePlayback(item, "media", "user", "device", "session-one",
            source, 0, lifetime, out var transient);
        Assert.IsFalse(transient);
        for (var i = 0; i < TicketStore.MaximumPlaybackTickets + 1; i++)
        {
            var next = store.GetOrIssueNativePlayback(item, "media", "user", "device", "session-one",
                source, 0, lifetime, out transient);
            Assert.AreEqual(first, next);
            Assert.IsTrue(store.TryRedeem(next, "user", out _));
        }
        Assert.AreEqual(1, store.Count);
        Assert.AreNotEqual(first, store.GetOrIssueNativePlayback(item, "media", "user2", "device", "session-one",
            source, 0, lifetime, out _));
        Assert.AreNotEqual(first, store.GetOrIssueNativePlayback(item, "media", "user", "device2", "session-one",
            source, 0, lifetime, out _));
        var fresh = store.IssuePlayback(item, "media", "user", source, PlaybackTicketPurpose.DirectClient, 0,
            lifetime, "device");
        store.RegisterNativePlayback(fresh, "session-two");
        Assert.AreEqual(fresh, store.GetOrIssueNativePlayback(item, "media", "user", "device", null,
            source, 0, lifetime, out transient));
        Assert.IsFalse(transient);
        Assert.AreNotEqual(first, fresh);
        store.Clear();
        var anonymous = store.GetOrIssueNativePlayback(item, "media", null, null, null,
            source, 1, lifetime, out transient);
        Assert.IsTrue(transient);
        store.ReleaseRequestTicket(anonymous);
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public void SourceRedirectHandoffHint_IsLimitedToDirectPlaybackAndDoesNotReachHlsChildren()
    {
        var store = new TicketStore(new ManualClock());
        var item = Guid.NewGuid();
        var source = TestSources.Create();
        var directTicket = store.IssuePlayback(
            item, "media", "user", source, PlaybackTicketPurpose.DirectClient, 1,
            sourceRedirectHandoffAllowed: true);
        var ffmpegTicket = store.IssuePlayback(
            item, "media", "user", source, PlaybackTicketPurpose.ServerFfmpeg, 1,
            sourceRedirectHandoffAllowed: true);

        Assert.IsTrue(store.TryInspect(directTicket, out var direct));
        Assert.IsTrue(direct!.SourceRedirectHandoffAllowed);
        Assert.IsTrue(store.TryInspect(ffmpegTicket, out var ffmpeg));
        Assert.IsFalse(ffmpeg!.SourceRedirectHandoffAllowed);

        var childTicket = store.IssueHlsResource(
            directTicket,
            directTicket,
            direct,
            new Uri("https://source.invalid/segment.ts"),
            out _);
        Assert.IsTrue(store.TryInspect(childTicket, out var child));
        Assert.IsFalse(child!.SourceRedirectHandoffAllowed);
    }

    [TestMethod]
    public void NativePlayback_SeparatesKnownFileAndUnknownSourceHintsWithinOneSession()
    {
        var store = new TicketStore(new ManualClock());
        var item = Guid.NewGuid();
        var source = TestSources.Create();
        var lifetime = TimeSpan.FromHours(2);
        var unknown = store.GetOrIssueNativePlayback(
            item, "media", "user", "device", "session", source, 1, lifetime, out _,
            sourceRedirectHandoffAllowed: false);
        var knownFile = store.GetOrIssueNativePlayback(
            item, "media", "user", "device", "session", source, 1, lifetime, out _,
            sourceRedirectHandoffAllowed: true);
        var knownFileAgain = store.GetOrIssueNativePlayback(
            item, "media", "user", "device", "session", source, 1, lifetime, out _,
            sourceRedirectHandoffAllowed: true);

        Assert.AreNotEqual(unknown, knownFile);
        Assert.AreEqual(knownFile, knownFileAgain);
        Assert.IsTrue(store.TryInspect(unknown, out var unknownPayload));
        Assert.IsFalse(unknownPayload!.SourceRedirectHandoffAllowed);
        Assert.IsTrue(store.TryInspect(knownFile, out var knownFilePayload));
        Assert.IsTrue(knownFilePayload!.SourceRedirectHandoffAllowed);
    }

    [TestMethod]
    public void ProbeTicket_CannotOutliveItsShortProbeBudget()
    {
        var clock = new ManualClock();
        var store = new TicketStore(clock);
        var ticket = store.IssuePlayback(Guid.NewGuid(), "probe", null, TestSources.Create(),
            PlaybackTicketPurpose.ExtractionProbe, 0, TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.IsTrue(store.TryRedeem(ticket, null, out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsFalse(store.TryRedeem(ticket, null, out _));
    }

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
    public void DirectRouteScope_IsStableWithinOneTicketAndSeparatedAcrossPlaybackTickets()
    {
        var store = new TicketStore(new ManualClock(), 6, 2);
        var itemId = Guid.NewGuid();
        var source = TestSources.Create();
        var firstTicket = store.IssuePlayback(
            itemId, "source-id", "first-user", source,
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1,
            deviceId: "device-a");
        var reconnectTicket = store.IssuePlayback(
            itemId, "source-id", "first-user", source,
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1,
            deviceId: "device-a");
        var anotherUserTicket = store.IssuePlayback(
            itemId, "source-id", "second-user", source,
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1,
            deviceId: "device-a");
        var anotherDeviceTicket = store.IssuePlayback(
            itemId, "source-id", "first-user", source,
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1,
            deviceId: "device-b");
        var unboundTicket = store.IssuePlayback(
            itemId, "source-id", "first-user", source,
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1);
        var anotherUnboundTicket = store.IssuePlayback(
            itemId, "source-id", "first-user", source,
            PlaybackTicketPurpose.DirectClient, runtimeGeneration: 1);
        Assert.IsTrue(store.TryInspect(firstTicket, out var first));
        Assert.IsTrue(store.TryInspect(reconnectTicket, out var reconnect));
        Assert.IsTrue(store.TryInspect(anotherUserTicket, out var anotherUser));
        Assert.IsTrue(store.TryInspect(anotherDeviceTicket, out var anotherDevice));
        Assert.IsTrue(store.TryInspect(unboundTicket, out var unbound));
        Assert.IsTrue(store.TryInspect(anotherUnboundTicket, out var anotherUnbound));

        var firstScope = GatewayTransport.CreateDirectRouteScope(first!, firstTicket);
        Assert.AreEqual(firstScope, GatewayTransport.CreateDirectRouteScope(first!, firstTicket));
        Assert.AreNotEqual(firstScope, GatewayTransport.CreateDirectRouteScope(reconnect!, reconnectTicket));
        Assert.AreNotEqual(
            firstScope,
            GatewayTransport.CreateDirectRouteScope(anotherUser!, anotherUserTicket));
        Assert.AreNotEqual(
            firstScope,
            GatewayTransport.CreateDirectRouteScope(anotherDevice!, anotherDeviceTicket));
        Assert.AreNotEqual(
            GatewayTransport.CreateDirectRouteScope(unbound!, unboundTicket),
            GatewayTransport.CreateDirectRouteScope(anotherUnbound!, anotherUnboundTicket));
        Assert.AreEqual(32, first!.DeviceBindingHash.Length);
        Assert.IsTrue(firstScope.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));

        var firstResourceTicket = store.IssueHlsResource(
            firstTicket, firstTicket, first!, new Uri("https://source.invalid/segment-one.ts"), out _);
        var secondResourceTicket = store.IssueHlsResource(
            firstTicket, firstTicket, first!, new Uri("https://source.invalid/segment-two.ts"), out _);
        Assert.IsTrue(store.TryInspect(firstResourceTicket, out var firstResource));
        Assert.IsTrue(store.TryInspect(secondResourceTicket, out var secondResource));
        Assert.AreNotEqual(
            GatewayTransport.CreateDirectRouteScope(firstResource!, firstResourceTicket),
            GatewayTransport.CreateDirectRouteScope(secondResource!, secondResourceTicket));
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
