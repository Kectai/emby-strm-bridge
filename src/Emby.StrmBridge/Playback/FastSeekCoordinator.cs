using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Playback;

internal sealed class FastSeekPlan
{
    public FastSeekPlan(
        long targetTimeTicks,
        long totalLength,
        long byteOffset,
        int packetStride,
        long durationTicks,
        TimeSpan relativeSeek,
        int probeCount,
        int runtimeGeneration,
        DateTimeOffset expiresAtUtc,
        int? videoStreamIndex = null,
        FastSeekRepresentation? representation = null)
    {
        TargetTimeTicks = targetTimeTicks;
        TotalLength = totalLength;
        ByteOffset = byteOffset;
        PacketStride = packetStride;
        DurationTicks = durationTicks;
        RelativeSeek = relativeSeek;
        ProbeCount = probeCount;
        RuntimeGeneration = runtimeGeneration;
        ExpiresAtUtc = expiresAtUtc;
        VideoStreamIndex = videoStreamIndex;
        Representation = representation;
    }

    public long TargetTimeTicks { get; }

    public long TotalLength { get; }

    public long ByteOffset { get; }

    public int PacketStride { get; }

    public long DurationTicks { get; }

    public TimeSpan RelativeSeek { get; }

    public int ProbeCount { get; }

    public int RuntimeGeneration { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public int? VideoStreamIndex { get; }

    public FastSeekRepresentation? Representation { get; }
}

internal sealed class FastSeekProbeResult
{
    public FastSeekProbeResult(long rangeStart, long totalLength, byte[] bytes,
        FastSeekRepresentation? representation = null,
        TimeSpan? setupDuration = null,
        TimeSpan? bodyDuration = null)
    {
        RangeStart = rangeStart;
        TotalLength = totalLength;
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        Representation = representation;
        SetupDuration = setupDuration;
        BodyDuration = bodyDuration;
    }

    public long RangeStart { get; }

    public long TotalLength { get; }

    public byte[] Bytes { get; }

    public FastSeekRepresentation? Representation { get; }

    public TimeSpan? SetupDuration { get; }

    public TimeSpan? BodyDuration { get; }
}

internal interface IFastSeekProbeClient
{
    Task<FastSeekProbeResult> ReadAsync(
        SourceIdentity source,
        long offset,
        int maximumBytes,
        PluginConfiguration options,
        CancellationToken cancellationToken);
}

internal sealed class GatewayFastSeekProbeClient : IFastSeekProbeClient
{
    private readonly GatewayTransport transport;

