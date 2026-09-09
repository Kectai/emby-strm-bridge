using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Localization;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Notifications;

namespace Emby.StrmBridge.Extraction;

public sealed class ExtractionCoordinator
{
    private const int MaximumSharedProbeResults = 256;
    private static readonly TimeSpan AdministratorNotificationTimeout = TimeSpan.FromSeconds(15);
    private static readonly string ProbeUserAgent =
        "Emby.StrmBridge/" + (typeof(ExtractionCoordinator).Assembly.GetName().Version?.ToString(3) ?? "unknown");
    private readonly PluginRuntime runtime;
    private readonly ILibraryManager libraryManager;
    private readonly IMediaSourceManager mediaSourceManager;
    private readonly IItemRepository itemRepository;
    private readonly INotificationManager? notificationManager;
    private readonly IActivityManager? activityManager;
    private readonly ILogger logger;
    private readonly Func<PluginConfiguration> optionsProvider;
    private readonly Func<string> localApiUrlProvider;
    private readonly IExtractionProbeFallback? probeFallback;
    private readonly SemaphoreSlim runGate = new(1, 1);
    private readonly SemaphoreSlim fallbackProbeGate = new(1, 1);
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> activeItems = new();
    private readonly object backgroundSync = new();
    private Task? backgroundTask;
    private int postScanWorkerRunning;
    private int postScanPending;
    private int trustRetryNotificationPending;

    public ExtractionCoordinator(
        PluginRuntime runtime,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IItemRepository itemRepository,
        ILogManager logManager,
        INotificationManager notificationManager,
        IActivityManager activityManager,
        IServerApplicationHost applicationHost)
        : this(
            runtime,
            libraryManager,
            mediaSourceManager,
            itemRepository,
            logManager,
            runtime.GetOptionsSnapshot,
            notificationManager,
            activityManager,
            () => applicationHost.GetLocalApiUrl(IPAddress.Loopback),
            IndependentFfprobeMediaInfoProbe.TryCreate(
                applicationHost,
                logManager.GetLogger(Plugin.Instance?.Name ?? "STRM Bridge")))
    {
    }

    internal ExtractionCoordinator(
        PluginRuntime runtime,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IItemRepository itemRepository,
        ILogManager logManager,
        Func<PluginConfiguration> optionsProvider,
        INotificationManager? notificationManager = null,
        IActivityManager? activityManager = null,
        Func<string>? localApiUrlProvider = null,
        IExtractionProbeFallback? probeFallback = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        this.mediaSourceManager = mediaSourceManager ?? throw new ArgumentNullException(nameof(mediaSourceManager));
        this.itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
        this.notificationManager = notificationManager;
        this.activityManager = activityManager;
        this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        this.localApiUrlProvider = localApiUrlProvider ??
            (() => throw new InvalidOperationException("The local probe gateway is unavailable."));
        this.probeFallback = probeFallback;
        logger = (logManager ?? throw new ArgumentNullException(nameof(logManager)))
            .GetLogger(Plugin.Instance?.Name ?? "STRM Bridge");
    }

    public bool QueuePostScan()
    {
        Interlocked.Exchange(ref postScanPending, 1);
        return TryStartPostScanWorker();
    }

    public bool QueueTrustRetry()
    {
        Interlocked.Exchange(ref trustRetryNotificationPending, 1);
        Interlocked.Exchange(ref postScanPending, 1);
        return TryStartPostScanWorker();
    }

