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
    public const int MaximumRedirectLeases = 4096;
    private static readonly string DefaultUserAgent =
        "Emby.StrmBridge/" + (typeof(GatewayTransport).Assembly.GetName().Version?.ToString(3) ?? "unknown");
    private readonly object sync = new();
    private readonly Dictionary<string, RedirectLeaseEntry> redirectLeases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> redirectLeaseGates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectRouteEntry> directRoutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectRouteGateEntry> directRouteGates = new(StringComparer.Ordinal);
    private readonly HttpClient client;
    private readonly RedirectPolicy redirectPolicy;
    private readonly IClock clock;
    private int activeRequests;
    private int redirectLeaseGeneration;
    private bool disposed;

    public GatewayTransport(RedirectPolicy redirectPolicy, IClock? clock = null)
    {
        this.redirectPolicy = redirectPolicy ?? throw new ArgumentNullException(nameof(redirectPolicy));
        this.clock = clock ?? new SystemClock();
        client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public int ActiveRequests
    {
        get { lock (sync) return activeRequests; }
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

    public Uri ValidateResource(Uri source, Uri target) =>
        redirectPolicy.Validate(source, target.AbsoluteUri);

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
        var clientBinding = ticket.DeviceBindingHash.Length > 0
            ? "device-" + Convert.ToBase64String(ticket.DeviceBindingHash)
            : "ticket-" + ticketValue;
        using var hash = SHA256.Create();
        var input = Encoding.UTF8.GetBytes(
            ((int)ticket.Scope).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
            ticket.HlsDepth.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
            ticket.ItemId.ToString("N") + "\n" +
            ticket.MediaSourceId + "\n" +
            ticket.Source.SourceFingerprint + "\n" +
            ticket.UpstreamUri.AbsoluteUri + "\n" +
            Convert.ToBase64String(ticket.UserBindingHash) + "\n" +
            clientBinding);
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
            if (found.RelayRequired)
            {
                decision = DirectRouteDecision.Relay;
                return true;
            }
        }

        Uri validated;
        try { validated = redirectPolicy.Validate(source, found!.EffectiveUri!.AbsoluteUri); }
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
            decision = DirectRouteDecision.Redirect(validated, current.Behavior);
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
        var validated = redirectPolicy.Validate(source, lease.EffectiveUri.AbsoluteUri);
        var key = CreateRedirectLeaseKey(
            scope,
            NormalizeMethod(method),
            NormalizeUserAgent(userAgent),
            requestHeaders);
        if (key.Length == 0) return;
        StoreDirectRoute(
            key,
            new DirectRouteEntry(validated, behavior, relayRequired: false, clock.UtcNow + RedirectLeaseLifetime),
            lease.CacheGeneration,
            removeRedirectLease: false);
    }

    internal void RememberDirectRelay(
        string scope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        GatewayTransportLease lease)
    {
        if (lease is null) throw new ArgumentNullException(nameof(lease));
        var key = CreateRedirectLeaseKey(
            scope,
            NormalizeMethod(method),
            NormalizeUserAgent(userAgent),
            requestHeaders);
        if (key.Length == 0) return;
        StoreDirectRoute(
            key,
            new DirectRouteEntry(null, SourceTransportBehavior.FileBody, relayRequired: true,
                clock.UtcNow + RedirectLeaseLifetime),
            lease.CacheGeneration,
            removeRedirectLease: true);
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
        CancellationToken cancellationToken)
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
                cancellationToken)
            .ConfigureAwait(false);

    internal async Task<GatewayTransportLease> OpenDirectAsync(
        Uri source,
        string redirectLeaseScope,
        int expectedGeneration,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        CancellationToken cancellationToken)
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
                cancellationToken)
            .ConfigureAwait(false);

    internal async Task<GatewayTransportLease> OpenProbeAsync(
        Uri source,
        string? redirectLeaseScope,
        string? redirectCandidateScope,
        string method,
        string? userAgent,
        IReadOnlyDictionary<string, string> requestHeaders,
        PluginConfiguration options,
        CancellationToken cancellationToken)
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
                cancellationToken)
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
        CancellationToken cancellationToken)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (options is null) throw new ArgumentNullException(nameof(options));
        var leaseGeneration = Enter(options.RelayConcurrency, isProbe, expectedGeneration);
        SemaphoreSlim? resolutionGate = null;
        var resolutionGateHeld = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.GatewayTimeoutSeconds));
            var normalizedUserAgent = NormalizeUserAgent(userAgent);
            var normalizedMethod = NormalizeMethod(method);
            var leaseKey = CreateRedirectLeaseKey(
                redirectLeaseScope,
                normalizedMethod,
                normalizedUserAgent,
                requestHeaders);
            var candidateKey = CreateRedirectCandidateKey(
                redirectCandidateScope,
                normalizedMethod,
                requestHeaders);
            var hasRequestedRange = TryGetSingleRange(
                requestHeaders,
                out var requestedRangeStart,
                out var requestedRangeEnd);
            Func<HttpResponseMessage, bool>? rangeResponseValidator = hasRequestedRange
                ? response => IsRedirectRangeResponseCompatible(
                    response,
                    requestedRangeStart,
                    requestedRangeEnd)
                : null;
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
                    if (cachedLease is not null) return cachedLease;
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
                                leaseGeneration);
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
                            if (cachedLease is not null) return cachedLease;
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
                                        leaseGeneration);
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
                if (resolved.RedirectCount > 0 && InvalidatesRedirectLease(resolved.Response))
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
                if (leaseKey.Length > 0 && resolved.RedirectCount > 0 &&
                    IsReusableRedirectLeaseResponse(resolved.Response))
                    StoreRedirectLease(
                        leaseKey,
                        resolved.EffectiveUri,
                        resolved.RedirectCount,
                        leaseGeneration);
                if (candidateKey.Length > 0 && resolved.RedirectCount > 0 &&
                    IsRedirectRangeResponseCompatible(
                        resolved.Response,
                        requestedRangeStart,
                        requestedRangeEnd))
                    StoreRedirectLease(
                        candidateKey,
                        resolved.EffectiveUri,
                        resolved.RedirectCount,
                        leaseGeneration);
                return new GatewayTransportLease(
                    resolved.Response,
                    resolved.EffectiveUri,
                    resolved.RedirectCount,
                    usedCachedRedirect: false,
                    retriedRejectedRedirect,
                    leaseGeneration,
                    cancellationToken,
                    TimeSpan.FromSeconds(options.GatewayTimeoutSeconds),
                    Exit);
            }
            finally
            {
                if (resolutionGateHeld) ReleaseRedirectLeaseGate(resolutionGateKey, resolutionGate!);
            }
        }
        catch
        {
            Exit();
            throw;
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            redirectLeaseGeneration++;
            redirectLeases.Clear();
            redirectLeaseGates.Clear();
            directRoutes.Clear();
        }
    }

    public int RemoveExpiredRedirectLeases()
    {
        lock (sync)
        {
            var now = clock.UtcNow;
            return RemoveExpiredRedirectLeasesUnsafe(now) + RemoveExpiredDirectRoutesUnsafe(now);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            redirectLeaseGeneration++;
            redirectLeases.Clear();
            redirectLeaseGates.Clear();
            directRoutes.Clear();
        }
        client.Dispose();
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
        CancellationToken cancellationToken)
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
            if (!IsRedirect(response)) return new TransportResponse(response, current, redirects);
            if (redirects >= options.RedirectHopLimit)
            {
                response.Dispose();
                throw new RedirectRejectedException(RedirectRejectionReason.TooManyRedirects);
            }
            var location = response.Headers.Location?.OriginalString;
            response.Dispose();
            current = redirectPolicy.Validate(current, location);
            redirects++;
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

    private void StoreRedirectLease(string key, Uri effectiveUri, int redirectCount, int expectedGeneration)
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
                now + RedirectLeaseLifetime);
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
        int expectedGeneration,
        bool removeRedirectLease)
    {
        lock (sync)
        {
            if (disposed || expectedGeneration != redirectLeaseGeneration) return;
            RemoveExpiredDirectRoutesUnsafe(clock.UtcNow);
            if (!directRoutes.ContainsKey(key) && directRoutes.Count >= MaximumRedirectLeases)
            {
                var oldest = directRoutes.OrderBy(pair => pair.Value.ExpiresAtUtc).First();
                directRoutes.Remove(oldest.Key);
            }
            directRoutes[key] = entry;
            if (removeRedirectLease) redirectLeases.Remove(key);
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
            if (IsReusableRedirectLeaseResponse(cachedResponse.Response) &&
                (responseValidator is null || responseValidator(cachedResponse.Response)))
            {
                var effectiveRedirectCount = Math.Max(cached.RedirectCount, cachedResponse.RedirectCount);
                StoreRedirectLease(
                    leaseKey,
                    cachedResponse.EffectiveUri,
                    effectiveRedirectCount,
                    leaseGeneration);
                return new GatewayTransportLease(
                    cachedResponse.Response,
                    cachedResponse.EffectiveUri,
                    effectiveRedirectCount,
                    usedCachedRedirect: true,
                    retriedRejectedRedirect: false,
                    leaseGeneration,
                    requestCancellation,
                    TimeSpan.FromSeconds(options.GatewayTimeoutSeconds),
                    Exit);
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

    private static string CreateRedirectCandidateKey(
        string? scope,
        string method,
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
            "candidate\n" + scope + "\n" + method + "\n" + accept + "\n" + language);
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
        public RedirectLeaseEntry(Uri effectiveUri, int redirectCount, DateTimeOffset expiresAtUtc)
        {
            EffectiveUri = effectiveUri;
            RedirectCount = redirectCount;
            ExpiresAtUtc = expiresAtUtc;
        }

        public Uri EffectiveUri { get; }

        public int RedirectCount { get; }

        public DateTimeOffset ExpiresAtUtc { get; }
    }

    private sealed class DirectRouteEntry
    {
        public DirectRouteEntry(
            Uri? effectiveUri,
            SourceTransportBehavior behavior,
            bool relayRequired,
            DateTimeOffset expiresAtUtc)
        {
            EffectiveUri = effectiveUri;
            Behavior = behavior;
            RelayRequired = relayRequired;
            ExpiresAtUtc = expiresAtUtc;
        }

        public Uri? EffectiveUri { get; }

        public SourceTransportBehavior Behavior { get; }

        public bool RelayRequired { get; }

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
        public TransportResponse(HttpResponseMessage response, Uri effectiveUri, int redirectCount)
        {
            Response = response;
            EffectiveUri = effectiveUri;
            RedirectCount = redirectCount;
        }

        public HttpResponseMessage Response { get; }

        public Uri EffectiveUri { get; }

        public int RedirectCount { get; }
    }
}

internal readonly struct DirectRouteDecision
{
    private DirectRouteDecision(
        bool relayRequired,
        Uri? effectiveUri,
        SourceTransportBehavior behavior)
    {
        RelayRequired = relayRequired;
        EffectiveUri = effectiveUri;
        Behavior = behavior;
    }

    public static DirectRouteDecision Relay { get; } =
        new(relayRequired: true, null, SourceTransportBehavior.FileBody);

    public static DirectRouteDecision Redirect(Uri effectiveUri, SourceTransportBehavior behavior) =>
        new(false, effectiveUri ?? throw new ArgumentNullException(nameof(effectiveUri)), behavior);

    public bool RelayRequired { get; }

    public Uri? EffectiveUri { get; }

    public SourceTransportBehavior Behavior { get; }
}

public sealed class GatewayTransportLease : IDisposable
{
    private readonly Action release;
    private readonly CancellationToken requestCancellation;
    private readonly TimeSpan idleTimeout;
    private HttpResponseMessage? response;
    private Stream? preparedStream;
    private byte[]? bufferedPrefix;

    internal GatewayTransportLease(
        HttpResponseMessage response,
        Uri effectiveUri,
        int redirectCount,
        bool usedCachedRedirect,
        bool retriedRejectedRedirect,
        int cacheGeneration,
        CancellationToken requestCancellation,
        TimeSpan idleTimeout,
        Action release)
    {
        this.response = response ?? throw new ArgumentNullException(nameof(response));
        EffectiveUri = effectiveUri ?? throw new ArgumentNullException(nameof(effectiveUri));
        RedirectCount = redirectCount;
        UsedCachedRedirect = usedCachedRedirect;
        RetriedRejectedRedirect = retriedRejectedRedirect;
        CacheGeneration = cacheGeneration;
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
        current.Dispose();
        release();
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
            inner.Dispose();
            Interlocked.Exchange(ref response, null)?.Dispose();
            Interlocked.Exchange(ref release, null)?.Invoke();
        }
        base.Dispose(disposing);
    }
}

public sealed class GatewayCapacityException : Exception
{
    public GatewayCapacityException() : base("The playback relay capacity has been reached.") { }
}
