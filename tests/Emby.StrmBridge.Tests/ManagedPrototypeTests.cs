using Emby.StrmBridge.Api;
using Emby.StrmBridge.Managed;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class ManagedPrototypeTests
{
    private const string RoutePrefix = "/StrmBridge/Managed/v1/";

    [TestMethod]
    public void Create_UsesOpaqueBoundedRouteWithoutPersistingSourceMaterial()
    {
        var clock = new ManualClock();
        using var registry = new ManagedPrototypeRegistry(clock);

        var registration = registry.Create(
            "https://source.invalid/entry?opaque=long-source-value",
            ".MKV");
        var routeValue = registration.RelativePath.Substring(RoutePrefix.Length);

        Assert.StartsWith(RoutePrefix, registration.RelativePath, StringComparison.Ordinal);
        Assert.IsFalse(registration.RelativePath.Contains("source.invalid", StringComparison.Ordinal));
        Assert.IsFalse(registration.RelativePath.Contains("opaque", StringComparison.Ordinal));
        Assert.IsTrue(ManagedPrototypeRegistry.TryParseRouteValue(routeValue, out var token, out var hint));
        Assert.HasCount(43, token);
        Assert.AreEqual("mkv", hint);
        Assert.AreEqual(clock.UtcNow + ManagedPrototypeRegistry.EntryLifetime, registration.ExpiresAtUtc);
    }

    [TestMethod]
    public void Create_RejectsUnsafeSourceAndUnknownContainerHint()
    {
        using var registry = new ManagedPrototypeRegistry(new ManualClock());

        var sourceFailure = Assert.ThrowsExactly<ManagedPrototypeRegistrationException>(() =>
            registry.Create("https://user:secret@source.invalid/entry", "mkv"));
        var hintFailure = Assert.ThrowsExactly<ManagedPrototypeRegistrationException>(() =>
            registry.Create("https://source.invalid/entry", "unknown"));

        Assert.AreEqual(ManagedPrototypeRegistrationFailure.InvalidSource, sourceFailure.Reason);
        Assert.AreEqual(ManagedPrototypeRegistrationFailure.InvalidContainerHint, hintFailure.Reason);
    }

    [TestMethod]
    public void Registry_EnforcesCapacityAndExpiresRecords()
    {
        var clock = new ManualClock();
        using var registry = new ManagedPrototypeRegistry(clock);
        for (var index = 0; index < ManagedPrototypeRegistry.MaximumEntries; index++)
            registry.Create($"https://source-{index}.invalid/entry", null);

        var capacityFailure = Assert.ThrowsExactly<ManagedPrototypeRegistrationException>(() =>
            registry.Create("https://overflow.invalid/entry", null));
        Assert.AreEqual(ManagedPrototypeRegistrationFailure.CapacityExceeded, capacityFailure.Reason);

        clock.Advance(ManagedPrototypeRegistry.EntryLifetime);
        Assert.AreEqual(0, registry.Count);
        Assert.AreEqual(RoutePrefix.Length + 43, registry.Create("https://fresh.invalid/entry", null).RelativePath.Length);
    }

    [TestMethod]
    public async Task Resolve_UsesConsumerUserAgentAndReturnsOnlyApprovedRedirect()
    {
        var clock = new ManualClock();
        using var registry = new ManagedPrototypeRegistry(clock);
        var registration = registry.Create("https://source.invalid/entry?opaque=durable-value", "mp4");
        var observedUserAgent = string.Empty;
        using var resolver = new RedirectResolver(
            new StubRedirectClient((_, _, userAgent, _) =>
            {
                observedUserAgent = userAgent;
                return Task.FromResult(new RedirectSourceResponse(
                    302,
                    "https://source.invalid/temporary-media?lease=short",
                    null));
            }),
            new RedirectPolicy(),
            clock);

        var lease = await registry.ResolveAsync(
            registration.RelativePath.Substring(RoutePrefix.Length),
            "SyntheticClient/1.0",
            resolver,
            CancellationToken.None);

        Assert.AreEqual("SyntheticClient/1.0", observedUserAgent);
        Assert.AreEqual("https://source.invalid/temporary-media?lease=short", lease.GetLocation());
    }

    [TestMethod]
    public async Task Resolve_RejectsDirectMediaWithoutReturningDurableSource()
    {
        var clock = new ManualClock();
        using var registry = new ManagedPrototypeRegistry(clock);
        var registration = registry.Create(
            "https://source.invalid/direct?opaque=long-lived-secret",
            null);
        using var resolver = new RedirectResolver(
            new StubRedirectClient((_, _, _, _) =>
                Task.FromResult(new RedirectSourceResponse(206, null, null))),
            new RedirectPolicy(),
            clock);

        var failure = await Assert.ThrowsExactlyAsync<RedirectRejectedException>(() => registry.ResolveAsync(
            registration.RelativePath.Substring(RoutePrefix.Length),
            null,
            resolver,
            CancellationToken.None));

        Assert.AreEqual(RedirectRejectionReason.UnexpectedStatus, failure.Reason);
        Assert.IsFalse(failure.Message.Contains("long-lived-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Resolve_DoesNotPublishLeaseAfterRecordIsCleared()
    {
        var clock = new ManualClock();
        using var registry = new ManagedPrototypeRegistry(clock);
        var registration = registry.Create("https://source.invalid/entry", null);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resolver = new RedirectResolver(
            new StubRedirectClient(async (_, _, _, _) =>
            {
                started.TrySetResult(true);
                await release.Task;
                return new RedirectSourceResponse(302, "https://source.invalid/temporary", null);
            }),
            new RedirectPolicy(),
            clock);

        var resolution = registry.ResolveAsync(
            registration.RelativePath.Substring(RoutePrefix.Length),
            null,
            resolver,
            CancellationToken.None);
        await started.Task;
        Assert.AreEqual(1, registry.Clear());
        release.TrySetResult(true);

        await Assert.ThrowsExactlyAsync<ManagedPrototypeUnavailableException>(() => resolution);
    }

    [TestMethod]
    public void RouteParser_RejectsTamperingAndGatewayUsesCapabilityValidation()
    {
        using var registry = new ManagedPrototypeRegistry(new ManualClock());
        var registration = registry.Create("https://source.invalid/entry", "webm");
        var routeValue = registration.RelativePath.Substring(RoutePrefix.Length);

        Assert.IsFalse(ManagedPrototypeRegistry.TryParseRouteValue(routeValue + ".mp4", out _, out _));
        Assert.IsFalse(ManagedPrototypeRegistry.TryParseRouteValue(routeValue.Replace(".webm", ".exe"), out _, out _));
        Assert.IsTrue(Attribute.IsDefined(
            typeof(GetManagedStrmPrototype),
            typeof(UnauthenticatedAttribute),
            inherit: true));
    }

    [TestMethod]
    public void ManagementAuthorization_AcceptsBoundedServerIntegrationCredential()
    {
        Assert.IsTrue(ManagedPrototypeService.HasAuthenticatedServerToken(new AuthorizationInfo
        {
            Token = "synthetic-server-integration-key",
        }, CreateRequest(null)));
        Assert.IsTrue(ManagedPrototypeService.HasAuthenticatedServerToken(
            new AuthorizationInfo(),
            CreateRequest("synthetic-server-integration-key")));
        Assert.IsFalse(ManagedPrototypeService.HasAuthenticatedServerToken(
            new AuthorizationInfo(),
            CreateRequest(null)));
        Assert.IsFalse(Attribute.IsDefined(
            typeof(CreateManagedStrmPrototype),
            typeof(UnauthenticatedAttribute),
            inherit: true));
        Assert.IsFalse(Attribute.IsDefined(
            typeof(ClearManagedStrmPrototypes),
            typeof(UnauthenticatedAttribute),
            inherit: true));
    }

    private static IRequest CreateRequest(string? serverToken)
    {
        var headers = new QueryParamCollection();
        if (serverToken is not null) headers.Add("X-Emby-Token", serverToken);
        return TestProxy.Create<IRequest>((method, _) => method.Name switch
        {
            "get_Headers" => headers,
            _ => TestDispatchProxy.DefaultValue(method.ReturnType),
        });
    }
}
