using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Domain;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Playback;

internal sealed class TranscodeStartRejectedException : ServiceUnavailableException
{
    public TranscodeStartRejectedException(int retryAfterSeconds)
        : base("STRM Bridge is waiting before retrying this playback target.") =>
        RetryAfterSeconds = retryAfterSeconds;

    public int RetryAfterSeconds { get; }
}

internal sealed class TranscodeJobCoordinator
{
    internal static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StartupCancellationThreshold = TimeSpan.FromSeconds(8);
    internal const int MaximumEntries = 1024;
    private readonly object sync = new();
    private readonly Dictionary<string, RetryState> retries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectorySlot> directories = new(
        Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConditionalWeakTable<MediaSourceInfo, Attempt> owners = new();
    private readonly IClock clock;
    private readonly ILogger logger;
    private int generation = -1;

    public TranscodeJobCoordinator(IClock clock, ILogger logger)
    {
        this.clock = clock;
        this.logger = logger;
    }

    public void CheckRetry(string? key, int runtimeGeneration)
    {
        if (key is null) return;
        lock (sync)
        {
            TrimRetries(runtimeGeneration);
            if (retries.TryGetValue(key, out var state)) RejectIfBlocked(state);
        }
    }

    public Attempt Register(MediaSourceInfo source, string outputPath, string? retryKey,
        int runtimeGeneration, DateTimeOffset startedAt, Action? releaseResources = null,
        Action? rollbackStart = null)
    {
        var directory = GetOutputDirectory(outputPath);
        Attempt attempt;
        lock (sync)
        {
            TrimRetries(runtimeGeneration);
            RetryState? retry = null;
            if (retryKey is not null)
            {
                if (retries.TryGetValue(retryKey, out retry)) RejectIfBlocked(retry);
                else
                {
                    if (retries.Count >= MaximumEntries) throw new TranscodeStartRejectedException(1);
                    retry = new RetryState();
                    retries.Add(retryKey, retry);
                }
            }
            if (!directories.TryGetValue(directory, out var slot))
            {
                foreach (var unused in directories.Where(pair => pair.Value.Reservations == 0 &&
                             !pair.Value.Owner.TryGetTarget(out _))
                             .Select(pair => pair.Key).ToArray()) directories.Remove(unused);
                if (directories.Count >= MaximumEntries) throw new TranscodeStartRejectedException(1);
                slot = new DirectorySlot();
                directories.Add(directory, slot);
            }
            attempt = new Attempt(
                slot,
                outputPath,
                directory,
                retryKey,
                runtimeGeneration,
                startedAt,
                releaseResources,
                rollbackStart);
            owners.Add(source, attempt);
            slot.Reservations++;
            if (retry is not null) retry.Active = attempt;
        }
        try { lock (attempt.Slot.Sync) attempt.Slot.Owner.SetTarget(attempt); }
        finally { lock (sync) attempt.Slot.Reservations--; }
        return attempt;
    }

    // A native start can reuse a previously managed directory as well.
    public void ObserveUnmanagedStart(string outputPath)
    {
        string directory;
        try { directory = GetOutputDirectory(outputPath); }
        catch (ArgumentException) { return; }
        DirectorySlot slot;
        lock (sync)
        {
            if (!directories.TryGetValue(directory, out slot)) return;
            slot.Reservations++;
        }
        try { lock (slot.Sync) slot.Owner.SetTarget(null!); }
        finally { lock (sync) slot.Reservations--; }
    }

    public async Task<T> ObserveStartAsync<T>(Task<T> task, Attempt attempt)
    {
        try
        {
            var job = await task.ConfigureAwait(false);
            Complete(attempt, failed: false, cancelled: false);
            return job;
        }
        catch (OperationCanceledException)
        {
            Complete(attempt, failed: true, cancelled: true);
            throw;
        }
        catch
        {
            Complete(attempt, failed: true, cancelled: false);
            throw;
        }
    }

    internal void Complete(Attempt attempt, bool failed, bool cancelled)
    {
        try
        {
            lock (sync)
            {
                if (attempt.RetryKey is null || attempt.Generation != generation ||
                    !retries.TryGetValue(attempt.RetryKey, out var retry) ||
                    !ReferenceEquals(retry.Active, attempt)) return;
                retry.Active = null;
                if (!failed || cancelled && clock.UtcNow - attempt.StartedAt < StartupCancellationThreshold)
                {
                    retries.Remove(attempt.RetryKey);
                    return;
                }
                retry.ExpiresAt = clock.UtcNow + FailureCooldown;
                logger.Warn("STRM_BRIDGE_TRANSCODE_START_COOLDOWN reason=" +
                            (cancelled ? "startup_cancelled" : "startup_failed") + " retry_seconds=" +
                            FailureCooldown.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            if (failed)
            {
                RollbackStart(attempt);
                ReleaseResources(attempt);
            }
            else attempt.CommitStart();
        }
    }

    public bool TryGetOwner(MediaSourceInfo source, string outputPath, out Attempt? attempt)
    {
        if (owners.TryGetValue(source, out attempt) &&
            string.Equals(attempt.OutputPath, outputPath, StringComparison.Ordinal)) return true;
        attempt = null;
        return false;
    }

    public async Task CleanupAsync(Attempt attempt, int retryCount, int delayMilliseconds,
        Action<string> deleteDirectory, Func<string, bool> hasActiveJob,
        Func<int, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (var retry = Math.Max(0, retryCount); retry < 10; retry++)
        {
            await delay(Math.Max(0, delayMilliseconds)).ConfigureAwait(false);
            var shouldRelease = false;
            try
            {
                lock (attempt.Slot.Sync)
                {
                    if (!attempt.Slot.Owner.TryGetTarget(out var current) ||
                        !ReferenceEquals(current, attempt))
                    {
                        logger.Debug("STRM_BRIDGE_TRANSCODE_CLEANUP_SKIPPED reason=directory_reused");
                        shouldRelease = true;
                    }
                    else
                    {
                        if (hasActiveJob(attempt.OutputPath))
                        {
                            logger.Debug("STRM_BRIDGE_TRANSCODE_CLEANUP_SKIPPED reason=job_active");
                            return;
                        }
                        shouldRelease = true;
                        deleteDirectory(attempt.Directory);
                    }
                }
                if (shouldRelease)
                {
                    attempt.CommitStart();
                    ReleaseResources(attempt);
                }
                return;
            }
            catch (DirectoryNotFoundException)
            {
                if (shouldRelease)
                {
                    attempt.CommitStart();
                    ReleaseResources(attempt);
                }
                return;
            }
            catch (Exception exception)
            {
                if (shouldRelease)
                {
                    attempt.CommitStart();
                    ReleaseResources(attempt);
                }
                logger.Debug("STRM_BRIDGE_TRANSCODE_CLEANUP_RETRY error=" + exception.GetType().Name);
                delayMilliseconds = 500;
            }
        }
    }

    private void RollbackStart(Attempt attempt)
    {
        try { attempt.RollbackStart(); }
        catch (Exception exception)
        {
            logger.Warn("STRM_BRIDGE_TRANSCODE_START_ROLLBACK_FAILED error=" + exception.GetType().Name);
        }
    }

    private void ReleaseResources(Attempt attempt)
    {
        try { attempt.ReleaseResources(); }
        catch (Exception exception)
        {
            logger.Warn("STRM_BRIDGE_TRANSCODE_RESOURCES_RELEASE_FAILED error=" + exception.GetType().Name);
        }
    }

    private void TrimRetries(int runtimeGeneration)
    {
        if (generation != runtimeGeneration)
        {
            retries.Clear();
            generation = runtimeGeneration;
        }
        foreach (var key in retries.Where(pair => pair.Value.Active is null &&
                     pair.Value.ExpiresAt <= clock.UtcNow).Select(pair => pair.Key).ToArray())
            retries.Remove(key);
    }

    private void RejectIfBlocked(RetryState state)
    {
        var seconds = state.Active is not null ? 1 :
            Math.Max(1, (int)Math.Ceiling((state.ExpiresAt - clock.UtcNow).TotalSeconds));
        logger.Debug("STRM_BRIDGE_TRANSCODE_START_DEFERRED retry_seconds=" + seconds);
        throw new TranscodeStartRejectedException(seconds);
    }

    private static string GetOutputDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            throw new ArgumentException("An absolute Emby output path is required.");
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || directory == Path.GetPathRoot(fullPath))
            throw new ArgumentException("An Emby output subdirectory is required.");
        return directory;
    }

    internal sealed class DirectorySlot
    {
        public object Sync { get; } = new();
        public WeakReference<Attempt> Owner { get; } = new(null!);
        public int Reservations { get; set; }
    }

    internal sealed class Attempt
    {
        private Action? releaseResources;
        private Action? rollbackStart;

        public Attempt(DirectorySlot slot, string outputPath, string directory, string? retryKey,
            int generation, DateTimeOffset startedAt, Action? releaseResources, Action? rollbackStart)
        {
            Slot = slot;
            OutputPath = outputPath;
            Directory = directory;
            RetryKey = retryKey;
            Generation = generation;
            StartedAt = startedAt;
            this.releaseResources = releaseResources;
            this.rollbackStart = rollbackStart;
        }

        public DirectorySlot Slot { get; }
        public string OutputPath { get; }
        public string Directory { get; }
        public string? RetryKey { get; }
        public int Generation { get; }
        public DateTimeOffset StartedAt { get; }

        internal void ReleaseResources() =>
            Interlocked.Exchange(ref releaseResources, null)?.Invoke();

        internal void RollbackStart() =>
            Interlocked.Exchange(ref rollbackStart, null)?.Invoke();

        internal void CommitStart() => Interlocked.Exchange(ref rollbackStart, null);
    }

    private sealed class RetryState
    {
        public Attempt? Active { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
