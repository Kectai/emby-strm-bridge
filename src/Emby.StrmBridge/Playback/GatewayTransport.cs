using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Policy;

namespace Emby.StrmBridge.Playback;

public sealed class GatewayTransport : IDisposable
{
    public static readonly TimeSpan RedirectLeaseLifetime = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumSourceBackoff = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaximumRetryAfterBackoff = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan SourceFailureRetention = TimeSpan.FromMinutes(5);
    public const int MaximumRedirectLeases = 4096;
    private static readonly string DefaultUserAgent =
        "Emby.StrmBridge/" + (typeof(GatewayTransport).Assembly.GetName().Version?.ToString(3) ?? "unknown");

    internal static string ProbeUserAgent => DefaultUserAgent;

    private readonly object sync = new();
    private readonly Dictionary<string, RedirectLeaseEntry> redirectLeases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> redirectLeaseGates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectRouteEntry> directRoutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectRouteGateEntry> directRouteGates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceAttemptEntry> sourceAttempts = new(StringComparer.Ordinal);
    private readonly HttpClient client;
    private readonly RedirectPolicy redirectPolicy;
    private readonly IClock clock;
    private CancellationTokenSource lifetime = new();
    private int activeRequests;
    private int redirectLeaseGeneration;
    private bool disposed;

    public GatewayTransport(RedirectPolicy redirectPolicy, IClock? clock = null)
    {
        this.redirectPolicy = redirectPolicy ?? throw new ArgumentNullException(nameof(redirectPolicy));
        this.clock = clock ?? new SystemClock();
        client = new HttpClient(PinnedHttpHandler.Create(redirectPolicy), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public int ActiveRequests
    {
        get { lock (sync) return activeRequests; }
    }

    internal Action<Uri>? RepresentationChanged { get; set; }

    internal static string ResourceDigest(Uri source)
    {
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(source.AbsoluteUri)));
    }

    public int RedirectLeaseCount
    {
        get { lock (sync) return redirectLeases.Count; }
    }

    internal int DirectRouteCount
    {
        get { lock (sync) return directRoutes.Count; }
    }

    internal int DirectRouteGateCount
    {
        get { lock (sync) return directRouteGates.Count; }
    }

    internal int DirectRouteGateReferenceCount
    {
        get { lock (sync) return directRouteGates.Values.Sum(entry => entry.ReferenceCount); }
    }

    internal int SourceBackoffCount
    {
        get
        {
            lock (sync)
            {
                var now = clock.UtcNow;
                return sourceAttempts.Values.Count(entry => now < entry.BlockedUntilUtc);
            }
        }
    }

    public Uri ValidateResource(Uri source, Uri target) =>
        redirectPolicy.Validate(source, target.AbsoluteUri);

    internal void MarkHlsManifest(GatewayTransportLease lease) => lease.MarkHlsManifest();