    public GatewayFastSeekProbeClient(GatewayTransport transport) =>
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async Task<FastSeekProbeResult> ReadAsync(
        SourceIdentity source,
        long offset,
        int maximumBytes,
        PluginConfiguration options,
        CancellationToken cancellationToken)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (source.SourceFingerprint.Length < 32)
            throw new FastSeekProbeException(FastSeekSkipReason.Unavailable);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (maximumBytes < 1 || maximumBytes > FastSeekCoordinator.MaximumProbeBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var end = checked(offset + maximumBytes - 1L);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Range"] = "bytes=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        "-" + end.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Accept"] = "*/*",
        };
        var scope = GatewayTransport.CreateRedirectCandidateScope(source);
        var stopwatch = Stopwatch.StartNew();
        using var lease = await transport.OpenProbeAsync(
                source.SourceUri,
                scope,
                scope,
                "GET",
                GatewayTransport.ProbeUserAgent,
                headers,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        var setupDuration = stopwatch.Elapsed;
        var response = lease.Response;
        var contentRange = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent ||
            contentRange is null ||
            !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            contentRange.From != offset ||
            !contentRange.To.HasValue || contentRange.To.Value < offset ||
            !contentRange.Length.HasValue || contentRange.Length.Value <= contentRange.To.Value ||
            response.Content.Headers.ContentEncoding.Any(value =>
                !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new FastSeekProbeException(FastSeekSkipReason.RangeResponse);
        var expectedLength = contentRange.To.Value - offset + 1;
        if (expectedLength > maximumBytes ||
            response.Content.Headers.ContentLength is long contentLength && contentLength != expectedLength)
            throw new FastSeekProbeException(FastSeekSkipReason.RangeResponse);
        var bodyStart = stopwatch.Elapsed;
        var prefix = await lease.PeekPrefixAsync((int)expectedLength, cancellationToken).ConfigureAwait(false);
        var bodyDuration = stopwatch.Elapsed - bodyStart;
        if (prefix.Length != expectedLength)
            throw new FastSeekProbeException(FastSeekSkipReason.TruncatedSample);
        FastSeekRepresentation.TryCreate(response, out var representation);
        return new FastSeekProbeResult(offset, contentRange.Length.Value, prefix.ToArray(), representation,
            setupDuration, bodyDuration);
    }
}

internal enum FastSeekSkipReason
{
    RangeResponse,
    TruncatedSample,
    TransportStructure,
    PcrMissing,
    StreamSelection,
    RandomAccessMissing,
    Timeline,
    ByteRate,
    Correction,
    Capacity,
    Cancelled,
    Unavailable,
    Representation,
}

internal sealed class FastSeekProbeException : Exception
{
    public FastSeekProbeException(FastSeekSkipReason reason) : base("Fast seek probe was rejected.") =>
        Reason = reason;

    public FastSeekSkipReason Reason { get; }
}

internal sealed class FastSeekCoordinator
{
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PreRoll = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan TargetScanDuration = FastSeekBudgetPolicy.TargetScanDuration;
    public static readonly TimeSpan MinimumTarget = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinimumRelativeSeek = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumRelativeSeek = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan MaximumInitialRelativeSeekWithoutCorrection = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan PreparationTimeout = FastSeekBudgetPolicy.PreparationTimeout;
    public const int InitialProbeBytes = FastSeekBudgetPolicy.InitialProbeBytes;
    public const int MaximumProbeBytes = FastSeekBudgetPolicy.MaximumProbeBytes;
    public const int MaximumTargetScanBytes = FastSeekBudgetPolicy.MaximumTargetScanBytes;
    public const int MaximumPreparationBytes = FastSeekBudgetPolicy.MaximumPreparationBytes;
    public const int MaximumCorrections = FastSeekBudgetPolicy.MaximumCorrections;
    public const int MaximumPlans = 512;
    public const int MaximumPendingPreparations = 64;
    private const double MinimumByteRate = 16 * 1024;
    private const double MaximumByteRate = 250 * 1024 * 1024;
    private readonly object sync = new();
    private readonly Dictionary<PreparationKey, FastSeekPlan> prepared = new();
    private readonly Dictionary<PreparationKey, FastSeekFailure> failed = new();
    private readonly Dictionary<string, FastSeekBinding> boundInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<PreparationKey, PendingFastSeek> pending = new();
    private ConditionalWeakTable<TicketPayload, FastSeekRepresentation> activeRepresentations = new();
    private readonly IFastSeekProbeClient probeClient;
    private readonly IClock clock;
    private readonly ILogger logger;
    private readonly Func<int>? runtimeGenerationProvider;

    public FastSeekCoordinator(
        IFastSeekProbeClient probeClient,
        IClock clock,
        ILogger logger,
        Func<int>? runtimeGenerationProvider = null)
    {
        this.probeClient = probeClient ?? throw new ArgumentNullException(nameof(probeClient));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.runtimeGenerationProvider = runtimeGenerationProvider;
    }

    public int Count
    {
        get
        {
            lock (sync)
            {
                RemoveExpiredUnsafe();
                return prepared.Count + boundInputs.Count;
            }
        }
    }

    public async Task<bool> PrepareAsync(
        SourceIdentity source,
        string mediaSourceId,
        long targetTimeTicks,
        long durationTicks,
        int runtimeGeneration,
        PluginConfiguration options,
        CancellationToken cancellationToken,
        FastSeekVideoSelection? videoSelection = null)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (string.IsNullOrWhiteSpace(mediaSourceId) || targetTimeTicks < MinimumTarget.Ticks ||
            durationTicks <= targetTimeTicks || durationTicks <= MinimumTarget.Ticks)
            return false;
        if (cancellationToken.IsCancellationRequested)
        {
            LogSkipped(FastSeekSkipReason.Cancelled);
            return false;
        }
        var key = new PreparationKey(
            source,
            mediaSourceId,
            targetTimeTicks,
            durationTicks,
            runtimeGeneration,
            videoSelection);
        PendingFastSeek? entry = null;
        FastSeekSkipReason? cachedFailure = null;
        var shouldStart = false;
        lock (sync)
        {
            RemoveExpiredUnsafe();
            if (prepared.ContainsKey(key)) return true;
            if (failed.TryGetValue(key, out var failure))
            {
                cachedFailure = failure.Reason;
            }
            else if (!pending.TryGetValue(key, out entry))
            {
                if (pending.Count >= MaximumPendingPreparations)
                {
                    LogSkipped(FastSeekSkipReason.Capacity);
                    return false;
                }
                entry = new PendingFastSeek(PreparationTimeout);
                pending.Add(key, entry);
                shouldStart = true;
            }
            entry?.AddWaiter();
        }

        if (cachedFailure.HasValue)
        {
            LogSkipped(cachedFailure.Value, cached: true);
            return false;
        }
        if (entry is null) return false;

        try
        {
            if (shouldStart)
                _ = CompleteInitialPreparationAsync(
                    key,
                    entry,
                    source,
                    targetTimeTicks,
                    durationTicks,
                    runtimeGeneration,
                    options,
                    videoSelection);

            FastSeekPlan? plan;
            try
            {
                using var waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, entry.CancellationToken);
                plan = await AwaitWithCancellation(entry.Task, waiterCancellation.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                entry.ThrowIfExpired();
            }
            catch (OperationCanceledException)
            {
                LogSkipped(FastSeekSkipReason.Cancelled);
                return false;
            }
            if (plan is null || plan.DurationTicks != durationTicks ||
                runtimeGenerationProvider is not null && runtimeGenerationProvider() != plan.RuntimeGeneration)
                return false;
            return true;
        }
        finally
        {
            ReleaseWaiter(key, entry);
        }
    }

    private void ReleaseWaiter(PreparationKey key, PendingFastSeek entry)
    {
        var cancel = false;
        lock (sync)
        {
            if (!entry.RemoveWaiter() || entry.Task.IsCompleted ||
                !pending.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
            pending.Remove(key);
            cancel = true;
        }
        if (cancel) entry.Cancel();
    }

    private async Task CompleteInitialPreparationAsync(
        PreparationKey key,
        PendingFastSeek entry,
        SourceIdentity source,
        long targetTimeTicks,
        long durationTicks,
        int runtimeGeneration,
        PluginConfiguration options,
        FastSeekVideoSelection? videoSelection)
    {
        FastSeekPlan? plan = null;
        FastSeekSkipReason? failureReason = null;
        try
        {
            plan = await PrepareCoreAsync(
                    source,
                    targetTimeTicks,
                    durationTicks,
                    runtimeGeneration,
                    options,
                    entry,
                    videoSelection)
                .ConfigureAwait(false);
            entry.ThrowIfExpired();
            if (plan is not null && (runtimeGenerationProvider is null ||
                                    runtimeGenerationProvider() == plan.RuntimeGeneration))
            {
                var stored = false;
                lock (sync)
                {
                    RemoveExpiredUnsafe();
                    if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    {
                        entry.ThrowIfExpired();
                        StorePreparedUnsafe(key, plan);
                        stored = true;
                    }
                }
                if (stored)
                    logger.Debug("STRM_BRIDGE_FAST_SEEK_READY packets=" + plan.PacketStride +
                                 " probes=" + plan.ProbeCount +
                                 (plan.VideoStreamIndex.HasValue ? " video_index=" + plan.VideoStreamIndex.Value : string.Empty));
                else
                    plan = null;
            }
            else
            {
                plan = null;
            }
        }
        catch (FastSeekProbeException exception)
        {
            failureReason = exception.Reason;
            plan = null;
        }
        catch (OperationCanceledException)
        {
            failureReason = FastSeekSkipReason.Cancelled;
            plan = null;
        }
        catch (Exception)
        {
            failureReason = FastSeekSkipReason.Unavailable;
            plan = null;
        }
        finally
        {
            lock (sync)
            {
                if (entry.IsExpired)
                {
                    if (plan is not null && prepared.TryGetValue(key, out var stored) &&
                        ReferenceEquals(stored, plan)) prepared.Remove(key);
                    failureReason = FastSeekSkipReason.Cancelled;
                    plan = null;
                }
                if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    pending.Remove(key);
                    if (plan is not null)
                    {
                        failed.Remove(key);
                    }
                    else if (failureReason.HasValue && ShouldCacheFailure(failureReason.Value) &&
                             !entry.UsedNetworkSizing)
                    {
                        StoreFailureUnsafe(key, failureReason.Value);
                    }
                }
            }
            if (failureReason.HasValue) LogSkipped(failureReason.Value);
            entry.Complete(plan);
            entry.Dispose();
        }
    }

    public bool TryBindInput(
        SourceIdentity source,
        string mediaSourceId,
        long targetTimeTicks,
        long durationTicks,
        int runtimeGeneration,
        string inputUrl,
        PluginConfiguration options,
        FastSeekVideoSelection? videoSelection = null,
        string? ticket = null)
    {
        if (source is null || string.IsNullOrWhiteSpace(mediaSourceId) ||
            string.IsNullOrWhiteSpace(inputUrl) || inputUrl.Length > 2048 || options is null)
            return false;
        var key = new PreparationKey(
            source,
            mediaSourceId,
            targetTimeTicks,
            durationTicks,
            runtimeGeneration,
            videoSelection);
        lock (sync)
        {
            RemoveExpiredUnsafe();
            if (!prepared.TryGetValue(key, out var plan)) return false;
            if (!boundInputs.ContainsKey(inputUrl) && boundInputs.Count >= MaximumPlans)
                EvictOldestBindingUnsafe();
            boundInputs[inputUrl] = new FastSeekBinding(plan, key.ResourceKey, ticket);
            return true;
        }
    }

    public bool TryGetBoundPlan(
        string inputUrl,
        int runtimeGeneration,
        long targetTimeTicks,
        CancellationToken cancellationToken,
        out FastSeekPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(inputUrl) || inputUrl.Length > 2048 ||
            targetTimeTicks < MinimumTarget.Ticks)
            return false;
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            RemoveExpiredUnsafe();
            if (!boundInputs.TryGetValue(inputUrl, out var binding) ||
                binding.Plan.RuntimeGeneration != runtimeGeneration ||
                binding.Plan.TargetTimeTicks != targetTimeTicks)
                return false;
            plan = binding.Plan;
            return true;
        }
    }

    internal bool TryActivateInput(string inputUrl, FastSeekPlan plan, TicketStore tickets,
        out TicketPayload? ticket)
    {
        ticket = null;
        FastSeekBinding binding;
        lock (sync)
        {
            RemoveExpiredUnsafe();
            if (!boundInputs.TryGetValue(inputUrl, out binding!) || !ReferenceEquals(binding.Plan, plan) ||
                binding.Ticket is null || plan.Representation is null) return false;
        }
        if (!tickets.TryInspect(binding.Ticket, out var payload) || payload is null ||
            payload.Purpose != PlaybackTicketPurpose.ServerFfmpeg ||
            payload.RuntimeGeneration != plan.RuntimeGeneration) return false;
        lock (sync)
        {
            if (!boundInputs.TryGetValue(inputUrl, out var current) || !ReferenceEquals(current, binding)) return false;
            activeRepresentations.Remove(payload);
            activeRepresentations.Add(payload, plan.Representation);
            ticket = payload;
            return true;
        }
    }

    internal bool TryGetInputRepresentation(TicketPayload ticket, int runtimeGeneration,
        out FastSeekRepresentation? representation)
    {
        representation = null;
        lock (sync)
            return ticket.Purpose == PlaybackTicketPurpose.ServerFfmpeg &&
                   ticket.RuntimeGeneration == runtimeGeneration && clock.UtcNow < ticket.ExpiresAtUtc &&
                   activeRepresentations.TryGetValue(ticket, out representation);
    }

    internal void DisableInput(string inputUrl, TicketPayload ticket)
    {
        lock (sync)
        {
            boundInputs.Remove(inputUrl);
            activeRepresentations.Remove(ticket);
        }
    }

    internal void DisableInput(string inputUrl, string ticket, TicketPayload payload)
    {
        lock (sync)
        {
            if (boundInputs.TryGetValue(inputUrl, out var binding) &&
                string.Equals(binding.Ticket, ticket, StringComparison.Ordinal))
                boundInputs.Remove(inputUrl);
            activeRepresentations.Remove(payload);
        }
    }

    internal void InvalidateSource(Uri source)
    {
        var resource = GatewayTransport.ResourceDigest(source);
        PendingFastSeek[] active;
        lock (sync)
        {
            active = pending.Where(pair => pair.Key.ResourceKey == resource).Select(pair => pair.Value).ToArray();
            foreach (var key in pending.Keys.Where(key => key.ResourceKey == resource).ToArray()) pending.Remove(key);
            foreach (var key in prepared.Keys.Where(key => key.ResourceKey == resource).ToArray()) prepared.Remove(key);
            foreach (var key in failed.Keys.Where(key => key.ResourceKey == resource).ToArray()) failed.Remove(key);
            foreach (var key in boundInputs.Where(pair => pair.Value.ResourceKey == resource).Select(pair => pair.Key).ToArray())
                boundInputs.Remove(key);
        }
        foreach (var entry in active) entry.Cancel();
    }

    public void Clear()
    {
        PendingFastSeek[] active;
        lock (sync)
        {
            active = pending.Values.Distinct().ToArray();
            prepared.Clear();
            failed.Clear();
            boundInputs.Clear();
            activeRepresentations = new ConditionalWeakTable<TicketPayload, FastSeekRepresentation>();
            pending.Clear();
        }
        foreach (var entry in active) entry.Cancel();
    }

    public int RemoveExpired()
    {
        lock (sync)
        {
            var before = prepared.Count + boundInputs.Count;
            RemoveExpiredUnsafe();
            return before - prepared.Count - boundInputs.Count;
        }
    }

    private async Task<FastSeekPlan?> PrepareCoreAsync(
        SourceIdentity source,
        long targetTimeTicks,
        long durationTicks,
        int runtimeGeneration,
        PluginConfiguration options,
        PendingFastSeek entry,
        FastSeekVideoSelection? videoSelection)
    {
        var cancellationToken = entry.CancellationToken;
        var budget = new ProbeBudget(entry);
        var firstProbe = await ReadProbeAsync(
                source,
                0,
                InitialProbeBytes,
                options,
                budget,
                cancellationToken)
            .ConfigureAwait(false);
        if (firstProbe.Representation is null || firstProbe.Representation.TotalLength != firstProbe.TotalLength)
            throw new FastSeekProbeException(FastSeekSkipReason.Representation);
        var analyzed = TransportStreamClockParser.TryAnalyze(
                firstProbe.Bytes,
                firstProbe.RangeStart,
                null,
                out var firstAnalysis);
        entry.ThrowIfExpired();
        if (!analyzed)
            throw new FastSeekProbeException(FastSeekSkipReason.TransportStructure);
        if (!firstAnalysis!.TrySelectVideo(videoSelection, out var randomAccessPid, out var clockPid,
                out var program))
            throw new FastSeekProbeException(FastSeekSkipReason.StreamSelection);
        if (clockPid < 0 || !firstAnalysis.TryGetFirstPcr(clockPid, out var firstPcr))
            throw new FastSeekProbeException(FastSeekSkipReason.PcrMissing);

        var durationSeconds = TimeSpan.FromTicks(durationTicks).TotalSeconds;
        var targetSeconds = TimeSpan.FromTicks(targetTimeTicks).TotalSeconds;
        var desiredSeconds = targetSeconds - PreRoll.TotalSeconds;
        var targetScanBytes = FastSeekBudgetPolicy.CalculateTargetScanBytes(
            firstProbe.TotalLength,
            durationSeconds,
            firstAnalysis.Format.PacketStride);
        budget.Configure(targetScanBytes);
        logger.Debug("STRM_BRIDGE_FAST_SEEK_BUDGET scan_bytes=" + targetScanBytes +
                     " preparation_bytes=" + budget.MaximumBytes);
        var estimatedOffset = AlignAndClamp(
            firstProbe.TotalLength * (desiredSeconds / durationSeconds),
            firstAnalysis.Format,
            firstProbe.TotalLength,
            targetScanBytes);
        var estimatedEvidence = await ReadTargetEvidenceAsync(
                source,
                estimatedOffset,
                targetScanBytes,
                firstProbe,
                firstAnalysis.Format,
                clockPid,
                randomAccessPid,
                program,
                options,
                budget,
                cancellationToken)
            .ConfigureAwait(false);

        var estimatedSeconds = TransportStreamClockParser.SecondsBetween(
            firstPcr.Clock27Mhz,
            estimatedEvidence.Pcr.Clock27Mhz);
        if (!IsFinitePositive(estimatedSeconds) || estimatedSeconds > durationSeconds * 1.25)
            throw new FastSeekProbeException(FastSeekSkipReason.Timeline);
        var byteDelta = estimatedEvidence.Pcr.PacketOffset - firstPcr.PacketOffset;
        var byteRate = byteDelta / estimatedSeconds;
        if (!IsFinitePositive(byteRate) || byteRate < MinimumByteRate || byteRate > MaximumByteRate)
            throw new FastSeekProbeException(FastSeekSkipReason.ByteRate);

        var estimatedIsValid = TryCalculateRelativeSeek(
                targetSeconds,
                firstPcr,
                estimatedEvidence,
                out var estimatedRelativeSeek);
        if (estimatedIsValid && estimatedRelativeSeek <= MaximumInitialRelativeSeekWithoutCorrection)
        {
            var estimatedPlanTime = clock.UtcNow;
            return new FastSeekPlan(
                targetTimeTicks,
                firstProbe.TotalLength,
                estimatedEvidence.Anchor.PacketOffset,
                firstAnalysis.Format.PacketStride,
                durationTicks,
                estimatedRelativeSeek,
                budget.ProbeCount,
                runtimeGeneration,
                estimatedPlanTime + PlanLifetime,
                videoSelection?.StreamIndex,
                firstProbe.Representation);
        }

        var previousOffset = firstPcr.PacketOffset;
        var previousSeconds = 0d;
        var evidence = estimatedEvidence;
        var seconds = estimatedSeconds;
        var lowerOffset = firstPcr.PacketOffset;
        var upperOffset = firstProbe.TotalLength - firstAnalysis.Format.PacketStride;
        var visited = new HashSet<long> { estimatedOffset };
        for (var correction = 1; correction <= MaximumCorrections; correction++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seconds < desiredSeconds) lowerOffset = Math.Max(lowerOffset, evidence.Pcr.PacketOffset);
            else upperOffset = Math.Min(upperOffset, evidence.Pcr.PacketOffset);
            var localRate = (evidence.Pcr.PacketOffset - previousOffset) / (seconds - previousSeconds);
            if (!IsFinitePositive(localRate) || localRate < MinimumByteRate || localRate > MaximumByteRate)
                throw new FastSeekProbeException(FastSeekSkipReason.Timeline);
            var candidate = evidence.Pcr.PacketOffset + (desiredSeconds - seconds) * localRate;
            if (candidate <= lowerOffset || candidate >= upperOffset)
                candidate = lowerOffset + (upperOffset - lowerOffset) / 2d;
            var scanBytes = Math.Min(targetScanBytes, budget.RemainingBytes);
            if (scanBytes < firstAnalysis.Format.PacketStride * 5) break;
            var offset = AlignAndClamp(candidate, firstAnalysis.Format, firstProbe.TotalLength, scanBytes);
            if (!visited.Add(offset)) break;
            previousOffset = evidence.Pcr.PacketOffset;
            previousSeconds = seconds;
            evidence = await ReadTargetEvidenceAsync(
                    source, offset, scanBytes, firstProbe, firstAnalysis.Format,
                    clockPid, randomAccessPid, program, options, budget, cancellationToken)
                .ConfigureAwait(false);
            seconds = TransportStreamClockParser.SecondsBetween(firstPcr.Clock27Mhz, evidence.Pcr.Clock27Mhz);
            if (!IsFinitePositive(seconds) || seconds > durationSeconds * 1.25)
                throw new FastSeekProbeException(FastSeekSkipReason.Timeline);
            if ((seconds - previousSeconds) * (evidence.Pcr.PacketOffset - previousOffset) <= 0)
                throw new FastSeekProbeException(FastSeekSkipReason.Timeline);
            logger.Debug("STRM_BRIDGE_FAST_SEEK_CORRECTION step=" + correction +
                         " probes=" + budget.ProbeCount + " remaining_bytes=" + budget.RemainingBytes +
                         " error_ms=" + Math.Round((targetSeconds - seconds) * 1000)
                             .ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (TryCalculateRelativeSeek(targetSeconds, firstPcr, evidence, out var relativeSeek))
                return new FastSeekPlan(
                    targetTimeTicks, firstProbe.TotalLength, evidence.Anchor.PacketOffset,
                    firstAnalysis.Format.PacketStride, durationTicks, relativeSeek, budget.ProbeCount,
                    runtimeGeneration, clock.UtcNow + PlanLifetime, videoSelection?.StreamIndex,
                    firstProbe.Representation);
        }
        if (estimatedIsValid)
            return new FastSeekPlan(
                targetTimeTicks, firstProbe.TotalLength, estimatedEvidence.Anchor.PacketOffset,
                firstAnalysis.Format.PacketStride, durationTicks, estimatedRelativeSeek, budget.ProbeCount,
                runtimeGeneration, clock.UtcNow + PlanLifetime, videoSelection?.StreamIndex,
                firstProbe.Representation);
        throw new FastSeekProbeException(FastSeekSkipReason.Correction);
    }

    private async Task<FastSeekProbeResult> ReadProbeAsync(
        SourceIdentity source,
        long offset,
        int requestedBytes,
        PluginConfiguration options,
        ProbeBudget budget,
        CancellationToken cancellationToken)
    {
        budget.ThrowIfExpired();
        var maximumBytes = budget.GetRequestSize(requestedBytes);
        var result = await probeClient.ReadAsync(
                source,
                offset,
                maximumBytes,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        budget.ThrowIfExpired();
        if (result.Bytes.Length > maximumBytes)
            throw new FastSeekProbeException(FastSeekSkipReason.TruncatedSample);
        budget.Record(result);
        return result;
    }

    private async Task<TargetProbeEvidence> ReadTargetEvidenceAsync(
        SourceIdentity source,
        long startOffset,
        int maximumScanBytes,
        FastSeekProbeResult firstProbe,
        TransportStreamFormat format,
        int clockPid,
        int randomAccessPid,
        TransportProgramMap? program,
        PluginConfiguration options,
        ProbeBudget budget,
        CancellationToken cancellationToken)
    {
        var offset = startOffset;
        var scanned = 0;
        var sawStructure = false;
        var sawPcr = false;
        while (scanned < maximumScanBytes && offset < firstProbe.TotalLength && budget.RemainingBytes > 0)
        {
            budget.ThrowIfExpired();
            var remainingScan = maximumScanBytes - scanned;
            var remainingSource = firstProbe.TotalLength - offset;
            var requested = (int)Math.Min(
                Math.Min(MaximumProbeBytes, remainingScan),
                Math.Min(remainingSource, budget.RemainingBytes));
            var candidateBytes = AlignByteCountDown(requested, format.PacketStride);
            requested = budget.GetTargetRequestSize(requested, format.PacketStride);
            if (requested < format.PacketStride * 5) break;
            if (requested < candidateBytes)
                logger.Debug("STRM_BRIDGE_FAST_SEEK_RANGE candidate_bytes=" + candidateBytes +
                             " requested_bytes=" + requested + " remaining_bytes=" + budget.RemainingBytes);

            var probe = await ReadProbeAsync(
                    source,
                    offset,
                    requested,
                    options,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateTotalLength(firstProbe, probe);
            var analyzed = TransportStreamClockParser.TryAnalyze(
                    probe.Bytes,
                    probe.RangeStart,
                    format,
                    out var analysis);
            budget.ThrowIfExpired();
            if (analyzed)
            {
                sawStructure = true;
                var hasPcr = analysis!.TryGetFirstPcr(clockPid, out var pcr);
                sawPcr |= hasPcr;
                if (!analysis.MatchesProgram(program))
                    throw new FastSeekProbeException(FastSeekSkipReason.StreamSelection);
                if (hasPcr &&
                    TryGetTimedRandomAccess(analysis, randomAccessPid, clockPid, pcr, out var evidence))
                    return evidence;
            }

            if (probe.Bytes.Length <= 0) break;
            scanned += probe.Bytes.Length;
            offset = AlignUp(offset + probe.Bytes.Length, format);
        }

        budget.ThrowIfExpired();
        if (!sawStructure)
            throw new FastSeekProbeException(FastSeekSkipReason.TransportStructure);
        if (!sawPcr)
            throw new FastSeekProbeException(FastSeekSkipReason.PcrMissing);
        throw new FastSeekProbeException(FastSeekSkipReason.RandomAccessMissing);
    }

    private static bool TryGetTimedRandomAccess(
        TransportStreamAnalysis analysis,
        int pid,
        int clockPid,
        PcrSample firstPcr,
        out TargetProbeEvidence evidence)
    {
        var clocks = analysis.PcrSamples.Where(clock => clock.Pid == clockPid).ToArray();
        var clockIndex = 0;
        foreach (var candidate in analysis.RandomAccessSamples)
        {
            if (candidate.Pid != pid) continue;
            while (clockIndex < clocks.Length && clocks[clockIndex].PacketOffset < candidate.PacketOffset)
                clockIndex++;
            if (clockIndex >= clocks.Length) break;
            var after = clocks[clockIndex];
            var beforeIndex = after.PacketOffset == candidate.PacketOffset ? clockIndex : clockIndex - 1;
            if (beforeIndex >= 0)
            {
                var before = clocks[beforeIndex];
                if (TransportStreamClockParser.SecondsBetween(before.Clock27Mhz, after.Clock27Mhz) <= 0.1)
                {
                    evidence = new TargetProbeEvidence(firstPcr, candidate, before, after);
                    return true;
                }
            }
        }
        evidence = default;
        return false;
    }

    private static int AlignByteCountDown(int value, int packetStride) =>
        value < packetStride ? 0 : value / packetStride * packetStride;

    private static long AlignUp(long absoluteOffset, TransportStreamFormat format)
    {
        if (absoluteOffset <= format.PacketOrigin) return format.PacketOrigin;
        var relative = absoluteOffset - format.PacketOrigin;
        return format.PacketOrigin +
               (relative + format.PacketStride - 1) / format.PacketStride * format.PacketStride;
    }

    private static long AlignAndClamp(
        double candidate,
        TransportStreamFormat format,
        long totalLength,
        int maximumProbeBytes)
    {
        if (double.IsNaN(candidate) || double.IsInfinity(candidate) || candidate < format.PacketOrigin ||
            candidate > totalLength - format.PacketStride)
            throw new FastSeekProbeException(FastSeekSkipReason.Correction);
        var maximumStart = Math.Max(format.PacketOrigin, totalLength - maximumProbeBytes);
        var bounded = Math.Min(candidate, maximumStart);
        return TransportStreamClockParser.AlignDown((long)Math.Floor(bounded), format);
    }

    private static bool TryCalculateRelativeSeek(
        double targetSeconds,
        PcrSample origin,
        TargetProbeEvidence evidence,
        out TimeSpan relativeSeek)
    {
        var lower = TransportStreamClockParser.SecondsBetween(origin.Clock27Mhz, evidence.Before.Clock27Mhz);
        var upper = TransportStreamClockParser.SecondsBetween(origin.Clock27Mhz, evidence.After.Clock27Mhz);
        relativeSeek = TimeSpan.FromSeconds(targetSeconds - (lower + upper) / 2);
        return upper >= lower && upper - lower <= 0.1 &&
               targetSeconds - upper >= MinimumRelativeSeek.TotalSeconds &&
               targetSeconds - lower <= MaximumRelativeSeek.TotalSeconds;
    }

    private static void ValidateTotalLength(FastSeekProbeResult first, FastSeekProbeResult later)
    {
        if (first.TotalLength != later.TotalLength)
            throw new FastSeekProbeException(FastSeekSkipReason.RangeResponse);
        if (first.Representation is null || !first.Representation.Matches(later.Representation))
            throw new FastSeekProbeException(FastSeekSkipReason.Representation);
    }

    private static bool IsFinitePositive(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;

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

    private void StorePreparedUnsafe(PreparationKey key, FastSeekPlan plan)
    {
        if (!prepared.ContainsKey(key) && prepared.Count >= MaximumPlans) EvictOldestUnsafe(prepared);
        prepared[key] = plan;
    }

    private void StoreFailureUnsafe(PreparationKey key, FastSeekSkipReason reason)
    {
        if (!failed.ContainsKey(key) && failed.Count >= MaximumPlans)
        {
            var oldest = failed.OrderBy(pair => pair.Value.ExpiresAtUtc).First().Key;
            failed.Remove(oldest);
        }
        failed[key] = new FastSeekFailure(reason, clock.UtcNow + FailureLifetime);
    }

    private static bool ShouldCacheFailure(FastSeekSkipReason reason) =>
        reason is FastSeekSkipReason.TransportStructure or
            FastSeekSkipReason.StreamSelection or
            FastSeekSkipReason.PcrMissing or
            FastSeekSkipReason.RandomAccessMissing or
            FastSeekSkipReason.Timeline or
            FastSeekSkipReason.ByteRate or
            FastSeekSkipReason.Correction;

    private void RemoveExpiredUnsafe()
    {
        var now = clock.UtcNow;
        foreach (var key in prepared.Where(pair => now >= pair.Value.ExpiresAtUtc).Select(pair => pair.Key).ToArray())
            prepared.Remove(key);
        foreach (var key in failed.Where(pair => now >= pair.Value.ExpiresAtUtc).Select(pair => pair.Key).ToArray())
            failed.Remove(key);
        foreach (var key in boundInputs
                     .Where(pair => now >= pair.Value.Plan.ExpiresAtUtc)
                     .Select(pair => pair.Key)
                     .ToArray())
            boundInputs.Remove(key);
    }

    private static void EvictOldestUnsafe<TKey>(Dictionary<TKey, FastSeekPlan> values) where TKey : notnull
    {
        if (values.Count == 0) return;
        var oldest = values.OrderBy(pair => pair.Value.ExpiresAtUtc).First().Key;
        values.Remove(oldest);
    }

    private void EvictOldestBindingUnsafe()
    {
        if (boundInputs.Count == 0) return;
        var oldest = boundInputs
            .OrderBy(pair => pair.Value.Plan.ExpiresAtUtc)
            .First()
            .Key;
        boundInputs.Remove(oldest);
    }

    private void LogSkipped(FastSeekSkipReason reason, bool cached = false) =>
        logger.Debug("STRM_BRIDGE_FAST_SEEK_SKIPPED reason=" + reason.ToString().ToLowerInvariant() +
                     (cached ? " cached=true" : string.Empty));

    private readonly struct TargetProbeEvidence
    {
        public TargetProbeEvidence(PcrSample pcr, RandomAccessSample anchor, PcrSample before, PcrSample after)
        {
            Pcr = pcr;
            Anchor = anchor;
            Before = before;
            After = after;
        }

        public PcrSample Pcr { get; }

        public RandomAccessSample Anchor { get; }

        public PcrSample Before { get; }

        public PcrSample After { get; }
    }

    private sealed class ProbeBudget
    {
        private readonly PendingFastSeek entry;
        private long measuredBodyBytes;
        private double measuredBodySeconds;
        private TimeSpan? latestSetupDuration;

        private int consumedBytes;

        public ProbeBudget(PendingFastSeek entry) => this.entry = entry;

        public int MaximumBytes { get; private set; } = InitialProbeBytes;

        public int RemainingBytes => MaximumBytes - consumedBytes;

        public int ProbeCount { get; private set; }

        public void Configure(int targetScanBytes) =>
            MaximumBytes = FastSeekBudgetPolicy.CalculatePreparationBytes(targetScanBytes);

        public void ThrowIfExpired() => entry.ThrowIfExpired();

        public int GetTargetRequestSize(int requestedBytes, int packetStride)
        {
            var candidate = GetRequestSize(requestedBytes);
            var dynamicBytes = FastSeekBudgetPolicy.CalculateRequestBytes(
                candidate, packetStride, entry.RemainingTime, latestSetupDuration,
                measuredBodySeconds > 0 ? measuredBodyBytes / measuredBodySeconds : (double?)null);
            if (dynamicBytes > 0 && dynamicBytes < AlignByteCountDown(candidate, packetStride))
                entry.UsedNetworkSizing = true;
            return dynamicBytes;
        }

        public int GetRequestSize(int requestedBytes)
        {
            var bounded = Math.Min(requestedBytes, RemainingBytes);
            if (bounded < 1) throw new FastSeekProbeException(FastSeekSkipReason.Correction);
            return bounded;
        }

        public void Record(FastSeekProbeResult probe)
        {
            var actualBytes = probe.Bytes.Length;
            if (actualBytes < 0 || actualBytes > RemainingBytes)
                throw new FastSeekProbeException(FastSeekSkipReason.TruncatedSample);
            consumedBytes += actualBytes;
            ProbeCount++;
            if (actualBytes > 0 && probe.SetupDuration is TimeSpan setup && setup >= TimeSpan.Zero &&
                probe.BodyDuration is TimeSpan body && body > TimeSpan.Zero)
            {
                // This is an application-level read estimate, not a bandwidth guarantee. Socket
                // prefetch can overestimate it, but it can only restore the existing Range ceiling.
                latestSetupDuration = setup;
                measuredBodyBytes += actualBytes;
                measuredBodySeconds += body.TotalSeconds;
            }
        }
    }

    private readonly struct FastSeekFailure
    {
        public FastSeekFailure(FastSeekSkipReason reason, DateTimeOffset expiresAtUtc)
        {
            Reason = reason;
            ExpiresAtUtc = expiresAtUtc;
        }

        public FastSeekSkipReason Reason { get; }

        public DateTimeOffset ExpiresAtUtc { get; }
    }

    private readonly struct PreparationKey : IEquatable<PreparationKey>
    {
        public PreparationKey(
            SourceIdentity source,
            string mediaSourceId,
            long targetTimeTicks,
            long durationTicks,
            int runtimeGeneration,
            FastSeekVideoSelection? videoSelection)
        {
            SourceFingerprint = source.SourceFingerprint;
            ResourceKey = GatewayTransport.ResourceDigest(source.SourceUri);
            MediaSourceId = mediaSourceId;
            TargetTimeTicks = targetTimeTicks;
            DurationTicks = durationTicks;
            RuntimeGeneration = runtimeGeneration;
            VideoSelectionKey = videoSelection?.CacheKey ?? string.Empty;
        }

        private string SourceFingerprint { get; }

        public string ResourceKey { get; }

        private string MediaSourceId { get; }

        private long TargetTimeTicks { get; }

        private long DurationTicks { get; }

        private int RuntimeGeneration { get; }

        private string VideoSelectionKey { get; }

        public bool Equals(PreparationKey other) =>
            TargetTimeTicks == other.TargetTimeTicks &&
            DurationTicks == other.DurationTicks &&
            RuntimeGeneration == other.RuntimeGeneration &&
            string.Equals(VideoSelectionKey, other.VideoSelectionKey, StringComparison.Ordinal) &&
            string.Equals(SourceFingerprint, other.SourceFingerprint, StringComparison.Ordinal) &&
            string.Equals(MediaSourceId, other.MediaSourceId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is PreparationKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(SourceFingerprint);
                hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(MediaSourceId);
                hash = hash * 397 ^ TargetTimeTicks.GetHashCode();
                hash = hash * 397 ^ DurationTicks.GetHashCode();
                hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(VideoSelectionKey);
                return hash * 397 ^ RuntimeGeneration;
            }
        }
    }

    private sealed class PendingFastSeek : IDisposable
    {
        private readonly CancellationTokenSource timeout;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private readonly TimeSpan lifetime;
        private readonly TaskCompletionSource<FastSeekPlan?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingFastSeek(TimeSpan lifetime)
        {
            this.lifetime = lifetime;
            timeout = new CancellationTokenSource();
            CancellationToken = timeout.Token;
            timeout.CancelAfter(lifetime);
        }

        public CancellationToken CancellationToken { get; }

        public TimeSpan RemainingTime => lifetime - stopwatch.Elapsed;

        public bool IsExpired => CancellationToken.IsCancellationRequested || RemainingTime <= TimeSpan.Zero;

        public bool UsedNetworkSizing { get; set; }

        public void ThrowIfExpired()
        {
            if (IsExpired) throw new OperationCanceledException(CancellationToken);
        }

        public Task<FastSeekPlan?> Task => completion.Task;

        public int WaiterCount { get; private set; }

        public void AddWaiter() => WaiterCount++;

        public bool RemoveWaiter()
        {
            if (WaiterCount <= 0) return false;
            WaiterCount--;
            return WaiterCount == 0;
        }

        public void Complete(FastSeekPlan? plan) => completion.TrySetResult(plan);

        public void Cancel()
        {
            try { timeout.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => timeout.Dispose();
    }

    private sealed class FastSeekBinding
    {
        public FastSeekBinding(FastSeekPlan plan, string resourceKey, string? ticket)
        {
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            ResourceKey = resourceKey;
            Ticket = ticket;
        }

        public FastSeekPlan Plan { get; }

        public string ResourceKey { get; }

        public string? Ticket { get; }
    }
}