    private bool TryStartPostScanWorker()
    {
        if (Interlocked.CompareExchange(ref postScanWorkerRunning, 1, 0) != 0) return false;
        lock (backgroundSync)
        {
            backgroundTask = Task.Run(async () =>
            {
                try
                {
                    while (Interlocked.Exchange(ref postScanPending, 0) != 0)
                    {
                        var notifyTrustRetryCompletion =
                            Interlocked.Exchange(ref trustRetryNotificationPending, 0) != 0;
                        try
                        {
                            var operation = runtime.BeginOperation();
                            var result = await ExtractAsync(
                                    force: false,
                                    progress: null,
                                    operation.CancellationToken)
                                .ConfigureAwait(false);
                            if (notifyTrustRetryCompletion)
                                await NotifyTrustRetryCompletedAsync(result, operation.CancellationToken)
                                    .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Configuration changes and shutdown cancel this pass.
                        }
                        catch (Exception)
                        {
                            logger.Debug("STRM_BRIDGE_POSTSCAN_FAILED");
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref postScanWorkerRunning, 0);
                    if (Volatile.Read(ref postScanPending) != 0) TryStartPostScanWorker();
                }
            });
        }
        return true;
    }

    internal async Task WaitForPostScanIdleAsync()
    {
        while (true)
        {
            Task? current;
            lock (backgroundSync) current = backgroundTask;
            if (current is not null) await current.ConfigureAwait(false);
            if (Volatile.Read(ref postScanWorkerRunning) == 0 &&
                Volatile.Read(ref postScanPending) == 0)
                return;
        }
    }

    public async Task<ExtractionResult> ExtractAsync(
        bool force,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        Guid? itemId = null,
        string? libraryId = null)
    {
        var operation = runtime.BeginOperation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operation.CancellationToken);
        var token = linked.Token;
        await runGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var options = optionsProvider().Snapshot();
            if (!options.Enabled) return new ExtractionResult();
            var libraryFilter = string.IsNullOrWhiteSpace(libraryId)
                ? options.IncludedLibraryIds
                : new[] { NormalizeLibraryId(libraryId) };
            var candidates = GetCandidates(libraryFilter, token)
                .Where(item => !itemId.HasValue || item.Id == itemId.Value)
                .ToArray();
            var result = new ExtractionResult { Total = candidates.Length };
            logger.Info("STRM_BRIDGE_EXTRACTION_STARTED total=" + candidates.Length +
                        " force=" + (force ? "1" : "0"));
            if (candidates.Length == 0)
            {
                progress?.Report(100);
                LogExtractionCompleted(result, options);
                return result;
            }

            var sourceFlights = new ProbeFlightCache(MaximumSharedProbeResults);
            var probeRunCircuit = new ProbeRunCircuit();
            var recordedSourceFailures = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var completed = 0;
            var nextIndex = -1;
            var workers = Enumerable.Range(0, options.MaximumExtractionConcurrency).Select(async _ =>
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= candidates.Length) break;
                    try
                    {
                        var outcome = await ProcessItemAsync(
                                candidates[index],
                                options,
                                force,
                                operation.Generation,
                                sourceFlights,
                                probeRunCircuit,
                                recordedSourceFailures,
                                token)
                            .ConfigureAwait(false);
                        result.Add(outcome);
                    }
                    finally
                    {
                        var current = Interlocked.Increment(ref completed);
                        progress?.Report(current * 100d / candidates.Length);
                    }
                }
            }).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            if (result.AwaitingApproval > 0)
                await NotifyAwaitingApprovalAsync(token).ConfigureAwait(false);
            LogExtractionCompleted(result, options);
            return result;
        }
        finally
        {
            FlushExtractionState();
            runGate.Release();
        }
    }

    public async Task<ExtractionResult> RestoreAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var operation = runtime.BeginOperation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operation.CancellationToken);
        var token = linked.Token;
        await runGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var options = optionsProvider().Snapshot();
            if (!options.Enabled) return new ExtractionResult();
            var candidates = GetCandidates(options.IncludedLibraryIds, token);
            var result = new ExtractionResult { Total = candidates.Length };
            var completed = 0;
            foreach (var item in candidates)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var source = runtime.SourcePolicy!.Read(item.Path);
                    if (!StaticMediaSourcePolicy.Matches(mediaSourceManager, item, source) ||
                        !runtime.MediaInfoStore!.TryLoad(source, out var snapshot))
                    {
                        result.Add(ExtractionOutcome.Skipped);
                    }
                    else if (TryApply(
                                 item,
                                 source,
                                 snapshot!.ToMediaSource("strmbridge-restored"),
                                 operation.Generation,
                                 saveSnapshot: null))
                    {
                        result.Add(ExtractionOutcome.Restored);
                    }
                    else
                    {
                        result.Add(ExtractionOutcome.Skipped);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    result.Add(ExtractionOutcome.Failed);
                    logger.Debug("STRM_BRIDGE_RESTORE_FAILED item=" + ShortId(item.Id));
                }
                progress?.Report(Interlocked.Increment(ref completed) * 100d / Math.Max(1, candidates.Length));
            }
            return result;
        }
        finally
        {
            FlushExtractionState();
            runGate.Release();
        }
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var operation = runtime.BeginOperation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operation.CancellationToken);
        var token = linked.Token;
        await runGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in GetCandidates(Array.Empty<string>(), token, includeAllLibraries: true))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // Snapshot ownership is path-based. Cleanup must not require the STRM
                    // contents to be readable or valid, because a temporary source-file
                    // problem does not make its recovery data orphaned.
                    keep.Add(runtime.SourcePolicy!.GetStorageKey(item.Path));
                }
                catch (SourcePolicyException)
                {
                    // An incomplete keep-set is unsafe: one unrepresentable candidate could
                    // otherwise cause valid snapshots and retry state to be deleted.
                    logger.Warn("STRM_BRIDGE_CLEANUP_SKIPPED reason=storage_key_unavailable");
                    return 0;
                }
            }
            runtime.ExtractionState!.RemoveMissing(keep);
            return runtime.MediaInfoStore!.RemoveOrphans(keep.Contains);
        }
        finally
        {
            runGate.Release();
        }
    }

    public async Task<ClearStoredMediaInfoResult> ClearStoredMediaInfoAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var clearedStateKeys = new HashSet<string>(StringComparer.Ordinal);
        var stateKeysRemoved = false;
        var operation = runtime.BeginOperation();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operation.CancellationToken);
        var token = linked.Token;
        await runGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var options = optionsProvider().Snapshot();
            var candidates = GetCandidates(options.IncludedLibraryIds, token);
            var result = new ClearStoredMediaInfoResult { Total = candidates.Length };
            logger.Info("STRM_BRIDGE_CLEAR_STARTED total=" + candidates.Length);
            var completed = 0;
            foreach (var item in candidates)
            {
                token.ThrowIfCancellationRequested();
                ItemTechnicalState? previous = null;
                try
                {
                    var storageKey = runtime.SourcePolicy!.GetStorageKey(item.Path);
                    previous = ClearTechnicalFields(item);
                    if (runtime.MediaInfoStore!.Remove(storageKey)) result.SnapshotsRemoved++;
                    clearedStateKeys.Add(storageKey);
                    result.AddCleared();
                }
                catch (Exception)
                {
                    if (previous is not null)
                    {
                        previous.Restore(item);
                        TryRestoreTechnicalFields(item, previous);
                    }
                    result.AddFailed();
                    logger.Debug("STRM_BRIDGE_CLEAR_FAILED item=" + ShortId(item.Id));
                }
                progress?.Report(Interlocked.Increment(ref completed) * 100d / Math.Max(1, candidates.Length));
            }
            result.StateEntriesRemoved = runtime.ExtractionState!.Remove(clearedStateKeys);
            stateKeysRemoved = true;
            logger.Info("STRM_BRIDGE_CLEAR_COMPLETED total=" + result.Total +
                        " cleared=" + result.Cleared +
                        " failed=" + result.Failed +
                        " snapshots_removed=" + result.SnapshotsRemoved +
                        " state_entries_removed=" + result.StateEntriesRemoved);
            return result;
        }
        finally
        {
            if (!stateKeysRemoved && clearedStateKeys.Count > 0)
            {
                try { runtime.ExtractionState?.Remove(clearedStateKeys); }
                catch (Exception exception) when (
                    exception is IOException || exception is UnauthorizedAccessException ||
                    exception is InvalidDataException ||
                    exception is System.Runtime.Serialization.SerializationException)
                {
                    logger.Debug("STRM_BRIDGE_STATE_SAVE_FAILED");
                }
            }
            runGate.Release();
        }
    }

    private async Task<ExtractionOutcome> ProcessItemAsync(
        BaseItem item,
        PluginConfiguration options,
        bool force,
        int operationGeneration,
        ProbeFlightCache sourceFlights,
        ProbeRunCircuit probeRunCircuit,
        ConcurrentDictionary<string, byte> recordedSourceFailures,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentlyAllowed(item, requirePlayback: false)) return ExtractionOutcome.Skipped;
        SourceIdentity source;
        try { source = runtime.SourcePolicy!.Read(item.Path); }
        catch (SourcePolicyException exception)
        {
            // A selected STRM item whose local record cannot be parsed is still missing work.
            // Report it as a failure so an all-green task result cannot hide invalid URLs.
            logger.Debug("STRM_BRIDGE_SOURCE_REJECTED item=" + ShortId(item.Id) +
                         " reason=" + exception.Reason.ToString().ToLowerInvariant());
            return ExtractionOutcome.Failed;
        }
        if (!StaticMediaSourcePolicy.Matches(mediaSourceManager, item, source))
        {
            logger.Debug("STRM_BRIDGE_STATIC_SOURCE_REJECTED item=" + ShortId(item.Id));
            return ExtractionOutcome.Skipped;
        }

        bool missing;
        try
        {
            missing = IsMissing(item, GetRequiredStreamType(item, source.SourceUri));
        }
        catch (Exception exception) when (IsRecoverableItemFailure(exception))
        {
            // A transient Emby repository failure is local to this item. It must not abort
            // the remaining library pass or be attributed to the remote media source.
            logger.Debug("STRM_BRIDGE_EXTRACTION_FAILED item=" + ShortId(item.Id) +
                         " reason=local_stream_read");
            return ExtractionOutcome.Failed;
        }
        var lastSuccessfulFingerprint = runtime.ExtractionState!.GetLastSuccessfulFingerprint(source.StorageKey);
        var shouldAttempt = force || !options.OnlyMissingMediaInfo || runtime.ExtractionState.ShouldAttempt(
            source.StorageKey,
            source.SourceFingerprint,
            runtime.Clock.UtcNow);
        if (!force && options.OnlyMissingMediaInfo && !missing)
        {
            // Existing complete information is authoritative in missing-only mode, including
            // after the STRM source changes. Refreshing it requires Force or
            // OnlyMissingMediaInfo=false. Recording the current baseline is best effort: a
            // full state store must never turn a complete item into a remote probe.
            if (!string.Equals(lastSuccessfulFingerprint, source.SourceFingerprint, StringComparison.Ordinal))
                BaselineCompleteItem(item, source, operationGeneration);
            return ExtractionOutcome.Skipped;
        }

        if (!force && options.OnlyMissingMediaInfo && options.EnablePersistence &&
            runtime.MediaInfoStore!.TryLoad(source, out var stored))
        {
            var restoredSource = stored!.ToMediaSource("strmbridge-restored");
            if (IsComplete(restoredSource, GetRequiredStreamType(item, source.SourceUri)))
            {
                try
                {
                    if (TryApply(
                            item,
                            source,
                            restoredSource,
                            operationGeneration,
                            saveSnapshot: null))
                    {
                        logger.Debug("STRM_BRIDGE_MEDIAINFO_RESTORED item=" + ShortId(item.Id));
                        return ExtractionOutcome.Restored;
                    }
                    return ExtractionOutcome.Skipped;
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (Exception)
                {
                    logger.Debug("STRM_BRIDGE_RESTORE_FAILED item=" + ShortId(item.Id));
                    return ExtractionOutcome.Failed;
                }
            }
        }

        // Retry state limits only a new remote probe. Local completeness checks and matching
        // snapshot restoration above remain available throughout the delay.
        if (!shouldAttempt) return ExtractionOutcome.Skipped;

        try
        {
            var probe = await ProbeSharedAsync(
                    item.Id,
                    source,
                    GetRequiredStreamType(item, source.SourceUri),
                    options.ExtractionTimeoutSeconds,
                    sourceFlights,
                    probeRunCircuit,
                    operationGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (probe is null) return ExtractionOutcome.Skipped;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = MediaInfoSnapshot.FromMediaSource(
                    source,
                    probe.MediaSource,
                    runtime.Clock.UtcNow);
                if (!TryApply(
                        item,
                        source,
                        probe.MediaSource,
                        operationGeneration,
                        snapshot))
                    return ExtractionOutcome.Skipped;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception)
            {
                // The remote probe completed successfully. A local commit failure must not
                // create source retry backoff, otherwise a transient repository problem can
                // suppress later probes for an otherwise healthy source.
                logger.Debug("STRM_BRIDGE_EXTRACTION_FAILED item=" + ShortId(item.Id) +
                             " reason=local_apply");
                return ExtractionOutcome.Failed;
            }
            logger.Debug("STRM_BRIDGE_MEDIAINFO_EXTRACTED item=" + ShortId(item.Id));
            return ExtractionOutcome.Extracted;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (RedirectRejectedException exception) when (
            exception.Reason == RedirectRejectionReason.UntrustedTargetHost)
        {
            RecordFailureIfCurrent(source, operationGeneration, recordedSourceFailures);
            logger.Debug("STRM_BRIDGE_EXTRACTION_AWAITING_TRUST item=" + ShortId(item.Id));
            return ExtractionOutcome.AwaitingApproval;
        }
        catch (ProbeInputNotOpenedException exception)
        {
            logger.Warn(exception.CircuitOpened
                ? "STRM_BRIDGE_EXTRACTION_PROBE_CIRCUIT_OPEN"
                : "STRM_BRIDGE_EXTRACTION_PROBE_INPUT_NOT_OPENED item=" + ShortId(item.Id));
            return ExtractionOutcome.Failed;
        }
        catch (ProbeLocalFailureException exception)
        {
            logger.Warn(exception.CircuitOpened
                ? "STRM_BRIDGE_EXTRACTION_PROBE_CIRCUIT_OPEN reason=fallback_local_failure"
                : "STRM_BRIDGE_EXTRACTION_PROBE_LOCAL_FAILURE item=" + ShortId(item.Id));
            return ExtractionOutcome.Failed;
        }
        catch (Exception exception)
        {
            RecordFailureIfCurrent(source, operationGeneration, recordedSourceFailures);
            logger.Debug("STRM_BRIDGE_EXTRACTION_FAILED item=" + ShortId(item.Id) +
                         " reason=" + GetFailureReason(exception));
            return ExtractionOutcome.Failed;
        }
    }

    private static string GetFailureReason(Exception exception)
    {
        if (exception is RedirectRejectedException rejected)
            return "redirect_" + rejected.Reason.ToString().ToLowerInvariant();
        if (exception is InvalidDataException) return "probe_incomplete";
        if (exception is ProbeInputNotOpenedException) return "probe_input_not_opened";
        if (exception is System.Net.Http.HttpRequestException) return "probe_http";
        if (exception is TaskCanceledException) return "timeout";
        return "probe_or_save";
    }

    private async Task<ProbeResult> ProbeAsync(
        Guid itemId,
        SourceIdentity source,
        MediaStreamType requiredStreamType,
        int timeoutSeconds,
        ProbeRunCircuit probeRunCircuit,
        int operationGeneration,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        activeItems.TryAdd(timeout, 0);
        string? probeTicket = null;
        TicketPayload? payload = null;
        try
        {
            var isAudio = requiredStreamType == MediaStreamType.Audio;
            probeTicket = runtime.Tickets.IssuePlayback(
                itemId, source.StorageKey, null, source, PlaybackTicketPurpose.ExtractionProbe,
                operationGeneration, TimeSpan.FromSeconds(timeoutSeconds));
            if (!runtime.Tickets.TryInspect(probeTicket, out payload) || payload is null)
                throw new ProbeInputNotOpenedException(probeRunCircuit.RecordInputNotOpened());
            payload.ProbeCancellation = timeout.Token;
            Volatile.Write(ref payload.ProbeInputObservedCallback, probeRunCircuit.RecordInputOpened);
            var target = GatewayRouteBuilder.CreateInternalPlaybackRoute(
                localApiUrlProvider(), string.Empty, probeTicket,
                Path.GetExtension(source.SourceUri.AbsolutePath).TrimStart('.')) ??
                throw new InvalidOperationException("The local probe gateway is unavailable.");
            var mediaSource = CreateProbeMediaSource(source, target);
            if (probeRunCircuit.PreferFallbackProbe && probeFallback?.IsAvailable == true)
            {
                await RunFallbackProbeAsync(mediaSource, isAudio, timeout.Token).ConfigureAwait(false);
                probeRunCircuit.RecordFallbackSuccess();
            }
            else
            {
                Exception? hostProbeFailure = null;
                try
                {
                    var probeTask = mediaSourceManager.AddMediaInfoWithProbeSafe(
                        mediaSource,
                        isAudio,
                        addProbeDelay: false,
                        timeout.Token);
                    await AwaitWithCancellation(probeTask, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    hostProbeFailure = exception;
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (payload is not null && Volatile.Read(ref payload.ProbeLocalFailureObserved) != 0)
                    throw new ProbeLocalFailureException(hostProbeFailure ??
                        new InvalidOperationException("The local media-probe path failed."));
                var hostInputOpened = payload is not null &&
                                      Volatile.Read(ref payload.ProbeRequestObserved) != 0;
                var hostProbeComplete = IsComplete(mediaSource, requiredStreamType);
                if ((!hostInputOpened || !hostProbeComplete && hostProbeFailure is null) &&
                    probeFallback?.IsAvailable == true)
                {
                    // A host probe can open the input successfully yet return only partial
                    // transport-stream metadata when its default analysis window is too
                    // small. Retry that item through the bounded independent probe, but only
                    // switch the rest of the run to fallback when the host never opened the
                    // input (the interception/compatibility case).
                    if (!hostInputOpened && probeRunCircuit.PreferFallback())
                        logger.Warn("STRM_BRIDGE_EXTRACTION_PROBE_FALLBACK_ACTIVE");
                    mediaSource = CreateProbeMediaSource(source, target);
                    Interlocked.Exchange(ref payload!.ProbeRequestObserved, 0);
                    await RunFallbackProbeAsync(mediaSource, isAudio, timeout.Token).ConfigureAwait(false);
                    probeRunCircuit.RecordFallbackSuccess();
                }
                else if (hostProbeFailure is not null)
                {
                    throw hostProbeFailure;
                }
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (payload is not null && Volatile.Read(ref payload.ProbeLocalFailureObserved) != 0)
                throw new ProbeLocalFailureException();
            var probeInputOpened = payload is not null && Volatile.Read(ref payload.ProbeRequestObserved) != 0;
            if (!probeInputOpened)
                throw new ProbeInputNotOpenedException(probeRunCircuit.RecordInputNotOpened());
            if (!IsComplete(mediaSource, requiredStreamType))
                throw new InvalidDataException("The media probe returned incomplete technical information.");
            return new ProbeResult(mediaSource);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (
            payload is not null && Volatile.Read(ref payload.ProbeLocalFailureObserved) != 0)
        {
            throw new ProbeLocalFailureException(exception);
        }
        catch (IndependentProbeResultException exception)
        {
            throw new ProbeLocalFailureException(
                probeRunCircuit.RecordFallbackLocalFailure(),
                exception);
        }
        catch (TicketCapacityException exception)
        {
            throw new ProbeInputNotOpenedException(probeRunCircuit.RecordInputNotOpened(), exception);
        }
        catch (Exception) when (payload is not null &&
                                Volatile.Read(ref payload.ProbeRejectionReason) >= 0)
        {
            throw new RedirectRejectedException((RedirectRejectionReason)Volatile.Read(ref payload.ProbeRejectionReason));
        }
        catch (Exception exception) when (
            payload is not null &&
            Volatile.Read(ref payload.ProbeRequestObserved) == 0 &&
            !(exception is ProbeInputNotOpenedException))
        {
            throw new ProbeInputNotOpenedException(probeRunCircuit.RecordInputNotOpened(), exception);
        }
        finally
        {
            try { timeout.Cancel(); }
            finally
            {
                if (probeTicket is not null) runtime.Tickets.Revoke(probeTicket);
                activeItems.TryRemove(timeout, out _);
            }
        }
    }

    private static MediaSourceInfo CreateProbeMediaSource(SourceIdentity source, string target) => new()
    {
        Id = "strmbridge-probe-" + source.StorageKey.Substring(0, 16),
        Path = target,
        ProbePath = target,
        Protocol = MediaProtocol.Http,
        ProbeProtocol = MediaProtocol.Http,
        IsRemote = true,
        RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = ProbeUserAgent,
        },
    };

    private async Task RunFallbackProbeAsync(
        MediaSourceInfo mediaSource,
        bool isAudio,
        CancellationToken cancellationToken)
    {
        await fallbackProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await probeFallback!.ProbeAsync(mediaSource, isAudio, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            fallbackProbeGate.Release();
        }
    }

    private async Task<ProbeResult?> ProbeSharedAsync(
        Guid itemId,
        SourceIdentity source,
        MediaStreamType requiredStreamType,
        int timeoutSeconds,
        ProbeFlightCache sourceFlights,
        ProbeRunCircuit probeRunCircuit,
        int operationGeneration,
        CancellationToken cancellationToken)
    {
        var flightKey = source.SourceFingerprint + ":" + requiredStreamType;
        var flight = sourceFlights.TryGetOrAdd(
            flightKey,
            () => new Lazy<Task<ProbeResult>>(
                () => ProbeAsync(
                    itemId, source, requiredStreamType, timeoutSeconds, probeRunCircuit, operationGeneration, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication),
            () => !probeRunCircuit.IsOpen,
            out var retained);
        if (flight is null) return null;
        try
        {
            return await flight.Value.ConfigureAwait(false);
        }
        catch
        {
            if (retained) sourceFlights.Remove(flightKey, flight);
            throw;
        }
    }

    private bool TryApply(
        BaseItem item,
        SourceIdentity source,
        MediaSourceInfo mediaSource,
        int operationGeneration,
        MediaInfoSnapshot? saveSnapshot)
    {
        PluginConfiguration? currentOptions = null;
        return runtime.TryCommitMediaInfo(
            operationGeneration,
            () =>
            {
                currentOptions = optionsProvider().Snapshot();
                return IsAllowed(item, currentOptions, requirePlayback: false);
            },
            () =>
            {
                var currentSource = runtime.SourcePolicy!.Read(item.Path);
                if (!source.HasSameFileVersion(currentSource))
                    throw new SourcePolicyException(SourceRejectionReason.FileChanged);
                ApplyTechnicalFields(item, mediaSource);
                if (saveSnapshot is not null && currentOptions!.EnablePersistence)
                {
                    if (!saveSnapshot.Matches(source))
                    {
                        logger.Debug("STRM_BRIDGE_SNAPSHOT_SAVE_FAILED item=" + ShortId(item.Id));
                    }
                    else
                    {
                        try { runtime.MediaInfoStore!.Save(source, saveSnapshot); }
                        catch (Exception exception) when (
                            exception is IOException || exception is UnauthorizedAccessException ||
                            exception is InvalidDataException || exception is ArgumentException ||
                            exception is System.Runtime.Serialization.SerializationException)
                        {
                            logger.Debug("STRM_BRIDGE_SNAPSHOT_SAVE_FAILED item=" + ShortId(item.Id));
                        }
                    }
                }
                try
                {
                    runtime.ExtractionState!.RecordSuccess(
                        source.StorageKey,
                        source.SourceFingerprint);
                }
                catch (Exception exception) when (
                    exception is IOException || exception is UnauthorizedAccessException ||
                    exception is InvalidDataException || exception is System.Runtime.Serialization.SerializationException)
                {
                    logger.Debug("STRM_BRIDGE_STATE_SAVE_FAILED item=" + ShortId(item.Id));
                }
            });
    }

    private void ApplyTechnicalFields(BaseItem item, MediaSourceInfo source)
    {
        var previous = ItemTechnicalState.Capture(item, GetStoredMediaStreams(item));
        var externalIndexChanges = new List<ExternalStreamIndexChange>();
        try
        {
            if (!string.IsNullOrWhiteSpace(source.Container)) item.Container = source.Container;
            if (source.RunTimeTicks.HasValue) item.RunTimeTicks = source.RunTimeTicks;
            item.Size = Math.Max(0L, source.Size.GetValueOrDefault());
            item.TotalBitrate = Math.Max(0, source.Bitrate.GetValueOrDefault());
            if (source.MediaStreams is { Count: > 0 })
            {
                var internalStreams = MediaInfoSnapshot.SelectInternalStreams(source.MediaStreams);
                var external = (previous.MediaStreams ?? new List<MediaStream>())
                    .Where(stream => stream.IsExternal)
                    .ToList();
                var usedIndices = new HashSet<int>(internalStreams
                    .Where(stream => stream.Index >= 0)
                    .Select(stream => stream.Index));
                var streamsNeedingIndex = new List<MediaStream>();
                foreach (var stream in external)
                {
                    if (stream.Index < 0 || !usedIndices.Add(stream.Index))
                        streamsNeedingIndex.Add(stream);
                }
                int? remappedAudioIndex = null;
                int? remappedSubtitleIndex = null;
                foreach (var stream in streamsNeedingIndex)
                {
                    var previousIndex = stream.Index;
                    var replacementIndex = FindAvailableStreamIndex(usedIndices);
                    externalIndexChanges.Add(new ExternalStreamIndexChange(stream, previousIndex));
                    stream.Index = replacementIndex;
                    usedIndices.Add(replacementIndex);
                    if (stream.Type == MediaStreamType.Audio && previous.AudioStreamIndex == previousIndex)
                        remappedAudioIndex = replacementIndex;
                    if (stream.Type == MediaStreamType.Subtitle && previous.SubtitleStreamIndex == previousIndex)
                        remappedSubtitleIndex = replacementIndex;
                }
                item.MediaStreams = internalStreams.Concat(external).ToList();
                item.AudioStreamIndex = SelectDefaultIndex(
                    internalStreams, external, MediaStreamType.Audio, source.DefaultAudioStreamIndex,
                    remappedAudioIndex ?? previous.AudioStreamIndex);
                item.SubtitleStreamIndex = SelectDefaultIndex(
                    internalStreams, external, MediaStreamType.Subtitle, source.DefaultSubtitleStreamIndex,
                    remappedSubtitleIndex ?? previous.SubtitleStreamIndex);
            }
            PersistTechnicalFields(item);
        }
        catch
        {
            RestoreExternalStreamIndices(externalIndexChanges);
            previous.Restore(item);
            TryRestoreTechnicalFields(item, previous);
            throw;
        }
    }

    private static int? SelectDefaultIndex(
        IEnumerable<MediaStream> internalStreams, IEnumerable<MediaStream> external,
        MediaStreamType type, int? newDefault, int? previousExternalDefault)
    {
        if (newDefault.HasValue && internalStreams.Any(stream => stream.Type == type && stream.Index == newDefault))
            return newDefault;
        return previousExternalDefault.HasValue && external.Any(stream =>
            stream.Type == type && stream.Index == previousExternalDefault)
            ? previousExternalDefault
            : null;
    }

    private static int FindAvailableStreamIndex(HashSet<int> usedIndices)
    {
        for (var index = 0; index < int.MaxValue; index++)
        {
            if (!usedIndices.Contains(index)) return index;
        }
        throw new InvalidDataException("No media stream index is available.");
    }

    private static void RestoreExternalStreamIndices(IEnumerable<ExternalStreamIndexChange> changes)
    {
        foreach (var change in changes) change.Stream.Index = change.PreviousIndex;
    }

    private ItemTechnicalState ClearTechnicalFields(BaseItem item)
    {
        var previous = ItemTechnicalState.Capture(item, GetStoredMediaStreams(item));
        try
        {
            item.Container = "strm";
            item.RunTimeTicks = null;
            item.Size = 0;
            item.TotalBitrate = 0;
            item.AudioStreamIndex = null;
            item.SubtitleStreamIndex = null;
            item.MediaStreams = (previous.MediaStreams ?? new List<MediaStream>())
                .Where(stream => stream.IsExternal)
                .ToList();
            PersistTechnicalFields(item);
            return previous;
        }
        catch
        {
            previous.Restore(item);
            TryRestoreTechnicalFields(item, previous);
            throw;
        }
    }

    private List<MediaStream> GetStoredMediaStreams(BaseItem item)
    {
        var hydrated = item.MediaStreams ?? new List<MediaStream>();
        var repository = mediaSourceManager.GetMediaStreams(item);
        if (repository is null || repository.Count == 0) return hydrated.ToList();

        var merged = repository.ToList();
        foreach (var stream in hydrated)
        {
            if (!merged.Any(existing => IsSameStoredStream(existing, stream))) merged.Add(stream);
        }
        return merged;
    }

    private static bool IsSameStoredStream(MediaStream existing, MediaStream candidate)
    {
        if (ReferenceEquals(existing, candidate)) return true;
        if (existing.IsExternal != candidate.IsExternal || existing.Type != candidate.Type) return false;
        if (existing.IsExternal) return IsSameExternalStream(existing, candidate);

        // Prefer the repository version when both sides describe the same typed internal
        // slot. Retain conflicting types and ambiguous unindexed streams rather than losing
        // them from a rollback snapshot.
        return existing.Index >= 0 && candidate.Index >= 0 && existing.Index == candidate.Index;
    }

    private static bool IsSameExternalStream(MediaStream existing, MediaStream candidate)
    {
        if (ReferenceEquals(existing, candidate)) return true;
        if (!existing.IsExternal || !candidate.IsExternal || existing.Type != candidate.Type) return false;
        var existingHasPath = !string.IsNullOrWhiteSpace(existing.Path);
        var candidateHasPath = !string.IsNullOrWhiteSpace(candidate.Path);
        var bothHavePaths = existingHasPath && candidateHasPath;
        if (bothHavePaths && ExternalPathEquals(existing.Path!, candidate.Path!)) return true;
        var existingHasDeliveryUrl = !string.IsNullOrWhiteSpace(existing.DeliveryUrl);
        var candidateHasDeliveryUrl = !string.IsNullOrWhiteSpace(candidate.DeliveryUrl);
        var bothHaveDeliveryUrls = existingHasDeliveryUrl && candidateHasDeliveryUrl;
        if (bothHaveDeliveryUrls && string.Equals(
                existing.DeliveryUrl, candidate.DeliveryUrl, StringComparison.Ordinal))
            return true;
        if (existingHasPath || candidateHasPath || existingHasDeliveryUrl || candidateHasDeliveryUrl) return false;
        return existing.Index >= 0 && candidate.Index >= 0 && existing.Index == candidate.Index;
    }

    private static bool ExternalPathEquals(string first, string second)
    {
        if (Uri.TryCreate(first, UriKind.Absolute, out var firstUri) && !firstUri.IsFile ||
            Uri.TryCreate(second, UriKind.Absolute, out var secondUri) && !secondUri.IsFile)
            return string.Equals(first, second, StringComparison.Ordinal);
        try
        {
            if (Path.IsPathRooted(first) && Path.IsPathRooted(second))
            {
                return string.Equals(
                    Path.GetFullPath(first),
                    Path.GetFullPath(second),
                    Path.DirectorySeparatorChar == '\\'
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException || exception is NotSupportedException ||
            exception is PathTooLongException || exception is IOException ||
            exception is System.Security.SecurityException)
        {
            return false;
        }
        return string.Equals(first, second, StringComparison.Ordinal);
    }

    private void PersistTechnicalFields(BaseItem item)
    {
        var mediaStreams = (item.MediaStreams ?? new List<MediaStream>()).ToList();
        var parent = libraryManager.GetItemById(item.ParentId) ?? item.GetParent()
            ?? throw new InvalidOperationException("The media item has no library parent.");
        libraryManager.UpdateItems(
            new List<BaseItem> { item },
            parent,
            ItemUpdateType.MetadataImport,
            setDateLastSaved: true,
            saveMetadata: false,
            metadataRefreshOptions: null!,
            CancellationToken.None);
        item.MediaStreams = mediaStreams;
        itemRepository.SaveMediaStreams(
            item.InternalId,
            mediaStreams,
            CancellationToken.None);
    }

    private void TryRestoreTechnicalFields(BaseItem item, ItemTechnicalState previous)
    {
        try
        {
            var mediaStreams = previous.MediaStreams ?? new List<MediaStream>();
            var parent = libraryManager.GetItemById(item.ParentId) ?? item.GetParent();
            if (parent is not null)
            {
                libraryManager.UpdateItems(
                    new List<BaseItem> { item },
                    parent,
                    ItemUpdateType.MetadataImport,
                    setDateLastSaved: true,
                    saveMetadata: false,
                    metadataRefreshOptions: null!,
                    CancellationToken.None);
            }
            item.MediaStreams = mediaStreams;
            itemRepository.SaveMediaStreams(
                item.InternalId,
                mediaStreams,
                CancellationToken.None);
        }
        catch
        {
            logger.Warn("STRM_BRIDGE_MEDIAINFO_ROLLBACK_FAILED item=" + ShortId(item.Id));
        }
    }

    private bool BaselineCompleteItem(BaseItem item, SourceIdentity source, int operationGeneration)
    {
        var recorded = false;
        try
        {
            var committed = runtime.TryCommitMediaInfo(
                operationGeneration,
                () => IsCurrentlyAllowed(item, requirePlayback: false),
                () =>
                {
                    var currentSource = runtime.SourcePolicy!.Read(item.Path);
                    if (source.HasSameFileVersion(currentSource))
                    {
                        recorded = runtime.ExtractionState!.TryRecordBaseline(
                            source.StorageKey,
                            source.SourceFingerprint);
                    }
                });
            return committed && recorded;
        }
        catch (SourcePolicyException)
        {
            return false;
        }
    }

    private void RecordFailureIfCurrent(
        SourceIdentity source,
        int operationGeneration,
        ConcurrentDictionary<string, byte> recordedSourceFailures)
    {
        // One shared probe failure can fan out to duplicate BaseItems. Persistent retry state
        // advances at most once per local path and source version during a run; distinct paths
        // that share identical STRM contents still receive independent attribution.
        var failureKey = source.StorageKey + source.SourceFingerprint;
        if (!recordedSourceFailures.TryAdd(failureKey, 0)) return;
        runtime.TryCommitMediaInfo(
            operationGeneration,
            () => true,
            () => runtime.ExtractionState!.RecordFailure(
                source.StorageKey,
                source.SourceFingerprint,
                runtime.Clock.UtcNow));
    }

    private BaseItem[] GetCandidates(
        string[] includedLibraryIds,
        CancellationToken cancellationToken,
        bool includeAllLibraries = false)
    {
        var allowed = new HashSet<string>(
            (includedLibraryIds ?? Array.Empty<string>())
                .Where(value => Guid.TryParse(value, out _))
                .Select(value => Guid.Parse(value).ToString("N")),
            StringComparer.OrdinalIgnoreCase);
        if (allowed.Count == 0 && !includeAllLibraries) return Array.Empty<BaseItem>();
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsFolder = false,
            MediaTypes = new[] { MediaType.Video, MediaType.Audio },
            HasPath = true,
            EnforceExtraType = false,
        };
        return libraryManager.GetItemList(query, cancellationToken)
            .Where(item => !string.IsNullOrWhiteSpace(item.Path) &&
                           Path.IsPathRooted(item.Path) &&
                           string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase))
            .Where(item => includeAllLibraries || libraryManager.GetCollectionFolders(item, cancellationToken)
                .Any(folder => allowed.Contains(folder.Id.ToString("N"))))
            .ToArray();
    }

    private bool IsMissing(BaseItem item, MediaStreamType requiredStreamType)
    {
        // A hydrated required stream is already authoritative for the same completeness
        // rule used after a repository read. Avoid making a complete item depend on a second
        // database call that can be temporarily unavailable.
        var hasRequiredStream = HasInternalStream(item.MediaStreams, requiredStreamType);
        if (!hasRequiredStream)
            hasRequiredStream = HasInternalStream(mediaSourceManager.GetMediaStreams(item), requiredStreamType);
        return !TechnicalMediaInfo.IsComplete(item.Container, item.RunTimeTicks, hasRequiredStream);
    }

    private static bool IsRecoverableItemFailure(Exception exception) =>
        exception is not OutOfMemoryException &&
        exception is not StackOverflowException &&
        exception is not AccessViolationException &&
        exception is not AppDomainUnloadedException;

    private static bool HasInternalStream(
        IEnumerable<MediaStream>? streams,
        MediaStreamType requiredStreamType) =>
        streams?.Any(stream => stream.Type == requiredStreamType && !stream.IsExternal) == true;

    private static bool IsComplete(MediaSourceInfo source, MediaStreamType requiredStreamType) =>
        TechnicalMediaInfo.IsComplete(
            source.Container,
            source.RunTimeTicks,
            HasInternalStream(source.MediaStreams, requiredStreamType));

    private static MediaStreamType GetRequiredStreamType(BaseItem item, Uri sourceUri) =>
        string.Equals(item.MediaType, MediaType.Audio, StringComparison.OrdinalIgnoreCase) || IsAudioSource(sourceUri)
            ? MediaStreamType.Audio
            : MediaStreamType.Video;

    private static bool IsAudioSource(Uri sourceUri) =>
        MimeTypes.GetMimeType(sourceUri.AbsolutePath)
            .StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    private bool IsCurrentlyAllowed(BaseItem item, bool requirePlayback) =>
        IsAllowed(item, optionsProvider().Snapshot(), requirePlayback);

    private bool IsAllowed(BaseItem item, PluginConfiguration options, bool requirePlayback)
    {
        if (!options.Enabled || requirePlayback && options.PlaybackMode == PlaybackRoutingMode.Native) return false;
        if (options.IncludedLibraryIds.Length == 0) return false;
        var allowed = new HashSet<string>(options.IncludedLibraryIds, StringComparer.OrdinalIgnoreCase);
        return libraryManager.GetCollectionFolders(item)
            .Any(folder => allowed.Contains(folder.Id.ToString("N")));
    }

    private static string NormalizeLibraryId(string value) =>
        Guid.TryParse(value, out var id) ? id.ToString("N") : value.Trim();

    private static string ShortId(Guid id) => id.ToString("N").Substring(0, 8);

    public void CancelActive()
    {
        foreach (var cancellation in activeItems.Keys)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void FlushExtractionState()
    {
        try { runtime.ExtractionState?.Flush(); }
        catch (Exception exception) when (
            exception is IOException || exception is UnauthorizedAccessException ||
            exception is InvalidDataException || exception is System.Runtime.Serialization.SerializationException)
        {
            logger.Debug("STRM_BRIDGE_STATE_SAVE_FAILED");
        }
    }

    private void LogExtractionCompleted(ExtractionResult result, PluginConfiguration options)
    {
        var detectedHostCount = runtime.GetDetectedRedirectHosts(options.AllowedRedirectHosts).Length;
        logger.Info("STRM_BRIDGE_EXTRACTION_COMPLETED total=" + result.Total +
                    " extracted=" + result.Extracted +
                    " restored=" + result.Restored +
                    " skipped=" + result.Skipped +
                    " failed=" + result.Failed +
                    " awaiting_trust=" + result.AwaitingApproval +
                    " detected_hosts=" + detectedHostCount);
    }

    private async Task NotifyAwaitingApprovalAsync(CancellationToken cancellationToken)
    {
        var options = optionsProvider().Snapshot();
        var detectedHostCount = runtime.GetDetectedRedirectHosts(options.AllowedRedirectHosts).Length;
        if (detectedHostCount == 0 || !runtime.TryBeginPendingHostNotification()) return;
        var description = string.Format(
            CultureInfo.CurrentUICulture,
            PluginStrings.AwaitingApprovalNotificationDescription,
            detectedHostCount);
        var dashboardRecorded = TryCreateActivity(
            PluginStrings.AwaitingApprovalNotificationTitle,
            description,
            "StrmBridgeAwaitingApproval",
            LogSeverity.Warn);
        var externalNotificationAccepted = false;
        try
        {
            if (notificationManager is not null)
            {
                await SendAdminNotificationAsync(
                        new NotificationRequest
                        {
                            Name = PluginStrings.AwaitingApprovalNotificationTitle,
                            Description = description,
                            Url = GetConfigurationPageUrl(),
                            Level = NotificationLevel.Warning,
                            SendToUserMode = SendToUserType.Admins,
                            Date = runtime.Clock.UtcNow,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                externalNotificationAccepted = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!dashboardRecorded) runtime.AbandonPendingHostNotification();
            throw;
        }
        catch (Exception exception)
        {
            logger.Warn("STRM Bridge could not send the pending-host administrator notification. error=" +
                        exception.GetType().Name);
        }
        if (!dashboardRecorded && !externalNotificationAccepted)
            runtime.AbandonPendingHostNotification();
    }

    private async Task NotifyTrustRetryCompletedAsync(
        ExtractionResult result,
        CancellationToken cancellationToken)
    {
        var options = optionsProvider().Snapshot();
        if (runtime.GetDetectedRedirectHosts(options.AllowedRedirectHosts).Length > 0) return;
        var description = string.Format(
            CultureInfo.CurrentUICulture,
            PluginStrings.RetryCompletedNotificationDescription,
            result.Extracted,
            result.Restored,
            result.Skipped,
            result.Failed);
        TryCreateActivity(
            PluginStrings.RetryCompletedNotificationTitle,
            description,
            "StrmBridgeTrustRetryCompleted",
            result.Failed > 0 ? LogSeverity.Warn : LogSeverity.Info);
        if (notificationManager is null) return;
        try
        {
            await SendAdminNotificationAsync(
                    new NotificationRequest
                    {
                        Name = PluginStrings.RetryCompletedNotificationTitle,
                        Description = description,
                        Url = GetConfigurationPageUrl(),
                        Level = result.Failed > 0 ? NotificationLevel.Warning : NotificationLevel.Normal,
                        SendToUserMode = SendToUserType.Admins,
                        Date = runtime.Clock.UtcNow,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.Warn("STRM Bridge could not send the automatic-retry administrator notification. error=" +
                        exception.GetType().Name);
        }
    }

    private bool TryCreateActivity(string name, string overview, string type, LogSeverity severity)
    {
        if (activityManager is null) return false;
        try
        {
            activityManager.Create(new ActivityLogEntry
            {
                Name = name,
                Overview = overview,
                ShortOverview = overview,
                Type = type,
                Date = runtime.Clock.UtcNow,
                Severity = severity,
            });
            return true;
        }
        catch (Exception exception)
        {
            logger.Warn("STRM Bridge could not create the administrator activity entry. error=" +
                        exception.GetType().Name);
            return false;
        }
    }

    private static string GetConfigurationPageUrl() =>
        "configurationpage?name=" + Uri.EscapeDataString(typeof(Plugin).Assembly.GetName().Name!);

    private async Task SendAdminNotificationAsync(
        NotificationRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AdministratorNotificationTimeout);
        // Emby 4.9's compatibility overload is the public contract that retains explicit Admins targeting.
#pragma warning disable CS0618
        var sendTask = notificationManager!.SendNotification(request, timeout.Token);
#pragma warning restore CS0618
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
        if (await Task.WhenAny(sendTask, timeoutTask).ConfigureAwait(false) == sendTask)
        {
            timeout.Cancel();
            await sendTask.ConfigureAwait(false);
            return;
        }
        ObserveFault(sendTask);
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException("The administrator notification provider exceeded its time limit.");
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task AwaitWithCancellation(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancellation.TrySetResult(true)))
        {
            if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
            {
                ObserveFault(task);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }
        }
        await task.ConfigureAwait(false);
    }

    public void CancelAndDrain()
    {
        CancelActive();
        Task? pending;
        lock (backgroundSync) pending = backgroundTask;
        try { pending?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        runGate.Wait();
        runGate.Release();
    }

    private sealed class ItemTechnicalState
    {
        private string? Container { get; set; }
        private long? RunTimeTicks { get; set; }
        private long Size { get; set; }
        private int TotalBitrate { get; set; }
        public int? AudioStreamIndex { get; private set; }
        public int? SubtitleStreamIndex { get; private set; }
        public List<MediaStream>? MediaStreams { get; private set; }

        public static ItemTechnicalState Capture(BaseItem item, List<MediaStream> mediaStreams) => new()
        {
            Container = item.Container,
            RunTimeTicks = item.RunTimeTicks,
            Size = item.Size,
            TotalBitrate = item.TotalBitrate,
            AudioStreamIndex = item.AudioStreamIndex,
            SubtitleStreamIndex = item.SubtitleStreamIndex,
            MediaStreams = mediaStreams,
        };

        public void Restore(BaseItem item)
        {
            item.Container = Container;
            item.RunTimeTicks = RunTimeTicks;
            item.Size = Size;
            item.TotalBitrate = TotalBitrate;
            item.AudioStreamIndex = AudioStreamIndex;
            item.SubtitleStreamIndex = SubtitleStreamIndex;
            item.MediaStreams = MediaStreams;
        }
    }

    private sealed class ExternalStreamIndexChange
    {
        public ExternalStreamIndexChange(MediaStream stream, int previousIndex)
        {
            Stream = stream;
            PreviousIndex = previousIndex;
        }

        public MediaStream Stream { get; }
        public int PreviousIndex { get; }
    }

    private sealed class ProbeFlightCache
    {
        private readonly object sync = new();
        private readonly int capacity;
        private readonly Dictionary<string, Lazy<Task<ProbeResult>>> flights = new(StringComparer.Ordinal);

        public ProbeFlightCache(int capacity) => this.capacity = capacity;

        public Lazy<Task<ProbeResult>>? TryGetOrAdd(
            string key,
            Func<Lazy<Task<ProbeResult>>> factory,
            Func<bool> allowCreate,
            out bool retained)
        {
            lock (sync)
            {
                if (flights.TryGetValue(key, out var existing))
                {
                    retained = true;
                    return existing;
                }
                if (!allowCreate())
                {
                    retained = false;
                    return null;
                }
                var flight = factory();
                if (flights.Count >= capacity)
                {
                    retained = false;
                    return flight;
                }
                flights.Add(key, flight);
                retained = true;
                return flight;
            }
        }

        public void Remove(string key, Lazy<Task<ProbeResult>> flight)
        {
            lock (sync)
            {
                if (flights.TryGetValue(key, out var found) && ReferenceEquals(found, flight))
                    flights.Remove(key);
            }
        }
    }

    private sealed class ProbeResult
    {
        public ProbeResult(MediaSourceInfo mediaSource) => MediaSource = mediaSource;

        public MediaSourceInfo MediaSource { get; }
    }

    private sealed class ProbeInputNotOpenedException : Exception
    {
        public ProbeInputNotOpenedException(bool circuitOpened) => CircuitOpened = circuitOpened;

        public ProbeInputNotOpenedException(bool circuitOpened, Exception innerException)
            : base("The media probe did not open its loopback input.", innerException) =>
            CircuitOpened = circuitOpened;

        public bool CircuitOpened { get; }
    }

    private sealed class ProbeLocalFailureException : Exception
    {
        public ProbeLocalFailureException()
            : this(false) { }

        public ProbeLocalFailureException(Exception innerException)
            : this(false, innerException) { }

        public ProbeLocalFailureException(bool circuitOpened)
            : base("The local media-probe path failed.") => CircuitOpened = circuitOpened;

        public ProbeLocalFailureException(bool circuitOpened, Exception innerException)
            : base("The local media-probe path failed.", innerException) => CircuitOpened = circuitOpened;

        public bool CircuitOpened { get; }
    }

}

internal sealed class ProbeRunCircuit
{
    private const int ConsecutiveFailureThreshold = 3;
    private readonly object sync = new();
    private readonly Action? beforeCircuitOpen;
    private int consecutiveInputNotOpened;
    private int consecutiveFallbackLocalFailures;
    private bool open;
    private bool fallbackLocalFailureOpen;
    private bool preferFallbackProbe;

    public ProbeRunCircuit(Action? beforeCircuitOpen = null) =>
        this.beforeCircuitOpen = beforeCircuitOpen;

    public bool IsOpen
    {
        get { lock (sync) return open || fallbackLocalFailureOpen; }
    }

    public bool PreferFallbackProbe
    {
        get { lock (sync) return preferFallbackProbe; }
    }

    /// <returns><see langword="true"/> only for the first activation in this run.</returns>
    public bool PreferFallback()
    {
        lock (sync)
        {
            if (preferFallbackProbe) return false;
            preferFallbackProbe = true;
            return true;
        }
    }

    public bool RecordInputNotOpened()
    {
        lock (sync)
        {
            if (open) return true;
            consecutiveInputNotOpened++;
            if (consecutiveInputNotOpened < ConsecutiveFailureThreshold) return false;
            beforeCircuitOpen?.Invoke();
            open = true;
            return true;
        }
    }

    public void RecordInputOpened()
    {
        lock (sync)
        {
            consecutiveInputNotOpened = 0;
            open = false;
        }
    }

    public bool RecordFallbackLocalFailure()
    {
        lock (sync)
        {
            if (fallbackLocalFailureOpen) return true;
            consecutiveFallbackLocalFailures++;
            if (consecutiveFallbackLocalFailures < ConsecutiveFailureThreshold) return false;
            beforeCircuitOpen?.Invoke();
            fallbackLocalFailureOpen = true;
            return true;
        }
    }

    public void RecordFallbackSuccess()
    {
        lock (sync)
        {
            consecutiveFallbackLocalFailures = 0;
            fallbackLocalFailureOpen = false;
        }
    }
}

public enum ExtractionOutcome
{
    Extracted,
    Restored,
    Skipped,
    AwaitingApproval,
    Failed,
}

public sealed class ExtractionResult
{
    private int extracted;
    private int restored;
    private int skipped;
    private int awaitingApproval;
    private int failed;

    public int Total { get; internal set; }
    public int Extracted => extracted;
    public int Restored => restored;
    public int Skipped => skipped;
    public int AwaitingApproval => awaitingApproval;
    public int Failed => failed;

    internal void Add(ExtractionOutcome outcome)
    {
        switch (outcome)
        {
            case ExtractionOutcome.Extracted: Interlocked.Increment(ref extracted); break;
            case ExtractionOutcome.Restored: Interlocked.Increment(ref restored); break;
            case ExtractionOutcome.Skipped: Interlocked.Increment(ref skipped); break;
            case ExtractionOutcome.AwaitingApproval: Interlocked.Increment(ref awaitingApproval); break;
            case ExtractionOutcome.Failed: Interlocked.Increment(ref failed); break;
        }
    }
}

public sealed class ClearStoredMediaInfoResult
{
    private int cleared;
    private int failed;

    public int Total { get; internal set; }
    public int Cleared => cleared;
    public int Failed => failed;
    public int SnapshotsRemoved { get; internal set; }
    public int StateEntriesRemoved { get; internal set; }

    internal void AddCleared() => Interlocked.Increment(ref cleared);

    internal void AddFailed() => Interlocked.Increment(ref failed);
}