    private void BindLeaseClassifications(GatewayTransportLease lease, params string[] keys)
    {
        var entries = new List<KeyValuePair<string, RedirectLeaseEntry>>();
        lock (sync)
        {
            if (disposed || lease.CacheGeneration != redirectLeaseGeneration) return;
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
                if (key.Length > 0 && redirectLeases.TryGetValue(key, out var entry) &&
                    entry.EffectiveUri == lease.EffectiveUri)
                    entries.Add(new KeyValuePair<string, RedirectLeaseEntry>(key, entry));
        }
        lease.OwnHlsClassification(() =>
        {
            lock (sync)
            {
                if (disposed || lease.CacheGeneration != redirectLeaseGeneration) return;
                foreach (var entry in entries)
                    if (redirectLeases.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry.Value))
                        current.IsHlsManifest = true;
            }
        });
    }

    internal static string CreateRedirectCandidateScope(SourceIdentity source)
    {
        if (source is null || source.SourceFingerprint.Length < 32)
            throw new ArgumentException("The source identity is unavailable.", nameof(source));
        return "source-" + source.SourceFingerprint.Substring(0, 32);
    }

    internal static string CreateDirectRouteScope(TicketPayload ticket, string ticketValue)
    {
        if (ticket is null) throw new ArgumentNullException(nameof(ticket));
        if (string.IsNullOrWhiteSpace(ticketValue)) throw new ArgumentException("The ticket is unavailable.", nameof(ticketValue));
        using var hash = SHA256.Create();
        var input = Encoding.UTF8.GetBytes(
            ((int)ticket.Scope).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
            ticket.HlsDepth.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
            ticket.ItemId.ToString("N") + "\n" +
            ticket.MediaSourceId + "\n" +
            ticket.Source.SourceFingerprint + "\n" +
            ticket.UpstreamUri.AbsoluteUri + "\n" +
            Convert.ToBase64String(ticket.UserBindingHash) + "\n" +
            Convert.ToBase64String(ticket.DeviceBindingHash) + "\n" +
            ticketValue);
        var digest = Convert.ToBase64String(hash.ComputeHash(input))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "direct-" + digest;
    }

    internal async Task<DirectRouteGateLease?> AcquireDirectRouteGateAsync(
        string scope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        CancellationToken cancellationToken)
    {
        var key = CreateRedirectLeaseKey(
            scope,
            NormalizeMethod(method),
            NormalizeUserAgent(userAgent),
            requestHeaders);
        if (key.Length == 0) return null;

        DirectRouteGateEntry entry;
        int generation;
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GatewayTransport));
            generation = redirectLeaseGeneration;
            if (!directRouteGates.TryGetValue(key, out entry!))
            {
                if (directRouteGates.Count >= MaximumRedirectLeases) throw new GatewayCapacityException();
                entry = new DirectRouteGateEntry();
                directRouteGates.Add(key, entry);
            }
            entry.ReferenceCount++;
        }

        var acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            lock (sync)
                if (disposed || generation != redirectLeaseGeneration)
                    throw new InvalidOperationException("The direct-route generation has changed.");
            return new DirectRouteGateLease(
                generation,
                () => ReleaseDirectRouteGate(key, entry, acquired: true));
        }
        catch
        {
            ReleaseDirectRouteGate(key, entry, acquired);
            throw;
        }
    }

    internal bool TryGetDirectRoute(
        Uri source,
        string scope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        out DirectRouteDecision decision)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        decision = default;
        ThrowIfSourceBackedOff(CreateSourceStateKey(
            source,
            NormalizeMethod(method),
            NormalizeUserAgent(userAgent)));
        var key = CreateRedirectLeaseKey(
            scope,
            NormalizeMethod(method),
            NormalizeUserAgent(userAgent),
            requestHeaders);
        if (key.Length == 0) return false;

        DirectRouteEntry? found;
        int generation;
        lock (sync)
        {
            if (disposed) return false;
            generation = redirectLeaseGeneration;
            if (!directRoutes.TryGetValue(key, out found)) return false;
            if (clock.UtcNow >= found.ExpiresAtUtc)
            {
                directRoutes.Remove(key);
                return false;
            }
        }

        if (found!.ResolveSourceRedirect)
        {
            lock (sync)
            {
                if (disposed || generation != redirectLeaseGeneration ||
                    !directRoutes.TryGetValue(key, out var current) || !ReferenceEquals(current, found) ||
                    clock.UtcNow >= current.ExpiresAtUtc)
                    return false;
                decision = DirectRouteDecision.SourceRedirect(current.Behavior, current.ExpiresAtUtc);
                return true;
            }
        }

        Uri validated;
        try { validated = redirectPolicy.Validate(source, found.EffectiveUri!.AbsoluteUri); }
        catch (RedirectRejectedException)
        {
            lock (sync)
                if (directRoutes.TryGetValue(key, out var current) && ReferenceEquals(current, found))
                    directRoutes.Remove(key);
            return false;
        }

        lock (sync)
        {
            if (disposed || generation != redirectLeaseGeneration ||
                !directRoutes.TryGetValue(key, out var current) || !ReferenceEquals(current, found) ||
                clock.UtcNow >= current.ExpiresAtUtc)
                return false;
            decision = DirectRouteDecision.Redirect(validated, current.Behavior, current.ExpiresAtUtc);
            return true;
        }
    }

    internal void RememberDirectRedirect(
        Uri source,
        string scope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        GatewayTransportLease lease,
        SourceTransportBehavior behavior)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (lease is null) throw new ArgumentNullException(nameof(lease));
        if (lease.IsRedirectHandoff)
            throw new ArgumentException("A source redirect handoff cannot establish a classified route.", nameof(lease));
        var validated = lease.RedirectCount == 0
            ? redirectPolicy.Validate(source, lease.EffectiveUri.AbsoluteUri)
            : null;
        var key = CreateRedirectLeaseKey(
            scope,
            NormalizeMethod(method),
            NormalizeUserAgent(userAgent),
            requestHeaders);
        if (key.Length == 0) return;
        StoreDirectRoute(
            key,
            new DirectRouteEntry(
                validated,
                behavior,
                resolveSourceRedirect: lease.RedirectCount > 0,
                CreateSourceStateKey(
                    source,
                    NormalizeMethod(method),
                    NormalizeUserAgent(userAgent)),
                clock.UtcNow + RedirectLeaseLifetime),
            lease.CacheGeneration);
    }

    internal void RememberDirectSourceRedirect(
        Uri source,
        string scope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        SourceTransportBehavior behavior,
        int expectedGeneration)
        => RememberDirectSourceRedirect(
            source,
            scope,
            method,
            userAgent,
            requestHeaders,
            effectiveUri: null,
            behavior,
            TimeSpan.Zero,
            expectedGeneration);

    internal void RememberDirectSourceRedirect(
        Uri source,
        string scope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        Uri? effectiveUri,
        SourceTransportBehavior behavior,
        TimeSpan cacheLifetime,
        int expectedGeneration)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (behavior != SourceTransportBehavior.FileBody)
            throw new ArgumentOutOfRangeException(nameof(behavior));
        if (cacheLifetime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cacheLifetime));
        var cacheTarget = cacheLifetime > TimeSpan.Zero;
        var validated = cacheTarget
            ? redirectPolicy.Validate(
                source,
                (effectiveUri ?? throw new ArgumentNullException(nameof(effectiveUri))).AbsoluteUri)
            : null;
        var normalizedMethod = NormalizeMethod(method);
        var normalizedUserAgent = NormalizeUserAgent(userAgent);
        var key = CreateRedirectLeaseKey(
            scope,
            normalizedMethod,
            normalizedUserAgent,
            requestHeaders);
        if (key.Length == 0) return;
        StoreDirectRoute(
            key,
            new DirectRouteEntry(
                validated,
                behavior,
                resolveSourceRedirect: !cacheTarget,
                CreateSourceStateKey(source, normalizedMethod, normalizedUserAgent),
                clock.UtcNow + (cacheTarget ? cacheLifetime : RedirectLeaseLifetime)),
            expectedGeneration);
    }

    internal void ForgetDirectRouteState(
        string scope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        int expectedGeneration)
    {
        var key = CreateRedirectLeaseKey(
            scope,
            NormalizeMethod(method),
            NormalizeUserAgent(userAgent),
            requestHeaders);
        if (key.Length == 0) return;
        lock (sync)
        {
            if (disposed || expectedGeneration != redirectLeaseGeneration) return;
            directRoutes.Remove(key);
            redirectLeases.Remove(key);
            if (redirectLeaseGates.TryGetValue(key, out var gate) && gate.CurrentCount == 1)
                redirectLeaseGates.Remove(key);
        }
    }

    public async Task<GatewayTransportLease> OpenAsync(
        Uri source,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        CancellationToken cancellationToken) =>
        await OpenAsync(source, null, method, userAgent, requestHeaders, options, cancellationToken)
            .ConfigureAwait(false);

    public async Task<GatewayTransportLease> OpenAsync(
        Uri source,
        string? redirectLeaseScope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        CancellationToken cancellationToken) =>
        await OpenAsync(
                source,
                redirectLeaseScope,
                null,
                method,
                userAgent,
                requestHeaders,
                options,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<GatewayTransportLease> OpenAsync(
        Uri source,
        string? redirectLeaseScope,
        string? redirectCandidateScope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        CancellationToken cancellationToken,
        CancellationToken headerCancellation = default)
        => await OpenCoreAsync(
                source,
                redirectLeaseScope,
                redirectCandidateScope,
                method,
                userAgent,
                requestHeaders,
                options,
                isProbe: false,
                expectedGeneration: null,
                stopAtFirstRedirect: false,
                cancellationToken,
                headerCancellation)
            .ConfigureAwait(false);

    internal async Task<GatewayTransportLease> OpenDirectAsync(
        Uri source,
        string redirectLeaseScope,
        int expectedGeneration,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        CancellationToken cancellationToken,
        CancellationToken headerCancellation = default)
        => await OpenCoreAsync(
                source,
                redirectLeaseScope,
                null,
                method,
                userAgent,
                requestHeaders,
                options,
                isProbe: false,
                expectedGeneration,
                stopAtFirstRedirect: false,
                cancellationToken,
                headerCancellation)
            .ConfigureAwait(false);

    internal async Task<GatewayTransportLease> OpenSourceRedirectHandoffAsync(
        Uri source,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        CancellationToken cancellationToken,
        CancellationToken headerCancellation = default)
        => await OpenCoreAsync(
                source,
                null,
                null,
                method,
                userAgent,
                requestHeaders,
                options,
                isProbe: false,
                expectedGeneration: null,
                stopAtFirstRedirect: true,
                cancellationToken,
                headerCancellation)
            .ConfigureAwait(false);

    internal async Task<GatewayTransportLease> OpenProbeAsync(
        Uri source,
        string? redirectLeaseScope,
        string? redirectCandidateScope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        CancellationToken cancellationToken,
        CancellationToken headerCancellation = default)
        => await OpenCoreAsync(
                source,
                redirectLeaseScope,
                redirectCandidateScope,
                method,
                userAgent,
                requestHeaders,
                options,
                isProbe: true,
                expectedGeneration: null,
                stopAtFirstRedirect: false,
                cancellationToken,
                headerCancellation)
            .ConfigureAwait(false);

    private async Task<GatewayTransportLease> OpenCoreAsync(
        Uri source,
        string? redirectLeaseScope,
        string? redirectCandidateScope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        bool isProbe,
        int? expectedGeneration,
        bool stopAtFirstRedirect,
        CancellationToken cancellationToken,
        CancellationToken headerCancellation)
    {
        CancellationTokenSource requestLifetime;
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GatewayTransport));
            requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        }
        try
        {
            var lease = await OpenRequestAsync(
                    source, redirectLeaseScope, redirectCandidateScope, method, userAgent,
                    requestHeaders, options, isProbe, expectedGeneration, stopAtFirstRedirect,
                    requestLifetime.Token,
                    headerCancellation)
                .ConfigureAwait(false);
            lease.OwnCancellation(requestLifetime);
            return lease;
        }
        catch
        {
            requestLifetime.Dispose();
            throw;
        }
    }

    private async Task<GatewayTransportLease> OpenRequestAsync(
        Uri source,
        string? redirectLeaseScope,
        string? redirectCandidateScope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        bool isProbe,
        int? expectedGeneration,
        bool stopAtFirstRedirect,
        CancellationToken cancellationToken,
        CancellationToken headerCancellation)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (options is null) throw new ArgumentNullException(nameof(options));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, headerCancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.GatewayTimeoutSeconds));
        var normalizedUserAgent = NormalizeUserAgent(userAgent);
        var normalizedMethod = NormalizeMethod(method);
        var sourceStateKey = CreateSourceStateKey(
            source,
            normalizedMethod,
            normalizedUserAgent);
        using var sourceAttempt = await AcquireSourceAttemptAsync(sourceStateKey, timeout.Token)
            .ConfigureAwait(false);
        var entered = false;
        var leaseGeneration = 0;
        SemaphoreSlim? resolutionGate = null;
        var resolutionGateHeld = false;
        try
        {
            leaseGeneration = Enter(options.RelayConcurrency, isProbe, expectedGeneration);
            entered = true;
            if (stopAtFirstRedirect)
            {
                var firstHop = await SendFollowingRedirectsAsync(
                        source,
                        0,
                        normalizedMethod,
                        normalizedUserAgent,
                        requestHeaders,
                        options,
                        timeout.Token,
                        stopAtFirstRedirect: true)
                    .ConfigureAwait(false);
                ObserveSourceResponse(sourceAttempt, firstHop.Response);
                return new GatewayTransportLease(
                    firstHop.Response,
                    firstHop.EffectiveUri,
                    firstHop.RedirectCount,
                    usedCachedRedirect: false,
                    retriedRejectedRedirect: false,
                    leaseGeneration,
                    clock.UtcNow,
                    cancellationToken,
                    TimeSpan.FromSeconds(options.GatewayTimeoutSeconds),
                    Exit,
                    firstHop.IsRedirectHandoff);
            }
            var leaseKey = CreateRedirectLeaseKey(
                redirectLeaseScope,
                normalizedMethod,
                normalizedUserAgent,
                requestHeaders);
            var knownHlsManifest = leaseKey.Length > 0 &&
                TryGetRedirectLease(leaseKey, out var previousLease) && previousLease!.IsHlsManifest;
            var candidateKey = CreateRedirectCandidateKey(
                source,
                redirectCandidateScope,
                normalizedMethod,
                normalizedUserAgent,
                requestHeaders);
            var hasRequestedRange = TryGetSingleRange(
                requestHeaders,
                out var requestedRangeStart,
                out var requestedRangeEnd);
            Func<HttpResponseMessage, bool> rangeResponseValidator = response =>
                IsPartialRangeResponseCompatible(response, requestHeaders) &&
                (!hasRequestedRange || IsRedirectRangeResponseCompatible(
                    response, requestedRangeStart, requestedRangeEnd));
            var resolutionGateKey = candidateKey.Length > 0 ? candidateKey : leaseKey;
            GatewayTransportLease? cachedLease;
            var rejectedCachedRedirect = false;
            try
            {
                if (leaseKey.Length > 0)
                {
                    cachedLease = await TryOpenRedirectLeaseAsync(
                            source, leaseKey, leaseGeneration, normalizedMethod, normalizedUserAgent,
                            requestHeaders, options, cancellationToken, timeout.Token, rangeResponseValidator,
                            () => rejectedCachedRedirect = true)
                        .ConfigureAwait(false);
                    if (cachedLease is not null)
                    {
                        ObserveSourceResponse(sourceAttempt, cachedLease.Response);
                        return cachedLease;
                    }
                }

                if (candidateKey.Length > 0 && !string.Equals(candidateKey, leaseKey, StringComparison.Ordinal))
                {
                    var candidateLease = await TryOpenRedirectLeaseAsync(
                            source, candidateKey, leaseGeneration, normalizedMethod, normalizedUserAgent,
                            requestHeaders, options, cancellationToken, timeout.Token,
                            rangeResponseValidator,
                            () => rejectedCachedRedirect = true)
                        .ConfigureAwait(false);
                    if (candidateLease is not null)
                    {
                        if (leaseKey.Length > 0)
                            StoreRedirectLease(
                                leaseKey,
                                candidateLease.EffectiveUri,
                                candidateLease.RedirectCount,
                                leaseGeneration,
                                candidateLease.Response,
                                candidateLease.RedirectExpiresAtUtc);
                        BindLeaseClassifications(candidateLease, leaseKey);
                        ObserveSourceResponse(sourceAttempt, candidateLease.Response);
                        return candidateLease;
                    }
                }

                if (resolutionGateKey.Length > 0)
                {
                    resolutionGate = GetRedirectLeaseGate(resolutionGateKey);
                    if (resolutionGate is not null)
                    {
                        await resolutionGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                        resolutionGateHeld = true;
                        if (leaseKey.Length > 0)
                        {
                            cachedLease = await TryOpenRedirectLeaseAsync(
                                    source, leaseKey, leaseGeneration, normalizedMethod, normalizedUserAgent,
                                    requestHeaders, options, cancellationToken, timeout.Token, rangeResponseValidator,
                                    () => rejectedCachedRedirect = true)
                                .ConfigureAwait(false);
                            if (cachedLease is not null)
                            {
                                ObserveSourceResponse(sourceAttempt, cachedLease.Response);
                                return cachedLease;
                            }
                        }
                        if (candidateKey.Length > 0 &&
                            !string.Equals(candidateKey, leaseKey, StringComparison.Ordinal))
                        {
                            var candidateLease = await TryOpenRedirectLeaseAsync(
                                    source, candidateKey, leaseGeneration, normalizedMethod, normalizedUserAgent,
                                    requestHeaders, options, cancellationToken, timeout.Token, rangeResponseValidator,
                                    () => rejectedCachedRedirect = true)
                                .ConfigureAwait(false);
                            if (candidateLease is not null)
                            {
                                if (leaseKey.Length > 0)
                                    StoreRedirectLease(
                                        leaseKey,
                                        candidateLease.EffectiveUri,
                                        candidateLease.RedirectCount,
                                        leaseGeneration,
                                        candidateLease.Response,
                                        candidateLease.RedirectExpiresAtUtc);
                                BindLeaseClassifications(candidateLease, leaseKey);
                                ObserveSourceResponse(sourceAttempt, candidateLease.Response);
                                return candidateLease;
                            }
                        }
                    }
                }

                var resolved = await SendFollowingRedirectsAsync(
                        source,
                        0,
                        normalizedMethod,
                        normalizedUserAgent,
                        requestHeaders,
                        options,
                        timeout.Token)
                    .ConfigureAwait(false);
                var retriedRejectedRedirect = rejectedCachedRedirect;
                if (!rejectedCachedRedirect &&
                    (resolved.RedirectCount > 0 && InvalidatesRedirectLease(resolved.Response) ||
                     !IsPartialRangeResponseCompatible(resolved.Response, requestHeaders)))
                {
                    resolved.Response.Dispose();
                    retriedRejectedRedirect = true;
                    resolved = await SendFollowingRedirectsAsync(
                            source,
                            0,
                            normalizedMethod,
                            normalizedUserAgent,
                            requestHeaders,
                            options,
                            timeout.Token)
                        .ConfigureAwait(false);
                }
                // Authoritative reads need the same range gate as cache hits. Never cache or
                // deliver a mismatched 206, even when the source has just issued a fresh URL.
                if (!IsPartialRangeResponseCompatible(resolved.Response, requestHeaders))
                {
                    resolved.Response.Dispose();
                    throw new HttpRequestException("The upstream partial response does not match the requested range.");
                }
                var redirectExpiresAtUtc = clock.UtcNow + RedirectLeaseLifetime;
                if (leaseKey.Length > 0 && resolved.RedirectCount > 0 &&
                    IsReusableRedirectLeaseResponse(resolved.Response))
                    StoreRedirectLease(
                        leaseKey,
                        resolved.EffectiveUri,
                        resolved.RedirectCount,
                        leaseGeneration,
                        resolved.Response,
                        redirectExpiresAtUtc);
                if (candidateKey.Length > 0 && resolved.RedirectCount > 0 &&
                    IsRedirectRangeResponseCompatible(
                        resolved.Response,
                        requestedRangeStart,
                        requestedRangeEnd))
                    StoreRedirectLease(
                        candidateKey,
                        resolved.EffectiveUri,
                        resolved.RedirectCount,
                        leaseGeneration,
                        resolved.Response,
                        redirectExpiresAtUtc);
                ObserveSourceResponse(sourceAttempt, resolved.Response);
                var resolvedLease = new GatewayTransportLease(
                    resolved.Response,
                    resolved.EffectiveUri,
                    resolved.RedirectCount,
                    usedCachedRedirect: false,
                    retriedRejectedRedirect,
                    leaseGeneration,
                    redirectExpiresAtUtc,
                    cancellationToken,
                    TimeSpan.FromSeconds(options.GatewayTimeoutSeconds),
                    Exit);
                BindLeaseClassifications(resolvedLease, leaseKey, candidateKey);
                if (knownHlsManifest) MarkHlsManifest(resolvedLease);
                return resolvedLease;
            }
            finally
            {
                if (resolutionGateHeld) ReleaseRedirectLeaseGate(resolutionGateKey, resolutionGate!);
            }
        }
        catch (HttpRequestException exception) when (
            !cancellationToken.IsCancellationRequested && !timeout.IsCancellationRequested &&
            RedirectPolicy.FindRejection(exception) is null)
        {
            ObserveSourceFailure(sourceAttempt, 502, null);
            if (entered) Exit();
            throw;
        }
        catch
        {
            if (entered) Exit();
            throw;
        }
    }

    public void Clear()
    {
        CancellationTokenSource previous;
        lock (sync)
        {
            if (disposed) return;
            previous = lifetime;
            lifetime = new CancellationTokenSource();
            redirectLeaseGeneration++;
            redirectLeases.Clear();
            redirectLeaseGates.Clear();
            directRoutes.Clear();
            ResetSourceAttemptsUnsafe();
        }
        previous.Cancel();
        previous.Dispose();
    }

    public int RemoveExpiredRedirectLeases()
    {
        lock (sync)
        {
            var now = clock.UtcNow;
            return RemoveExpiredRedirectLeasesUnsafe(now) + RemoveExpiredDirectRoutesUnsafe(now) +
                   RemoveExpiredSourceAttemptsUnsafe(now);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource previous;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            previous = lifetime;
            redirectLeaseGeneration++;
            redirectLeases.Clear();
            redirectLeaseGates.Clear();
            directRoutes.Clear();
            ResetSourceAttemptsUnsafe();
        }
        previous.Cancel();
        previous.Dispose();
        client.Dispose();
    }

    private async Task<SourceAttemptLease> AcquireSourceAttemptAsync(
        string key,
        CancellationToken cancellationToken)
    {
        SourceAttemptEntry entry;
        int generation;
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GatewayTransport));
            generation = redirectLeaseGeneration;
            var now = clock.UtcNow;
            RemoveExpiredSourceAttemptsUnsafe(now);
            if (!sourceAttempts.TryGetValue(key, out entry!))
            {
                EnsureSourceAttemptCapacityUnsafe();
                entry = new SourceAttemptEntry(now);
                sourceAttempts.Add(key, entry);
            }
            entry.ReferenceCount++;
            entry.LastTouchedUtc = now;
        }

        var acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            lock (sync)
            {
                if (disposed || generation != redirectLeaseGeneration)
                    throw new InvalidOperationException("The source-attempt generation has changed.");
                var now = clock.UtcNow;
                entry.LastTouchedUtc = now;
                if (now < entry.BlockedUntilUtc)
                    throw CreateSourceBackoffException(entry, now);
            }
            return new SourceAttemptLease(
                generation,
                key,
                entry,
                () => ReleaseSourceAttempt(key, entry, acquired: true));
        }
        catch
        {
            ReleaseSourceAttempt(key, entry, acquired);
            throw;
        }
    }

    private void ObserveSourceResponse(SourceAttemptLease attempt, HttpResponseMessage response)
    {
        if (attempt is null) throw new ArgumentNullException(nameof(attempt));
        if (response is null) throw new ArgumentNullException(nameof(response));
        ObserveSourceFailure(attempt, (int)response.StatusCode, GetRetryAfterBackoff(response, clock.UtcNow));
    }

    private void ObserveSourceFailure(SourceAttemptLease attempt, int statusCode, TimeSpan? retryAfter)
    {
        lock (sync)
        {
            if (disposed || attempt.Generation != redirectLeaseGeneration ||
                !sourceAttempts.TryGetValue(attempt.Key, out var current) ||
                !ReferenceEquals(current, attempt.Entry))
                return;
            var now = clock.UtcNow;
            current.LastTouchedUtc = now;
            if (!RequiresSourceBackoff(statusCode))
            {
                current.FailureCount = 0;
                current.StatusCode = 0;
                current.BlockedUntilUtc = DateTimeOffset.MinValue;
                current.RetainUntilUtc = DateTimeOffset.MinValue;
                return;
            }

            current.FailureCount = Math.Min(current.FailureCount + 1, 30);
            var duration = retryAfter ??
                           GetDefaultSourceBackoff(current.FailureCount);
            current.StatusCode = statusCode;
            current.BlockedUntilUtc = now + duration;
            current.RetainUntilUtc = current.BlockedUntilUtc + SourceFailureRetention;
            RemoveDirectRoutesForSourceUnsafe(attempt.Key);
        }
    }

    private int RemoveDirectRoutesForSourceUnsafe(string sourceStateKey)
    {
        var matching = directRoutes
            .Where(pair => string.Equals(
                pair.Value.SourceStateKey,
                sourceStateKey,
                StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in matching) directRoutes.Remove(key);
        return matching.Length;
    }

    private void ThrowIfSourceBackedOff(string key)
    {
        lock (sync)
        {
            if (disposed) return;
            if (!sourceAttempts.TryGetValue(key, out var entry)) return;
            var now = clock.UtcNow;
            if (now < entry.BlockedUntilUtc) throw CreateSourceBackoffException(entry, now);
        }
    }

    private static GatewaySourceBackoffException CreateSourceBackoffException(
        SourceAttemptEntry entry,
        DateTimeOffset now)
    {
        var remaining = entry.BlockedUntilUtc - now;
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return new GatewaySourceBackoffException(entry.StatusCode, seconds);
    }

    private static bool RequiresSourceBackoff(int statusCode) =>
        statusCode is 401 or 403 or 404 or 408 or 410 or 429 || statusCode >= 500;

    private static TimeSpan GetDefaultSourceBackoff(int failureCount)
    {
        var exponent = Math.Min(Math.Max(failureCount - 1, 0), 4);
        var seconds = Math.Min(2 << exponent, (int)MaximumSourceBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan? GetRetryAfterBackoff(HttpResponseMessage response, DateTimeOffset now)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;
        var duration = retryAfter.Delta ??
                       (retryAfter.Date.HasValue ? retryAfter.Date.Value - now : TimeSpan.Zero);
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromSeconds(1);
        return duration <= MaximumRetryAfterBackoff ? duration : MaximumRetryAfterBackoff;
    }

    private void EnsureSourceAttemptCapacityUnsafe()
    {
        if (sourceAttempts.Count < MaximumRedirectLeases) return;
        var removable = sourceAttempts
            .Where(pair => pair.Value.ReferenceCount == 0)
            .OrderBy(pair => pair.Value.LastTouchedUtc)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(removable.Key)) throw new GatewayCapacityException();
        sourceAttempts.Remove(removable.Key);
        removable.Value.Semaphore.Dispose();
    }

    private int RemoveExpiredSourceAttemptsUnsafe(DateTimeOffset now)
    {
        var expired = sourceAttempts
            .Where(pair => pair.Value.ReferenceCount == 0 &&
                           (pair.Value.FailureCount == 0 || now >= pair.Value.RetainUntilUtc))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired)
        {
            var entry = sourceAttempts[key];
            sourceAttempts.Remove(key);
            entry.Semaphore.Dispose();
        }
        return expired.Length;
    }

    private void ResetSourceAttemptsUnsafe()
    {
        foreach (var pair in sourceAttempts.ToArray())
        {
            var entry = pair.Value;
            entry.FailureCount = 0;
            entry.StatusCode = 0;
            entry.BlockedUntilUtc = DateTimeOffset.MinValue;
            entry.RetainUntilUtc = DateTimeOffset.MinValue;
            if (entry.ReferenceCount != 0) continue;
            sourceAttempts.Remove(pair.Key);
            entry.Semaphore.Dispose();
        }
    }

    private void ReleaseSourceAttempt(string key, SourceAttemptEntry entry, bool acquired)
    {
        if (acquired) entry.Semaphore.Release();
        var dispose = false;
        lock (sync)
        {
            if (entry.ReferenceCount > 0) entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 &&
                sourceAttempts.TryGetValue(key, out var current) && ReferenceEquals(current, entry) &&
                (entry.FailureCount == 0 || clock.UtcNow >= entry.RetainUntilUtc))
            {
                sourceAttempts.Remove(key);
                dispose = true;
            }
        }
        if (dispose) entry.Semaphore.Dispose();
    }

    private int Enter(int limit, bool isProbe, int? expectedGeneration)
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GatewayTransport));
            if (expectedGeneration.HasValue && expectedGeneration.Value != redirectLeaseGeneration)
                throw new InvalidOperationException("The direct-route generation has changed.");
            var effectiveLimit = isProbe && limit > 1 ? limit - 1 : limit;
            if (activeRequests >= effectiveLimit) throw new GatewayCapacityException();
            activeRequests++;
            return redirectLeaseGeneration;
        }
    }

    private void Exit()
    {
        lock (sync)
        {
            if (activeRequests > 0) activeRequests--;
        }
    }

    private static HttpRequestMessage CreateRequest(
        Uri source,
        string method,
        string userAgent,
        IReadOnlyDictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(
            string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Head : HttpMethod.Get,
            source);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        foreach (var header in headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return request;
    }

    private static string NormalizeUserAgent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultUserAgent;
        var normalized = value.Trim();
        return normalized.Length <= 256 && normalized.IndexOfAny(new[] { '\r', '\n' }) < 0
            ? normalized
            : DefaultUserAgent;
    }

    private static string NormalizeMethod(string value) =>
        string.Equals(value, "HEAD", StringComparison.OrdinalIgnoreCase) ? "HEAD" : "GET";

    private static bool IsRedirect(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return status is 301 or 302 or 303 or 307 or 308;
    }

    private async Task<TransportResponse> SendFollowingRedirectsAsync(
        Uri start,
        int priorRedirectCount,
        string method,
        string normalizedUserAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        CancellationToken cancellationToken,
        bool stopAtFirstRedirect = false)
    {
        var current = start;
        var redirects = priorRedirectCount;
        while (true)
        {
            using var request = CreateRequest(current, method, normalizedUserAgent, requestHeaders);
            var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsRedirect(response)) return new TransportResponse(response, current, redirects, false);
            if (redirects >= options.RedirectHopLimit)
            {
                response.Dispose();
                throw new RedirectRejectedException(RedirectRejectionReason.TooManyRedirects);
            }
            var location = response.Headers.Location?.OriginalString;
            Uri target;
            try { target = redirectPolicy.Validate(current, location); }
            catch
            {
                response.Dispose();
                throw;
            }
            redirects++;
            if (stopAtFirstRedirect) return new TransportResponse(response, target, redirects, true);
            response.Dispose();
            current = target;
        }
    }

    private bool TryGetRedirectLease(string key, out RedirectLeaseEntry? entry)
    {
        lock (sync)
        {
            entry = null;
            if (!redirectLeases.TryGetValue(key, out var found)) return false;
            if (clock.UtcNow >= found.ExpiresAtUtc)
            {
                redirectLeases.Remove(key);
                return false;
            }
            entry = found;
            return true;
        }
    }

    private void StoreRedirectLease(
        string key,
        Uri effectiveUri,
        int redirectCount,
        int expectedGeneration,
        HttpResponseMessage response,
        DateTimeOffset? expiresAtUtc = null)
    {
        lock (sync)
        {
            if (disposed || expectedGeneration != redirectLeaseGeneration) return;
            var now = clock.UtcNow;
            RemoveExpiredRedirectLeasesUnsafe(now);
            if (!redirectLeases.ContainsKey(key) && redirectLeases.Count >= MaximumRedirectLeases)
            {
                var oldest = redirectLeases.OrderBy(pair => pair.Value.ExpiresAtUtc).First();
                redirectLeases.Remove(oldest.Key);
                if (redirectLeaseGates.TryGetValue(oldest.Key, out var gate) && gate.CurrentCount == 1)
                    redirectLeaseGates.Remove(oldest.Key);
            }
            redirectLeases[key] = new RedirectLeaseEntry(
                effectiveUri,
                redirectCount,
                expiresAtUtc ?? now + RedirectLeaseLifetime,
                response);
        }
    }

    private void RemoveRedirectLease(string key)
    {
        lock (sync) redirectLeases.Remove(key);
    }

    private int RemoveExpiredRedirectLeasesUnsafe(DateTimeOffset now)
    {
        var expired = redirectLeases
            .Where(pair => now >= pair.Value.ExpiresAtUtc)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired)
        {
            redirectLeases.Remove(key);
            if (redirectLeaseGates.TryGetValue(key, out var gate) && gate.CurrentCount == 1)
                redirectLeaseGates.Remove(key);
        }
        return expired.Length;
    }

    private int RemoveExpiredDirectRoutesUnsafe(DateTimeOffset now)
    {
        var expired = directRoutes
            .Where(pair => now >= pair.Value.ExpiresAtUtc)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in expired) directRoutes.Remove(key);
        return expired.Length;
    }

    private void StoreDirectRoute(
        string key,
        DirectRouteEntry entry,
        int expectedGeneration)
    {
        lock (sync)
        {
            if (disposed || expectedGeneration != redirectLeaseGeneration) return;
            var now = clock.UtcNow;
            if (sourceAttempts.TryGetValue(entry.SourceStateKey, out var sourceState) &&
                now < sourceState.BlockedUntilUtc)
                return;
            redirectLeases.Remove(key);
            if (redirectLeaseGates.TryGetValue(key, out var redirectGate) && redirectGate.CurrentCount == 1)
                redirectLeaseGates.Remove(key);
            RemoveExpiredDirectRoutesUnsafe(now);
            if (!directRoutes.ContainsKey(key) && directRoutes.Count >= MaximumRedirectLeases)
            {
                var oldest = directRoutes.OrderBy(pair => pair.Value.ExpiresAtUtc).First();
                directRoutes.Remove(oldest.Key);
            }
            directRoutes[key] = entry;
        }
    }

    private void ReleaseDirectRouteGate(string key, DirectRouteGateEntry entry, bool acquired)
    {
        if (acquired) entry.Semaphore.Release();
        var dispose = false;
        lock (sync)
        {
            if (entry.ReferenceCount > 0) entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 &&
                directRouteGates.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                directRouteGates.Remove(key);
                dispose = true;
            }
        }
        if (dispose) entry.Semaphore.Dispose();
    }

    private SemaphoreSlim? GetRedirectLeaseGate(string key)
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GatewayTransport));
            if (redirectLeaseGates.TryGetValue(key, out var existing)) return existing;
            if (redirectLeaseGates.Count >= MaximumRedirectLeases) return null;
            var created = new SemaphoreSlim(1, 1);
            redirectLeaseGates.Add(key, created);
            return created;
        }
    }

    private void ReleaseRedirectLeaseGate(string key, SemaphoreSlim gate)
    {
        gate.Release();
        lock (sync)
        {
            if (!redirectLeases.ContainsKey(key) && gate.CurrentCount == 1 &&
                redirectLeaseGates.TryGetValue(key, out var current) && ReferenceEquals(current, gate))
                redirectLeaseGates.Remove(key);
        }
    }

    private async Task<GatewayTransportLease?> TryOpenRedirectLeaseAsync(
        Uri source,
        string leaseKey,
        int leaseGeneration,
        string method,
        string normalizedUserAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        CancellationToken requestCancellation,
        CancellationToken timeoutCancellation,
        Func<HttpResponseMessage, bool>? responseValidator = null,
        Action? rejectionObserver = null)
    {
        if (!TryGetRedirectLease(leaseKey, out var cached)) return null;
        try
        {
            var validated = redirectPolicy.Validate(source, cached!.EffectiveUri.AbsoluteUri);
            var cachedResponse = await SendFollowingRedirectsAsync(
                    validated,
                    0,
                    method,
                    normalizedUserAgent,
                    requestHeaders,
                    options,
                    timeoutCancellation)
                .ConfigureAwait(false);
            var isHlsManifest = cached.IsHlsManifest ||
                SourceBehaviorClassifier.Classify(cachedResponse.Response, cachedResponse.EffectiveUri) ==
                SourceTransportBehavior.HlsManifest;
            if (!isHlsManifest && IsReusableRedirectLeaseResponse(cachedResponse.Response) &&
                !cached.MatchesRepresentation(cachedResponse.Response))
            {
                cachedResponse.Response.Dispose();
                lock (sync)
                {
                    foreach (var key in redirectLeases.Where(pair => pair.Value.EffectiveUri == cached.EffectiveUri)
                                 .Select(pair => pair.Key).ToArray())
                        redirectLeases.Remove(key);
                    foreach (var key in directRoutes.Where(pair => pair.Value.EffectiveUri == cached.EffectiveUri)
                                 .Select(pair => pair.Key).ToArray())
                        directRoutes.Remove(key);
                }
                RepresentationChanged?.Invoke(source);
                throw new GatewayRepresentationChangedException();
            }
            if (IsReusableRedirectLeaseResponse(cachedResponse.Response) &&
                (responseValidator is null || responseValidator(cachedResponse.Response)))
            {
                var effectiveRedirectCount = Math.Max(cached.RedirectCount, cachedResponse.RedirectCount);
                var redirectExpiresAtUtc = clock.UtcNow + RedirectLeaseLifetime;
                StoreRedirectLease(
                    leaseKey,
                    cachedResponse.EffectiveUri,
                    effectiveRedirectCount,
                    leaseGeneration,
                    cachedResponse.Response,
                    redirectExpiresAtUtc);
                var lease = new GatewayTransportLease(
                    cachedResponse.Response,
                    cachedResponse.EffectiveUri,
                    effectiveRedirectCount,
                    usedCachedRedirect: true,
                    retriedRejectedRedirect: false,
                    leaseGeneration,
                    redirectExpiresAtUtc,
                    requestCancellation,
                    TimeSpan.FromSeconds(options.GatewayTimeoutSeconds),
                    Exit);
                BindLeaseClassifications(lease, leaseKey);
                if (isHlsManifest) MarkHlsManifest(lease);
                return lease;
            }
            cachedResponse.Response.Dispose();
        }
        catch (HttpRequestException)
        {
        }
        catch (RedirectRejectedException)
        {
        }
        catch (OperationCanceledException) when (!requestCancellation.IsCancellationRequested)
        {
            RemoveRedirectLease(leaseKey);
            throw;
        }
        rejectionObserver?.Invoke();
        RemoveRedirectLease(leaseKey);
        return null;
    }

    private static bool InvalidatesRedirectLease(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return status is 401 or 403 or 404 or 410;
    }

    private static bool IsReusableRedirectLeaseResponse(HttpResponseMessage response) =>
        TransportPlanner.CanHandoffRedirect((int)response.StatusCode);

    private static bool IsPartialRangeResponseCompatible(
        HttpResponseMessage response, IReadOnlyDictionary<string, string> headers)
    {
        // A server may ignore Range (200) or return a conditional response/416.
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent ||
            !RangeHeaderValue.TryParse(GetHeader(headers, "Range"), out var requested) ||
            requested.Ranges.Count != 1 || !string.Equals(requested.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
            return true;
        var range = requested.Ranges.Single();
        var actual = response.Content.Headers.ContentRange;
        if (actual?.Length is not > 0) return false;
        var start = range.From ?? Math.Max(0, actual.Length.Value - range.To.GetValueOrDefault());
        var end = range.From.HasValue ? range.To : actual.Length.Value - 1;
        return IsRedirectRangeResponseCompatible(response, start, end);
    }

    private static bool IsRedirectRangeResponseCompatible(
        HttpResponseMessage response,
        long requestedRangeStart,
        long? requestedRangeEnd)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent ||
            requestedRangeStart < 0)
            return false;
        var actual = response.Content.Headers.ContentRange;
        if (actual is null ||
            !string.Equals(actual.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            actual.From != requestedRangeStart || !actual.To.HasValue || actual.To < actual.From ||
            !actual.Length.HasValue || actual.Length <= actual.To ||
            response.Content.Headers.ContentEncoding.Any(value =>
                !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase)))
            return false;
        var expectedEnd = requestedRangeEnd.HasValue
            ? Math.Min(requestedRangeEnd.Value, actual.Length.Value - 1)
            : actual.Length.Value - 1;
        var responseLength = actual.To.Value - actual.From.Value + 1;
        return actual.To.Value == expectedEnd &&
               response.Content.Headers.ContentLength == responseLength;
    }

    private static bool TryGetSingleRange(
        IReadOnlyDictionary<string, string> headers,
        out long rangeStart,
        out long? rangeEnd)
    {
        rangeStart = -1;
        rangeEnd = null;
        var value = GetHeader(headers, "Range");
        if (!RangeHeaderValue.TryParse(value, out var parsed) || parsed.Ranges.Count != 1)
            return false;
        var requested = parsed.Ranges.Single();
        if (!requested.From.HasValue || requested.From.Value < 0 ||
            requested.To.HasValue && requested.To.Value < requested.From.Value)
            return false;
        rangeStart = requested.From.Value;
        rangeEnd = requested.To;
        return true;
    }

    private static string CreateRedirectLeaseKey(
        string? scope,
        string method,
        string normalizedUserAgent,
        IReadOnlyDictionary<string, string> headers)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 128 ||
            scope.Any(character =>
                !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
            return string.Empty;
        var accept = GetHeader(headers, "Accept");
        var language = GetHeader(headers, "Accept-Language");
        if (accept.Length > 4096 || language.Length > 4096) return string.Empty;
        using var hash = SHA256.Create();
        var input = Encoding.UTF8.GetBytes(
            scope + "\n" + method + "\n" + normalizedUserAgent + "\n" + accept + "\n" + language);
        return Convert.ToBase64String(hash.ComputeHash(input));
    }

    private static string CreateSourceStateKey(
        Uri source,
        string method,
        string normalizedUserAgent)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        using var hash = SHA256.Create();
        var input = Encoding.UTF8.GetBytes(
            "source-state\n" + source.AbsoluteUri + "\n" + method + "\n" + normalizedUserAgent);
        return Convert.ToBase64String(hash.ComputeHash(input));
    }

    private static string CreateRedirectCandidateKey(
        Uri resource,
        string? scope,
        string method,
        string normalizedUserAgent,
        IReadOnlyDictionary<string, string> headers)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 128 ||
            scope.Any(character =>
                !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
            return string.Empty;
        if (!TryGetSingleRange(headers, out _, out _)) return string.Empty;
        var accept = GetHeader(headers, "Accept");
        var language = GetHeader(headers, "Accept-Language");
        if (accept.Length > 4096 || language.Length > 4096) return string.Empty;
        using var hash = SHA256.Create();
        var input = Encoding.UTF8.GetBytes(
            "candidate\n" + scope + "\n" + resource.AbsoluteUri + "\n" + method + "\n" + normalizedUserAgent + "\n" +
            accept + "\n" + language);
        return Convert.ToBase64String(hash.ComputeHash(input));
    }

    private static string GetHeader(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var pair in headers)
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value ?? string.Empty;
        return string.Empty;
    }

    private sealed class RedirectLeaseEntry
    {
        public RedirectLeaseEntry(
            Uri effectiveUri, int redirectCount, DateTimeOffset expiresAtUtc, HttpResponseMessage response)
        {
            EffectiveUri = effectiveUri;
            RedirectCount = redirectCount;
            ExpiresAtUtc = expiresAtUtc;
            Length = GetRepresentationLength(response);
            ETag = response.Headers.ETag?.ToString();
            LastModified = response.Content.Headers.LastModified;
            IsHlsManifest = SourceBehaviorClassifier.Classify(response, effectiveUri) ==
                            SourceTransportBehavior.HlsManifest;
        }

        private long? Length { get; }

        private string? ETag { get; }

        private DateTimeOffset? LastModified { get; }

        public bool IsHlsManifest { get; set; }

        public bool MatchesRepresentation(HttpResponseMessage response) =>
            (!Length.HasValue || Length == GetRepresentationLength(response)) &&
            (ETag is null || string.Equals(ETag, response.Headers.ETag?.ToString(), StringComparison.Ordinal)) &&
            (!LastModified.HasValue || LastModified == response.Content.Headers.LastModified);

        private static long? GetRepresentationLength(HttpResponseMessage response) =>
            response.Content.Headers.ContentRange?.Length ??
            ((int)response.StatusCode == 200 ? response.Content.Headers.ContentLength : null);

        public Uri EffectiveUri { get; }

        public int RedirectCount { get; }

        public DateTimeOffset ExpiresAtUtc { get; }
    }

    private sealed class SourceAttemptEntry
    {
        public SourceAttemptEntry(DateTimeOffset now) => LastTouchedUtc = now;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public int FailureCount { get; set; }

        public int StatusCode { get; set; }

        public DateTimeOffset BlockedUntilUtc { get; set; }

        public DateTimeOffset RetainUntilUtc { get; set; }

        public DateTimeOffset LastTouchedUtc { get; set; }
    }

    private sealed class SourceAttemptLease : IDisposable
    {
        private Action? release;

        public SourceAttemptLease(
            int generation,
            string key,
            SourceAttemptEntry entry,
            Action release)
        {
            Generation = generation;
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            this.release = release ?? throw new ArgumentNullException(nameof(release));
        }

        public int Generation { get; }

        public string Key { get; }

        public SourceAttemptEntry Entry { get; }

        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }

    private sealed class DirectRouteEntry
    {
        public DirectRouteEntry(
            Uri? effectiveUri,
            SourceTransportBehavior behavior,
            bool resolveSourceRedirect,
            string sourceStateKey,
            DateTimeOffset expiresAtUtc)
        {
            if (!resolveSourceRedirect && effectiveUri is null)
                throw new ArgumentNullException(nameof(effectiveUri));
            EffectiveUri = effectiveUri;
            Behavior = behavior;
            ResolveSourceRedirect = resolveSourceRedirect;
            SourceStateKey = !string.IsNullOrWhiteSpace(sourceStateKey)
                ? sourceStateKey
                : throw new ArgumentException("The source state key is unavailable.", nameof(sourceStateKey));
            ExpiresAtUtc = expiresAtUtc;
        }

        public Uri? EffectiveUri { get; }

        public SourceTransportBehavior Behavior { get; }

        public bool ResolveSourceRedirect { get; }

        public string SourceStateKey { get; }

        public DateTimeOffset ExpiresAtUtc { get; }
    }

    private sealed class DirectRouteGateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    internal sealed class DirectRouteGateLease : IDisposable
    {
        private Action? release;

        public DirectRouteGateLease(int generation, Action release)
        {
            Generation = generation;
            this.release = release ?? throw new ArgumentNullException(nameof(release));
        }

        public int Generation { get; }

        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }

    private sealed class TransportResponse
    {
        public TransportResponse(
            HttpResponseMessage response,
            Uri effectiveUri,
            int redirectCount,
            bool isRedirectHandoff)
        {
            Response = response;
            EffectiveUri = effectiveUri;
            RedirectCount = redirectCount;
            IsRedirectHandoff = isRedirectHandoff;
        }

        public HttpResponseMessage Response { get; }

        public Uri EffectiveUri { get; }

        public int RedirectCount { get; }

        public bool IsRedirectHandoff { get; }
    }
}

