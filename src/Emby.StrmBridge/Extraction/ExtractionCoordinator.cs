using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Localization;
using Emby.StrmBridge.Policy;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
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
    private readonly SemaphoreSlim runGate = new(1, 1);
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
        IActivityManager activityManager)
        : this(
            runtime,
            libraryManager,
            mediaSourceManager,
            itemRepository,
            logManager,
            runtime.GetOptionsSnapshot,
            notificationManager,
            activityManager)
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
        IActivityManager? activityManager = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        this.mediaSourceManager = mediaSourceManager ?? throw new ArgumentNullException(nameof(mediaSourceManager));
        this.itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
        this.notificationManager = notificationManager;
        this.activityManager = activityManager;
        this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
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
            var redirectDiscovery = new RedirectDiscoveryBudget();
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
                                redirectDiscovery,
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
                try { keep.Add(runtime.SourcePolicy!.Read(item.Path).StorageKey); }
                catch (SourcePolicyException) { }
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
        RedirectDiscoveryBudget redirectDiscovery,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentlyAllowed(item, requirePlayback: false)) return ExtractionOutcome.Skipped;
        SourceIdentity source;
        try { source = runtime.SourcePolicy!.Read(item.Path); }
        catch (SourcePolicyException)
        {
            logger.Debug("STRM_BRIDGE_SOURCE_REJECTED item=" + ShortId(item.Id));
            return ExtractionOutcome.Skipped;
        }
        if (!StaticMediaSourcePolicy.Matches(mediaSourceManager, item, source))
        {
            logger.Debug("STRM_BRIDGE_STATIC_SOURCE_REJECTED item=" + ShortId(item.Id));
            return ExtractionOutcome.Skipped;
        }

        var missing = IsMissing(item);
        var lastSuccessfulFingerprint = runtime.ExtractionState!.GetLastSuccessfulFingerprint(source.StorageKey);
        var shouldAttempt = force || !options.OnlyMissingMediaInfo || runtime.ExtractionState.ShouldAttempt(
            source.StorageKey,
            source.SourceFingerprint,
            runtime.Clock.UtcNow);
        if (!force && options.OnlyMissingMediaInfo && !missing)
        {
            if (lastSuccessfulFingerprint is null)
            {
                if (options.EnablePersistence && runtime.MediaInfoStore!.TryLoad(source, out _))
                {
                    BaselineCompleteItem(item, source, operationGeneration);
                    return ExtractionOutcome.Skipped;
                }
                if (BaselineCompleteItem(item, source, operationGeneration))
                    return ExtractionOutcome.Skipped;
            }
            if (string.Equals(lastSuccessfulFingerprint, source.SourceFingerprint, StringComparison.Ordinal))
                return ExtractionOutcome.Skipped;
        }

        if (!shouldAttempt)
        {
            var pendingHosts = runtime.GetDetectedRedirectHosts(options.AllowedRedirectHosts);
            if (pendingHosts.Length > 0 || !redirectDiscovery.TryAcquire())
                return ExtractionOutcome.Skipped;
        }

        if (!force && options.OnlyMissingMediaInfo && options.EnablePersistence &&
            runtime.MediaInfoStore!.TryLoad(source, out var stored))
        {
            try
            {
                if (TryApply(
                        item,
                        source,
                        stored!.ToMediaSource("strmbridge-restored"),
                        operationGeneration,
                        saveSnapshot: null))
                {
                    logger.Debug("STRM_BRIDGE_MEDIAINFO_RESTORED item=" + ShortId(item.Id));
                    return ExtractionOutcome.Restored;
                }
                return ExtractionOutcome.Skipped;
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                RecordFailureIfCurrent(source, operationGeneration);
                logger.Debug("STRM_BRIDGE_RESTORE_FAILED item=" + ShortId(item.Id));
                return ExtractionOutcome.Failed;
            }
        }

        try
        {
            var probe = await ProbeSharedAsync(
                    source,
                    options.ExtractionTimeoutSeconds,
                    sourceFlights,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
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
            logger.Debug("STRM_BRIDGE_MEDIAINFO_EXTRACTED item=" + ShortId(item.Id));
            return ExtractionOutcome.Extracted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RedirectRejectedException exception) when (
            exception.Reason == RedirectRejectionReason.UntrustedTargetHost)
        {
            RecordFailureIfCurrent(source, operationGeneration);
            logger.Debug("STRM_BRIDGE_EXTRACTION_AWAITING_TRUST item=" + ShortId(item.Id));
            return ExtractionOutcome.AwaitingApproval;
        }
        catch (Exception exception)
        {
            RecordFailureIfCurrent(source, operationGeneration);
            logger.Debug("STRM_BRIDGE_EXTRACTION_FAILED item=" + ShortId(item.Id) +
                         " reason=" + GetFailureReason(exception));
            return ExtractionOutcome.Failed;
        }
    }

    private static string GetFailureReason(Exception exception)
    {
        if (exception is Emby.StrmBridge.Playback.RedirectSourceUnavailableException)
            return "source_unavailable";
        if (exception is RedirectRejectedException rejected)
            return "redirect_" + rejected.Reason.ToString().ToLowerInvariant();
        if (exception is InvalidDataException) return "probe_incomplete";
        if (exception is System.Net.Http.HttpRequestException) return "probe_http";
        if (exception is TaskCanceledException) return "timeout";
        return "probe_or_save";
    }

    private async Task<ProbeResult> ProbeAsync(
        SourceIdentity source,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        activeItems.TryAdd(timeout, 0);
        try
        {
            var lease = await runtime.Redirects!.ResolveForProbeAsync(source, ProbeUserAgent, timeout.Token)
                .ConfigureAwait(false);
            var target = lease.GetLocation();
            var mediaSource = new MediaSourceInfo
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
            await mediaSourceManager.AddMediaInfoWithProbeSafe(
                    mediaSource,
                    isAudio: false,
                    addProbeDelay: false,
                    timeout.Token)
                .ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (mediaSource.MediaStreams is null ||
                !mediaSource.MediaStreams.Any(stream => stream.Type == MediaStreamType.Video) ||
                !mediaSource.RunTimeTicks.HasValue)
                throw new InvalidDataException("The media probe returned incomplete technical information.");
            return new ProbeResult(mediaSource);
        }
        finally
        {
            activeItems.TryRemove(timeout, out _);
        }
    }

    private async Task<ProbeResult> ProbeSharedAsync(
        SourceIdentity source,
        int timeoutSeconds,
        ProbeFlightCache sourceFlights,
        CancellationToken cancellationToken)
    {
        var cached = sourceFlights.TryGetOrAdd(
            source.SourceFingerprint,
            () => new Lazy<Task<ProbeResult>>(
                () => ProbeAsync(source, timeoutSeconds, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication),
            out var flight);
        try
        {
            return await flight.Value.ConfigureAwait(false);
        }
        catch
        {
            if (cached) sourceFlights.Remove(source.SourceFingerprint, flight);
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
        return runtime.TryCommit(
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
                    try { runtime.MediaInfoStore!.Save(source, saveSnapshot); }
                    catch (Exception exception) when (
                        exception is IOException || exception is UnauthorizedAccessException ||
                        exception is InvalidDataException || exception is System.Runtime.Serialization.SerializationException)
                    {
                        logger.Debug("STRM_BRIDGE_SNAPSHOT_SAVE_FAILED item=" + ShortId(item.Id));
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
            if (source.Size.HasValue && source.Size.Value > 0) item.Size = source.Size.Value;
            if (source.Bitrate.HasValue && source.Bitrate.Value > 0) item.TotalBitrate = source.Bitrate.Value;
            if (source.MediaStreams is { Count: > 0 })
            {
                var internalStreams = source.MediaStreams
                    .Where(stream => !stream.IsExternal && MediaInfoSnapshot.IsSupportedStreamType(stream.Type))
                    .Take(MediaInfoSnapshot.MaximumMediaStreams)
                    .ToList();
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
                if (!source.DefaultAudioStreamIndex.HasValue && remappedAudioIndex.HasValue)
                    item.AudioStreamIndex = remappedAudioIndex;
                if (!source.DefaultSubtitleStreamIndex.HasValue && remappedSubtitleIndex.HasValue)
                    item.SubtitleStreamIndex = remappedSubtitleIndex;
            }
            if (source.DefaultAudioStreamIndex.HasValue) item.AudioStreamIndex = source.DefaultAudioStreamIndex;
            if (source.DefaultSubtitleStreamIndex.HasValue) item.SubtitleStreamIndex = source.DefaultSubtitleStreamIndex;
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

    private List<MediaStream> GetStoredMediaStreams(BaseItem item) =>
        (mediaSourceManager.GetMediaStreams(item) ?? item.MediaStreams ?? new List<MediaStream>()).ToList();

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
        var committed = runtime.TryCommit(
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

    private void RecordFailureIfCurrent(SourceIdentity source, int operationGeneration)
    {
        runtime.TryCommit(
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
            MediaTypes = new[] { MediaType.Video },
            HasPath = true,
        };
        return libraryManager.GetItemList(query, cancellationToken)
            .Where(item => !string.IsNullOrWhiteSpace(item.Path) &&
                           Path.IsPathRooted(item.Path) &&
                           string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase))
            .Where(item => includeAllLibraries || libraryManager.GetCollectionFolders(item, cancellationToken)
                .Any(folder => allowed.Contains(folder.Id.ToString("N"))))
            .ToArray();
    }

    private bool IsMissing(BaseItem item)
    {
        var streams = mediaSourceManager.GetMediaStreams(item);
        return streams.All(stream => stream.Type != MediaStreamType.Video) ||
               !item.RunTimeTicks.HasValue || item.RunTimeTicks.Value < TimeSpan.FromSeconds(1).Ticks ||
               string.IsNullOrWhiteSpace(item.Container) ||
               string.Equals(item.Container, "strm", StringComparison.OrdinalIgnoreCase);
    }

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

        public bool TryGetOrAdd(
            string key,
            Func<Lazy<Task<ProbeResult>>> factory,
            out Lazy<Task<ProbeResult>> flight)
        {
            lock (sync)
            {
                if (flights.TryGetValue(key, out flight!)) return true;
                flight = factory();
                if (flights.Count >= capacity) return false;
                flights.Add(key, flight);
                return true;
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

    private sealed class RedirectDiscoveryBudget
    {
        private int acquired;

        public bool TryAcquire() => Interlocked.CompareExchange(ref acquired, 1, 0) == 0;
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
