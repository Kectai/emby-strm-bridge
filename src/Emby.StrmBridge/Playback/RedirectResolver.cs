using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Persistence;
using Emby.StrmBridge.Policy;

namespace Emby.StrmBridge.Playback;

public sealed class RedirectResolver : IDisposable
{
    public static readonly TimeSpan LeaseLifetime = TimeSpan.FromSeconds(30);
    public const int MaximumLeases = 256;
    public const int MaximumWaitersPerPending = 64;
    public const int MaximumPendingResolutions = 256;
    public const int MaximumSourceStates = 512;

    private readonly object sync = new();
    private readonly Dictionary<string, RedirectLease> leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingResolution> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FailureState> failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceBudget> budgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> sourceConcurrency = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim globalConcurrency;
    private readonly IRedirectSourceClient sourceClient;
    private readonly RedirectPolicy redirectPolicy;
    private readonly IClock clock;
    private int generation;
    private bool disposed;

    public RedirectResolver(
        IRedirectSourceClient sourceClient,
        RedirectPolicy redirectPolicy,
        IClock clock,
        int maximumGlobalConcurrency = 16)
    {
        this.sourceClient = sourceClient ?? throw new ArgumentNullException(nameof(sourceClient));
        this.redirectPolicy = redirectPolicy ?? throw new ArgumentNullException(nameof(redirectPolicy));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (maximumGlobalConcurrency < 1 || maximumGlobalConcurrency > 16)
            throw new ArgumentOutOfRangeException(nameof(maximumGlobalConcurrency));
        globalConcurrency = new SemaphoreSlim(maximumGlobalConcurrency, maximumGlobalConcurrency);
    }

    public async Task<RedirectLease> ResolveAsync(
        SourceIdentity source,
        string? userAgent,
        CancellationToken cancellationToken) =>
        await ResolveInternalAsync(source, userAgent, allowDirectMediaResponse: false, cancellationToken)
            .ConfigureAwait(false);

    public async Task<RedirectLease> ResolveForProbeAsync(
        SourceIdentity source,
        string? userAgent,
        CancellationToken cancellationToken) =>
        await ResolveInternalAsync(source, userAgent, allowDirectMediaResponse: true, cancellationToken)
            .ConfigureAwait(false);

    private async Task<RedirectLease> ResolveInternalAsync(
        SourceIdentity source,
        string? userAgent,
        bool allowDirectMediaResponse,
        CancellationToken cancellationToken)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedUserAgent = NormalizeUserAgent(userAgent);
        var key = (allowDirectMediaResponse ? "probe:" : "redirect:") +
                  source.SourceFingerprint + ":" + Hash(normalizedUserAgent);
        PendingResolution current;
        var shouldStart = false;

        lock (sync)
        {
            ThrowIfDisposed();
            var now = clock.UtcNow;
            RemoveExpiredUnsafe(now);
            if (leases.TryGetValue(key, out var cached) && cached.IsValidAt(now)) return cached;
            if (failures.TryGetValue(source.SourceFingerprint, out var failure) && now < failure.RetryAtUtc)
            {
                throw new RedirectSourceUnavailableException(SecondsUntil(failure.RetryAtUtc, now));
            }
            if (pending.TryGetValue(key, out current!))
            {
                if (current.Waiters == 0 && current.Cancellation.IsCancellationRequested)
                {
                    pending.Remove(key);
                    current = null!;
                }
            }
            if (current is not null)
            {
                if (current.Waiters >= MaximumWaitersPerPending) throw new RedirectThrottledException(1);
                current.Waiters++;
            }
            else
            {
                if (pending.Count >= MaximumPendingResolutions) throw new RedirectThrottledException(1);
                current = new PendingResolution(source.SourceFingerprint);
                current.Waiters = 1;
                current.Generation = generation;
                pending.Add(key, current);
                shouldStart = true;
            }
        }

        if (shouldStart)
        {
            _ = RunResolutionAsync(key, source, normalizedUserAgent, allowDirectMediaResponse, current);
        }