internal readonly struct DirectRouteDecision
{
    private DirectRouteDecision(
        Uri? effectiveUri,
        bool resolveSourceRedirect,
        SourceTransportBehavior behavior,
        DateTimeOffset expiresAtUtc)
    {
        EffectiveUri = effectiveUri;
        ResolveSourceRedirect = resolveSourceRedirect;
        Behavior = behavior;
        ExpiresAtUtc = expiresAtUtc;
    }

    public static DirectRouteDecision Redirect(
        Uri effectiveUri,
        SourceTransportBehavior behavior,
        DateTimeOffset expiresAtUtc) =>
        new(effectiveUri ?? throw new ArgumentNullException(nameof(effectiveUri)), false, behavior, expiresAtUtc);

    public static DirectRouteDecision SourceRedirect(
        SourceTransportBehavior behavior,
        DateTimeOffset expiresAtUtc) =>
        new(null, true, behavior, expiresAtUtc);

    public Uri? EffectiveUri { get; }

    public bool ResolveSourceRedirect { get; }

    public SourceTransportBehavior Behavior { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed class GatewayTransportLease : IDisposable
{
    private Action release;
    private readonly CancellationToken requestCancellation;
    private readonly TimeSpan idleTimeout;
    private HttpResponseMessage? response;
    private Stream? preparedStream;
    private byte[]? bufferedPrefix;
    private Action? markHlsClassification;

    internal GatewayTransportLease(
        HttpResponseMessage response,
        Uri effectiveUri,
        int redirectCount,
        bool usedCachedRedirect,
        bool retriedRejectedRedirect,
        int cacheGeneration,
        DateTimeOffset redirectExpiresAtUtc,
        CancellationToken requestCancellation,
        TimeSpan idleTimeout,
        Action release,
        bool isRedirectHandoff = false)
    {
        this.response = response ?? throw new ArgumentNullException(nameof(response));
        EffectiveUri = effectiveUri ?? throw new ArgumentNullException(nameof(effectiveUri));
        RedirectCount = redirectCount;
        UsedCachedRedirect = usedCachedRedirect;
        RetriedRejectedRedirect = retriedRejectedRedirect;
        CacheGeneration = cacheGeneration;
        RedirectExpiresAtUtc = redirectExpiresAtUtc;
        IsRedirectHandoff = isRedirectHandoff;
        IsHlsManifest = SourceBehaviorClassifier.Classify(response, effectiveUri) ==
                        SourceTransportBehavior.HlsManifest;
        this.requestCancellation = requestCancellation;
        this.idleTimeout = idleTimeout;
        this.release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public HttpResponseMessage Response => response ?? throw new ObjectDisposedException(nameof(GatewayTransportLease));

    public Uri EffectiveUri { get; }

    public int RedirectCount { get; }

    public bool UsedCachedRedirect { get; }

    public bool RetriedRejectedRedirect { get; }

    internal int CacheGeneration { get; }

    internal DateTimeOffset RedirectExpiresAtUtc { get; }

    internal bool IsRedirectHandoff { get; }

    internal bool IsHlsManifest { get; set; }

    internal void OwnHlsClassification(Action classify)
    {
        markHlsClassification += classify;
        if (IsHlsManifest) classify();
    }

    internal void MarkHlsManifest()
    {
        IsHlsManifest = true;
        markHlsClassification?.Invoke();
    }

    internal void OwnCancellation(CancellationTokenSource cancellation)
    {
        var previousRelease = release;
        release = () =>
        {
            try
            {
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (AggregateException) { }
            }
            finally
            {
                try { cancellation.Dispose(); }
                finally { previousRelease(); }
            }
        };
    }

    public async Task<ReadOnlyMemory<byte>> PeekPrefixAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes < 1 || maximumBytes > FastSeekCoordinator.MaximumProbeBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var ownedResponse = response ?? throw new ObjectDisposedException(nameof(GatewayTransportLease));
        if (bufferedPrefix is not null) return bufferedPrefix;
        try
        {
            preparedStream ??= await ownedResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new byte[maximumBytes];
            var offset = 0;
            while (offset < buffer.Length)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    requestCancellation,
                    cancellationToken);
                timeout.CancelAfter(idleTimeout);
                var read = await preparedStream.ReadAsync(buffer, offset, buffer.Length - offset, timeout.Token)
                    .ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }
            bufferedPrefix = offset == buffer.Length ? buffer : buffer.Take(offset).ToArray();
            return bufferedPrefix;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public async Task<Stream> OpenOwnedStreamAsync()
    {
        var ownedResponse = response ?? throw new ObjectDisposedException(nameof(GatewayTransportLease));
        var stream = preparedStream ?? await ownedResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
        if (bufferedPrefix is { Length: > 0 }) stream = new PrefixReadStream(bufferedPrefix, stream);
        preparedStream = null;
        bufferedPrefix = null;
        response = null;
        return new OwnedResponseStream(stream, ownedResponse, release, requestCancellation, idleTimeout);
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref response, null);
        if (current is null) return;
        try { current.Dispose(); }
        finally { release(); }
    }
}

internal sealed class PrefixReadStream : Stream
{
    private readonly byte[] prefix;
    private readonly Stream inner;
    private int offset;

    public PrefixReadStream(byte[] prefix, Stream inner)
    {
        this.prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long value, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int bufferOffset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int bufferOffset, int count)
    {
        var copied = CopyPrefix(buffer.AsSpan(bufferOffset, count));
        return copied > 0 ? copied : inner.Read(buffer, bufferOffset, count);
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int bufferOffset,
        int count,
        CancellationToken cancellationToken)
    {
        var copied = CopyPrefix(buffer.AsSpan(bufferOffset, count));
        return copied > 0
            ? copied
            : await inner.ReadAsync(buffer, bufferOffset, count, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    private int CopyPrefix(Span<byte> target)
    {
        var count = Math.Min(target.Length, prefix.Length - offset);
        if (count <= 0) return 0;
        prefix.AsSpan(offset, count).CopyTo(target);
        offset += count;
        return count;
    }
}

internal sealed class OwnedResponseStream : Stream
{
    private readonly Stream inner;
    private HttpResponseMessage? response;
    private Action? release;
    private readonly CancellationToken requestCancellation;
    private readonly TimeSpan idleTimeout;
    private CancellationTokenRegistration cancellationRegistration;

    public OwnedResponseStream(
        Stream inner,
        HttpResponseMessage response,
        Action release,
        CancellationToken requestCancellation,
        TimeSpan idleTimeout)
    {
        this.inner = inner;
        this.response = response;
        this.release = release;
        this.requestCancellation = requestCancellation;
        this.idleTimeout = idleTimeout;
        cancellationRegistration = requestCancellation.Register(Dispose);
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellation,
            cancellationToken);
        timeout.CancelAfter(idleTimeout);
        try
        {
            return await inner.ReadAsync(buffer, offset, count, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            cancellationRegistration.Dispose();
            if (Interlocked.Exchange(ref release, null) is not { } releaseAction) return;
            try { inner.Dispose(); }
            finally
            {
                try { Interlocked.Exchange(ref response, null)?.Dispose(); }
                finally { releaseAction(); }
            }
        }
        base.Dispose(disposing);
    }
}

public sealed class GatewayCapacityException : Exception
{
    public GatewayCapacityException() : base("The playback relay capacity has been reached.") { }
}

internal sealed class GatewayRepresentationChangedException : IOException
{
    public GatewayRepresentationChangedException() : base("The source representation changed during playback.") { }
}

public sealed class GatewaySourceBackoffException : Exception
{
    public GatewaySourceBackoffException(int statusCode, int retryAfterSeconds)
        : base("The playback source is temporarily backed off.")
    {
        StatusCode = statusCode is >= 400 and <= 599 ? statusCode : 503;
        RetryAfterSeconds = Math.Max(1, retryAfterSeconds);
    }

    public int StatusCode { get; }

    public int RetryAfterSeconds { get; }
}
