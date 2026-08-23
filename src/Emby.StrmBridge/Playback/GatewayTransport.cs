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

    public Uri ValidateResource(Uri source, Uri target) =>
        redirectPolicy.Validate(source, target.AbsoluteUri);

    internal static string CreateRedirectCandidateScope(SourceIdentity source)
    {
        if (source is null || source.SourceFingerprint.Length < 32)
            throw new ArgumentException("The source identity is unavailable.", nameof(source));
        return "source-" + source.SourceFingerprint.Substring(0, 32);
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
        CancellationToken cancellationToken)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (options is null) throw new ArgumentNullException(nameof(options));
        var leaseGeneration = Enter(options.RelayConcurrency, isProbe);
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
            try
            {
                if (leaseKey.Length > 0)
                {
                    cachedLease = await TryOpenRedirectLeaseAsync(
                            source, leaseKey, leaseGeneration, normalizedMethod, normalizedUserAgent,
                            requestHeaders, options, cancellationToken, timeout.Token, rangeResponseValidator)
                        .ConfigureAwait(false);
                    if (cachedLease is not null) return cachedLease;
                }

                if (candidateKey.Length > 0 && !string.Equals(candidateKey, leaseKey, StringComparison.Ordinal))
                {
                    var candidateLease = await TryOpenRedirectLeaseAsync(
                            source, candidateKey, leaseGeneration, normalizedMethod, normalizedUserAgent,
                            requestHeaders, options, cancellationToken, timeout.Token,
                            rangeResponseValidator)
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
                                    requestHeaders, options, cancellationToken, timeout.Token, rangeResponseValidator)
                                .ConfigureAwait(false);
                            if (cachedLease is not null) return cachedLease;
                        }
                        if (candidateKey.Length > 0 &&
                            !string.Equals(candidateKey, leaseKey, StringComparison.Ordinal))
                        {
                            var candidateLease = await TryOpenRedirectLeaseAsync(
                                    source, candidateKey, leaseGeneration, normalizedMethod, normalizedUserAgent,
                                    requestHeaders, options, cancellationToken, timeout.Token, rangeResponseValidator)
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
                var retriedRejectedRedirect = false;
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
                if (leaseKey.Length > 0 && resolved.RedirectCount > 0 && !InvalidatesRedirectLease(resolved.Response))
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
        }
    }

    public int RemoveExpiredRedirectLeases()
    {
        lock (sync) return RemoveExpiredRedirectLeasesUnsafe(clock.UtcNow);
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
        }
        client.Dispose();
    }

    private int Enter(int limit, bool isProbe)
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GatewayTransport));
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
        Func<HttpResponseMessage, bool>? responseValidator = null)
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
            if (!InvalidatesRedirectLease(cachedResponse.Response) &&
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
        RemoveRedirectLease(leaseKey);
        return null;
    }

    private static bool InvalidatesRedirectLease(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return status is 401 or 403 or 404 or 410;
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
        CancellationToken requestCancellation,
        TimeSpan idleTimeout,
        Action release)
    {
        this.response = response ?? throw new ArgumentNullException(nameof(response));
        EffectiveUri = effectiveUri ?? throw new ArgumentNullException(nameof(effectiveUri));
        RedirectCount = redirectCount;
        UsedCachedRedirect = usedCachedRedirect;
        RetriedRejectedRedirect = retriedRejectedRedirect;
        this.requestCancellation = requestCancellation;
        this.idleTimeout = idleTimeout;
        this.release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public HttpResponseMessage Response => response ?? throw new ObjectDisposedException(nameof(GatewayTransportLease));

    public Uri EffectiveUri { get; }

    public int RedirectCount { get; }

    public bool UsedCachedRedirect { get; }

    public bool RetriedRejectedRedirect { get; }

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