        try
        {
            return await AwaitWithCancellation(current.Completion.Task, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                current.Waiters--;
                if (current.Waiters == 0 && !current.Completion.Task.IsCompleted)
                {
                    current.Cancellation.Cancel();
                }
            }
        }
    }

    public CleanupResult RemoveExpired()
    {
        lock (sync) return RemoveExpiredUnsafe(clock.UtcNow);
    }

    public void Clear()
    {
        PendingResolution[] active;
        lock (sync)
        {
            generation++;
            active = pending.Values.ToArray();
            var activeSources = new HashSet<string>(
                pending.Values.Select(entry => entry.SourceFingerprint),
                StringComparer.Ordinal);
            pending.Clear();
            leases.Clear();
            failures.Clear();
            budgets.Clear();
            foreach (var key in sourceConcurrency.Keys.Where(key => !activeSources.Contains(key)).ToArray())
            {
                if (sourceConcurrency[key].CurrentCount != 2) continue;
                var semaphore = sourceConcurrency[key];
                sourceConcurrency.Remove(key);
                semaphore.Dispose();
            }
        }
        foreach (var entry in active) entry.Cancellation.Cancel();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }
        Clear();
        if (sourceClient is IDisposable disposable) disposable.Dispose();
    }

    private async Task RunResolutionAsync(
        string cacheKey,
        SourceIdentity source,
        string userAgent,
        bool allowDirectMediaResponse,
        PendingResolution entry)
    {
        try
        {
            var lease = await ResolveCoreAsync(
                    source, userAgent, allowDirectMediaResponse, entry.Generation, entry.Cancellation.Token)
                .ConfigureAwait(false);
            lock (sync)
            {
                if (disposed || entry.Generation != generation ||
                    entry.Cancellation.IsCancellationRequested || entry.Waiters == 0)
                {
                    entry.Completion.TrySetCanceled();
                    return;
                }
                failures.Remove(source.SourceFingerprint);
                if (leases.Count >= MaximumLeases)
                {
                    var oldest = leases.OrderBy(pair => pair.Value.CreatedAtUtc).First();
                    leases.Remove(oldest.Key);
                }
                leases[cacheKey] = lease;
                entry.Completion.TrySetResult(lease);
            }
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Completion.TrySetCanceled();
        }
        catch (Exception exception)
        {
            entry.Completion.TrySetException(exception);
        }
        finally
        {
            lock (sync)
            {
                if (pending.TryGetValue(cacheKey, out var found) && ReferenceEquals(found, entry))
                    pending.Remove(cacheKey);
                RemoveUnusedSemaphoreUnsafe(source.SourceFingerprint);
            }
        }
    }

    private async Task<RedirectLease> ResolveCoreAsync(
        SourceIdentity source,
        string userAgent,
        bool allowDirectMediaResponse,
        int requestGeneration,
        CancellationToken cancellationToken)
    {
        SourceBudget budget;
        SemaphoreSlim perSource;
        lock (sync)
        {
            var now = clock.UtcNow;
            if (!budgets.TryGetValue(source.SourceFingerprint, out budget!))
            {
                if (budgets.Count >= MaximumSourceStates) throw new RedirectThrottledException(1);
                budget = new SourceBudget(now);
                budgets.Add(source.SourceFingerprint, budget);
            }
            if (!budget.TryConsume(now, out var retryAfter))
                throw new RedirectThrottledException(retryAfter);
            if (!sourceConcurrency.TryGetValue(source.SourceFingerprint, out perSource!))
            {
                perSource = new SemaphoreSlim(2, 2);
                sourceConcurrency.Add(source.SourceFingerprint, perSource);
            }
        }

        await perSource.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await globalConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RedirectSourceResponse response;
                try
                {
                    response = await sourceClient.SendAsync(source.SourceUri, userAgent, cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception exception) when (
                    exception is System.Net.Http.HttpRequestException ||
                    exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    RegisterFailure(source.SourceFingerprint, null, requestGeneration, cancellationToken);
                    throw new RedirectSourceUnavailableException(2);
                }

                if (response.StatusCode == 301 || response.StatusCode == 302 ||
                    response.StatusCode == 307 || response.StatusCode == 308)
                {
                    var target = redirectPolicy.Validate(source.SourceUri, response.Location);
                    var completedAt = clock.UtcNow;
                    return new RedirectLease(target, completedAt, completedAt + LeaseLifetime);
                }
                if (allowDirectMediaResponse && (response.StatusCode == 200 || response.StatusCode == 206))
                {
                    var completedAt = clock.UtcNow;
                    return new RedirectLease(
                        source.SourceUri,
                        completedAt,
                        completedAt + LeaseLifetime,
                        isDirectSource: true);
                }
                if (response.StatusCode == 408 || response.StatusCode == 429 || response.StatusCode >= 500)
                {
                    var seconds = RegisterFailure(
                        source.SourceFingerprint, response.RetryAfterSeconds, requestGeneration, cancellationToken);
                    throw new RedirectSourceUnavailableException(seconds);
                }
                if (response.StatusCode == 401 || response.StatusCode == 403 ||
                    response.StatusCode == 404 || response.StatusCode == 410)
                {
                    throw new RedirectRejectedException(RedirectRejectionReason.PermanentFailure);
                }
                throw new RedirectRejectedException(RedirectRejectionReason.UnexpectedStatus);
            }
            finally
            {
                globalConcurrency.Release();
            }
        }
        finally
        {
            perSource.Release();
        }
    }

    private int RegisterFailure(
        string sourceKey,
        int? retryAfterSeconds,
        int requestGeneration,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (disposed || requestGeneration != generation || cancellationToken.IsCancellationRequested) return 1;
            failures.TryGetValue(sourceKey, out var previous);
            var count = Math.Min((previous?.Count ?? 0) + 1, 5);
            var seconds = Math.Max(1, Math.Min(60, retryAfterSeconds ?? (1 << count)));
            failures[sourceKey] = new FailureState(count, clock.UtcNow.AddSeconds(seconds));
            return seconds;
        }
    }

    private CleanupResult RemoveExpiredUnsafe(DateTimeOffset now)
    {
        var expiredLeases = leases.Where(pair => !pair.Value.IsValidAt(now)).Select(pair => pair.Key).ToArray();
        foreach (var key in expiredLeases) leases.Remove(key);
        var expiredFailures = failures.Where(pair => now >= pair.Value.RetryAtUtc).Select(pair => pair.Key).ToArray();
        foreach (var key in expiredFailures) failures.Remove(key);
        var idleBudgets = budgets.Where(pair => pair.Value.IsIdleAt(now)).Select(pair => pair.Key).ToArray();
        foreach (var key in idleBudgets)
        {
            budgets.Remove(key);
            var hasPendingRequest = pending.Values.Any(entry =>
                string.Equals(entry.SourceFingerprint, key, StringComparison.Ordinal));
            if (!hasPendingRequest &&
                sourceConcurrency.TryGetValue(key, out var semaphore) && semaphore.CurrentCount == 2)
            {
                sourceConcurrency.Remove(key);
                semaphore.Dispose();
            }
        }
        return new CleanupResult(expiredLeases.Length, expiredFailures.Length, idleBudgets.Length);
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(RedirectResolver));
    }

    private void RemoveUnusedSemaphoreUnsafe(string sourceKey)
    {
        if (budgets.ContainsKey(sourceKey) ||
            pending.Values.Any(entry =>
                string.Equals(entry.SourceFingerprint, sourceKey, StringComparison.Ordinal)) ||
            !sourceConcurrency.TryGetValue(sourceKey, out var semaphore) || semaphore.CurrentCount != 2)
            return;
        sourceConcurrency.Remove(sourceKey);
        semaphore.Dispose();
    }

    private static string NormalizeUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return string.Empty;
        var trimmed = userAgent.Trim();
        if (trimmed.Length > 256 || trimmed.Any(char.IsControl))
            throw new RedirectRejectedException(RedirectRejectionReason.InvalidUserAgent);
        return trimmed;
    }

    private static string Hash(string value)
    {
        using var hash = SHA256.Create();
        return HmacIdentityProvider.ToHex(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static int SecondsUntil(DateTimeOffset future, DateTimeOffset now) =>
        Math.Max(1, Math.Min(60, (int)Math.Ceiling((future - now).TotalSeconds)));

    private static async Task<T> AwaitWithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) return await task.ConfigureAwait(false);
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancellation.TrySetResult(true)))
        {
            if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
                throw new OperationCanceledException(cancellationToken);
        }
        return await task.ConfigureAwait(false);
    }

    private sealed class PendingResolution
    {
        public PendingResolution(string sourceFingerprint) => SourceFingerprint = sourceFingerprint;

        public string SourceFingerprint { get; }
        public TaskCompletionSource<RedirectLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = new();
        public int Waiters { get; set; }
        public int Generation { get; set; }
    }

    private sealed class FailureState
    {
        public FailureState(int count, DateTimeOffset retryAtUtc)
        {
            Count = count;
            RetryAtUtc = retryAtUtc;
        }
        public int Count { get; }
        public DateTimeOffset RetryAtUtc { get; }
    }

    private sealed class SourceBudget
    {
        private const double Capacity = 12;
        private const double TokensPerSecond = 0.5;
        private double tokens = Capacity;
        private DateTimeOffset lastRefillUtc;
        private DateTimeOffset lastUsedUtc;

        public SourceBudget(DateTimeOffset now)
        {
            lastRefillUtc = now;
            lastUsedUtc = now;
        }

        public bool TryConsume(DateTimeOffset now, out int retryAfterSeconds)
        {
            Refill(now);
            if (tokens >= 1)
            {
                tokens -= 1;
                lastUsedUtc = now;
                retryAfterSeconds = 0;
                return true;
            }
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((1 - tokens) / TokensPerSecond));
            return false;
        }

        public bool IsIdleAt(DateTimeOffset now)
        {
            Refill(now);
            return tokens >= Capacity && now - lastUsedUtc > TimeSpan.FromMinutes(2);
        }

        private void Refill(DateTimeOffset now)
        {
            var elapsed = Math.Max(0, (now - lastRefillUtc).TotalSeconds);
            tokens = Math.Min(Capacity, tokens + elapsed * TokensPerSecond);
            if (elapsed > 0) lastRefillUtc = now;
        }
    }
}

public readonly struct CleanupResult
{
    public CleanupResult(int leases, int failures, int budgets)
    {
        Leases = leases;
        Failures = failures;
        Budgets = budgets;
    }
    public int Leases { get; }
    public int Failures { get; }
    public int Budgets { get; }
}

public sealed class RedirectThrottledException : Exception
{
    public RedirectThrottledException(int retryAfterSeconds) : base("The source request budget is exhausted.")
        => RetryAfterSeconds = Math.Max(1, Math.Min(60, retryAfterSeconds));
    public int RetryAfterSeconds { get; }
}

public sealed class RedirectSourceUnavailableException : Exception
{
    public RedirectSourceUnavailableException(int retryAfterSeconds) : base("The remote source is temporarily unavailable.")
        => RetryAfterSeconds = Math.Max(1, Math.Min(60, retryAfterSeconds));
    public int RetryAfterSeconds { get; }
}
