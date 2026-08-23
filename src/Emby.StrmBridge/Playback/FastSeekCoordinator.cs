using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Playback;

internal sealed class FastSeekPlan
{
    public FastSeekPlan(
        string sourceFingerprint,
        string mediaSourceId,
        long targetTimeTicks,
        long totalLength,
        long byteOffset,
        int packetStride,
        long packetOrigin,
        int pcrPid,
        long timelineOriginPacketOffset,
        long timelineOriginClock27Mhz,
        double byteRate,
        long durationTicks,
        TimeSpan relativeSeek,
        int probeCount,
        int runtimeGeneration,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        SourceFingerprint = sourceFingerprint;
        MediaSourceId = mediaSourceId;
        TargetTimeTicks = targetTimeTicks;
        TotalLength = totalLength;
        ByteOffset = byteOffset;
        PacketStride = packetStride;
        PacketOrigin = packetOrigin;
        PcrPid = pcrPid;
        TimelineOriginPacketOffset = timelineOriginPacketOffset;
        TimelineOriginClock27Mhz = timelineOriginClock27Mhz;
        ByteRate = byteRate;
        DurationTicks = durationTicks;
        RelativeSeek = relativeSeek;
        ProbeCount = probeCount;
        RuntimeGeneration = runtimeGeneration;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string SourceFingerprint { get; }

    public string MediaSourceId { get; }

    public long TargetTimeTicks { get; }

    public long TotalLength { get; }

    public long ByteOffset { get; }

    public int PacketStride { get; }

    public long PacketOrigin { get; }

    public int PcrPid { get; }

    public long TimelineOriginPacketOffset { get; }

    public long TimelineOriginClock27Mhz { get; }

    public double ByteRate { get; }

    public long DurationTicks { get; }

    public TimeSpan RelativeSeek { get; }

    public int ProbeCount { get; }

    public int RuntimeGeneration { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}

internal sealed class FastSeekProbeResult
{
    public FastSeekProbeResult(long rangeStart, long totalLength, byte[] bytes)
    {
        RangeStart = rangeStart;
        TotalLength = totalLength;
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
    }

    public long RangeStart { get; }

    public long TotalLength { get; }

    public byte[] Bytes { get; }
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
        using var lease = await transport.OpenProbeAsync(
                source.SourceUri,
                scope,
                scope,
                "GET",
                null,
                headers,
                options,
                cancellationToken)
            .ConfigureAwait(false);
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
        var prefix = await lease.PeekPrefixAsync((int)expectedLength, cancellationToken).ConfigureAwait(false);
        if (prefix.Length != expectedLength)
            throw new FastSeekProbeException(FastSeekSkipReason.TruncatedSample);
        return new FastSeekProbeResult(offset, contentRange.Length.Value, prefix.ToArray());
    }
}

internal enum FastSeekSkipReason
{
    RangeResponse,
    TruncatedSample,
    TransportStructure,
    PcrMissing,
    Timeline,
    ByteRate,
    Correction,
    Capacity,
    Cancelled,
    Unavailable,
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
    public static readonly TimeSpan PreRoll = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MinimumTarget = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinimumRelativeSeek = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumRelativeSeek = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan MaximumInitialRelativeSeekWithoutCorrection = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(5);
    public const int MaximumProbeBytes = 512 * 1024;
    public const int MaximumPlans = 512;
    public const int MaximumPendingPreparations = 64;
    private const double MinimumByteRate = 16 * 1024;
    private const double MaximumByteRate = 250 * 1024 * 1024;
    private readonly object sync = new();
    private readonly Dictionary<PreparationKey, FastSeekPlan> prepared = new();
    private readonly Dictionary<string, FastSeekBinding> boundInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<PreparationKey, PendingFastSeek> pending = new();
    private readonly Dictionary<PreparationKey, PendingFastSeek> pendingBoundPlans = new();
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
        CancellationToken cancellationToken)
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
            source.SourceFingerprint,
            mediaSourceId,
            targetTimeTicks,
            durationTicks,
            runtimeGeneration);
        PendingFastSeek entry;
        var shouldStart = false;
        lock (sync)
        {
            RemoveExpiredUnsafe();
            if (prepared.ContainsKey(key)) return true;
            if (!pending.TryGetValue(key, out entry!))
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
        }

        if (shouldStart)
            _ = CompleteInitialPreparationAsync(
                key,
                entry,
                source,
                mediaSourceId,
                targetTimeTicks,
                durationTicks,
                runtimeGeneration,
                options);

        FastSeekPlan? plan;
        try
        {
            plan = await AwaitWithCancellation(entry.Task, cancellationToken).ConfigureAwait(false);
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

    private async Task CompleteInitialPreparationAsync(
        PreparationKey key,
        PendingFastSeek entry,
        SourceIdentity source,
        string mediaSourceId,
        long targetTimeTicks,
        long durationTicks,
        int runtimeGeneration,
        PluginConfiguration options)
    {
        FastSeekPlan? plan = null;
        try
        {
            plan = await PrepareCoreAsync(
                    source,
                    mediaSourceId,
                    targetTimeTicks,
                    durationTicks,
                    runtimeGeneration,
                    options,
                    entry.CancellationToken)
                .ConfigureAwait(false);
            if (plan is not null && (runtimeGenerationProvider is null ||
                                    runtimeGenerationProvider() == plan.RuntimeGeneration))
            {
                var stored = false;
                lock (sync)
                {
                    RemoveExpiredUnsafe();
                    if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    {
                        StorePreparedUnsafe(key, plan);
                        stored = true;
                    }
                }
                if (stored)
                    logger.Debug("STRM_BRIDGE_FAST_SEEK_READY packets=" + plan.PacketStride +
                                 " probes=" + plan.ProbeCount);
                else
                    plan = null;
            }
            else
            {
                plan = null;
            }
        }
        catch (Exception)
        {
            LogSkipped(FastSeekSkipReason.Unavailable);
            plan = null;
        }
        finally
        {
            lock (sync)
            {
                if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    pending.Remove(key);
            }
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
        PluginConfiguration options)
    {
        if (source is null || string.IsNullOrWhiteSpace(mediaSourceId) ||
            string.IsNullOrWhiteSpace(inputUrl) || inputUrl.Length > 2048 || options is null)
            return false;
        var key = new PreparationKey(
            source.SourceFingerprint,
            mediaSourceId,
            targetTimeTicks,
            durationTicks,
            runtimeGeneration);
        lock (sync)
        {
            RemoveExpiredUnsafe();
            if (!prepared.TryGetValue(key, out var plan)) return false;
            if (!boundInputs.ContainsKey(inputUrl) && boundInputs.Count >= MaximumPlans)
                EvictOldestBindingUnsafe();
            boundInputs[inputUrl] = new FastSeekBinding(source, options, plan);
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
        try
        {
            plan = GetBoundPlanAsync(
                    inputUrl,
                    runtimeGeneration,
                    targetTimeTicks,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            return plan is not null;
        }
        catch (OperationCanceledException)
        {
            LogSkipped(FastSeekSkipReason.Cancelled);
            return false;
        }
        catch (Exception)
        {
            LogSkipped(FastSeekSkipReason.Unavailable);
            return false;
        }
    }

    private async Task<FastSeekPlan?> GetBoundPlanAsync(
        string inputUrl,
        int runtimeGeneration,
        long targetTimeTicks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FastSeekBinding binding;
        PreparationKey key;
        PendingFastSeek entry;
        var shouldStart = false;
        lock (sync)
        {
            RemoveExpiredUnsafe();
            if (!boundInputs.TryGetValue(inputUrl, out binding!) ||
                binding.Calibration.RuntimeGeneration != runtimeGeneration)
                return null;
            if (binding.Plans.TryGetValue(targetTimeTicks, out var cached))
            {
                return cached;
            }
            key = new PreparationKey(
                binding.Calibration.SourceFingerprint,
                binding.Calibration.MediaSourceId,
                targetTimeTicks,
                binding.Calibration.DurationTicks,
                runtimeGeneration);
            if (prepared.TryGetValue(key, out var shared))
            {
                if (IsCompatible(binding.Calibration, shared))
                {
                    binding.Store(shared);
                    return shared;
                }
                prepared.Remove(key);
            }
            if (!pendingBoundPlans.TryGetValue(key, out entry!))
            {
                if (pendingBoundPlans.Count >= MaximumPendingPreparations)
                {
                    LogSkipped(FastSeekSkipReason.Capacity);
                    return null;
                }
                entry = new PendingFastSeek(PreparationTimeout);
                pendingBoundPlans.Add(key, entry);
                shouldStart = true;
            }
        }

        if (shouldStart)
            _ = CompleteBoundPreparationAsync(key, entry, binding, targetTimeTicks);

        var plan = await AwaitWithCancellation(entry.Task, cancellationToken).ConfigureAwait(false);
        if (plan is null || runtimeGenerationProvider is not null &&
            runtimeGenerationProvider() != plan.RuntimeGeneration)
            return null;
        lock (sync)
        {
            RemoveExpiredUnsafe();
            if (!boundInputs.TryGetValue(inputUrl, out var current) ||
                !ReferenceEquals(current, binding) || !IsCompatible(current.Calibration, plan))
                return null;
            current.Store(plan);
        }
        return plan;
    }

    private async Task CompleteBoundPreparationAsync(
        PreparationKey key,
        PendingFastSeek entry,
        FastSeekBinding binding,
        long targetTimeTicks)
    {
        FastSeekPlan? plan = null;
        try
        {
            plan = await PrepareBoundTargetCoreAsync(
                    binding,
                    targetTimeTicks,
                    entry.CancellationToken)
                .ConfigureAwait(false);
            if (plan is not null && (runtimeGenerationProvider is null ||
                                    runtimeGenerationProvider() == plan.RuntimeGeneration))
            {
                var stored = false;
                lock (sync)
                {
                    RemoveExpiredUnsafe();
                    if (pendingBoundPlans.TryGetValue(key, out var current) &&
                        ReferenceEquals(current, entry))
                    {
                        StorePreparedUnsafe(key, plan);
                        stored = true;
                    }
                }
                if (stored)
                    logger.Debug("STRM_BRIDGE_FAST_SEEK_READY packets=" + plan.PacketStride +
                                 " probes=" + plan.ProbeCount);
                else
                    plan = null;
            }
            else
            {
                plan = null;
            }
        }
        catch (Exception)
        {
            LogSkipped(FastSeekSkipReason.Unavailable);
            plan = null;
        }
        finally
        {
            lock (sync)
            {
                if (pendingBoundPlans.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    pendingBoundPlans.Remove(key);
            }
            entry.Complete(plan);
            entry.Dispose();
        }
    }

    private static bool IsCompatible(FastSeekPlan calibration, FastSeekPlan candidate) =>
        string.Equals(calibration.SourceFingerprint, candidate.SourceFingerprint, StringComparison.Ordinal) &&
        string.Equals(calibration.MediaSourceId, candidate.MediaSourceId, StringComparison.Ordinal) &&
        calibration.TotalLength == candidate.TotalLength &&
        calibration.PacketStride == candidate.PacketStride &&
        calibration.PacketOrigin == candidate.PacketOrigin &&
        calibration.PcrPid == candidate.PcrPid &&
        calibration.TimelineOriginPacketOffset == candidate.TimelineOriginPacketOffset &&
        calibration.TimelineOriginClock27Mhz == candidate.TimelineOriginClock27Mhz &&
        calibration.DurationTicks == candidate.DurationTicks &&
        calibration.RuntimeGeneration == candidate.RuntimeGeneration;

    public void Clear()
    {
        PendingFastSeek[] active;
        lock (sync)
        {
            active = pending.Values.Concat(pendingBoundPlans.Values).Distinct().ToArray();
            prepared.Clear();
            boundInputs.Clear();
            pending.Clear();
            pendingBoundPlans.Clear();
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
        string mediaSourceId,
        long targetTimeTicks,
        long durationTicks,
        int runtimeGeneration,
        PluginConfiguration options,
        CancellationToken cancellationToken)
    {
        try
        {
            var firstProbe = await probeClient.ReadAsync(
                    source, 0, MaximumProbeBytes, options, cancellationToken)
                .ConfigureAwait(false);
            if (!TransportStreamClockParser.TryAnalyze(
                    firstProbe.Bytes,
                    firstProbe.RangeStart,
                    null,
                    out var firstAnalysis))
                throw new FastSeekProbeException(FastSeekSkipReason.TransportStructure);
            var clockPid = firstAnalysis!.SelectClockPid();
            if (clockPid < 0 || !firstAnalysis.TryGetFirstPcr(clockPid, out var firstPcr))
                throw new FastSeekProbeException(FastSeekSkipReason.PcrMissing);

            var durationSeconds = TimeSpan.FromTicks(durationTicks).TotalSeconds;
            var targetSeconds = TimeSpan.FromTicks(targetTimeTicks).TotalSeconds;
            var desiredSeconds = targetSeconds - PreRoll.TotalSeconds;
            var estimatedOffset = AlignAndClamp(
                firstProbe.TotalLength * (desiredSeconds / durationSeconds),
                firstAnalysis.Format,
                firstProbe.TotalLength);
            var estimatedProbe = await probeClient.ReadAsync(
                    source, estimatedOffset, ProbeLength(firstProbe.TotalLength, estimatedOffset), options,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateTotalLength(firstProbe, estimatedProbe);
            if (!TransportStreamClockParser.TryAnalyze(
                    estimatedProbe.Bytes,
                    estimatedProbe.RangeStart,
                    firstAnalysis.Format,
                    out var estimatedAnalysis) ||
                !estimatedAnalysis!.TryGetFirstPcr(clockPid, out var estimatedPcr))
                throw new FastSeekProbeException(FastSeekSkipReason.PcrMissing);

            var estimatedSeconds = TransportStreamClockParser.SecondsBetween(
                firstPcr.Clock27Mhz,
                estimatedPcr.Clock27Mhz);
            if (!IsFinitePositive(estimatedSeconds) || estimatedSeconds > durationSeconds * 1.25)
                throw new FastSeekProbeException(FastSeekSkipReason.Timeline);
            var byteDelta = estimatedPcr.PacketOffset - firstPcr.PacketOffset;
            var byteRate = byteDelta / estimatedSeconds;
            if (!IsFinitePositive(byteRate) || byteRate < MinimumByteRate || byteRate > MaximumByteRate)
                throw new FastSeekProbeException(FastSeekSkipReason.ByteRate);

            if (TryCalculateRelativeSeek(
                    targetSeconds,
                    estimatedProbe,
                    estimatedPcr,
                    estimatedSeconds,
                    byteRate,
                    out var estimatedRelativeSeek) &&
                estimatedRelativeSeek <= MaximumInitialRelativeSeekWithoutCorrection)
            {
                var estimatedPlanTime = clock.UtcNow;
                return new FastSeekPlan(
                    source.SourceFingerprint,
                    mediaSourceId,
                    targetTimeTicks,
                    firstProbe.TotalLength,
                    estimatedProbe.RangeStart,
                    firstAnalysis.Format.PacketStride,
                    firstAnalysis.Format.PacketOrigin,
                    clockPid,
                    firstPcr.PacketOffset,
                    firstPcr.Clock27Mhz,
                    byteRate,
                    durationTicks,
                    estimatedRelativeSeek,
                    2,
                    runtimeGeneration,
                    estimatedPlanTime,
                    estimatedPlanTime + PlanLifetime);
            }

            var correctedValue = estimatedPcr.PacketOffset + (desiredSeconds - estimatedSeconds) * byteRate;
            var correctedOffset = AlignAndClamp(
                correctedValue,
                firstAnalysis.Format,
                firstProbe.TotalLength);
            var correctedProbe = await probeClient.ReadAsync(
                    source, correctedOffset, ProbeLength(firstProbe.TotalLength, correctedOffset), options,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateTotalLength(firstProbe, correctedProbe);
            if (!TransportStreamClockParser.TryAnalyze(
                    correctedProbe.Bytes,
                    correctedProbe.RangeStart,
                    firstAnalysis.Format,
                    out var correctedAnalysis) ||
                !correctedAnalysis!.TryGetFirstPcr(clockPid, out var correctedPcr))
                throw new FastSeekProbeException(FastSeekSkipReason.PcrMissing);
            var correctedSeconds = TransportStreamClockParser.SecondsBetween(
                firstPcr.Clock27Mhz,
                correctedPcr.Clock27Mhz);
            if (!TryCalculateRelativeSeek(
                    targetSeconds,
                    correctedProbe,
                    correctedPcr,
                    correctedSeconds,
                    byteRate,
                    out var relativeSeek))
                throw new FastSeekProbeException(FastSeekSkipReason.Correction);

            var now = clock.UtcNow;
            return new FastSeekPlan(
                source.SourceFingerprint,
                mediaSourceId,
                targetTimeTicks,
                firstProbe.TotalLength,
                correctedProbe.RangeStart,
                firstAnalysis.Format.PacketStride,
                firstAnalysis.Format.PacketOrigin,
                clockPid,
                firstPcr.PacketOffset,
                firstPcr.Clock27Mhz,
                byteRate,
                durationTicks,
                relativeSeek,
                3,
                runtimeGeneration,
                now,
                now + PlanLifetime);
        }
        catch (FastSeekProbeException exception)
        {
            LogSkipped(exception.Reason);
        }
        catch (OperationCanceledException)
        {
            LogSkipped(FastSeekSkipReason.Cancelled);
        }
        catch (Exception)
        {
            LogSkipped(FastSeekSkipReason.Unavailable);
        }
        return null;
    }

    private static int ProbeLength(long totalLength, long offset) =>
        (int)Math.Min(MaximumProbeBytes, totalLength - offset);

    private async Task<FastSeekPlan?> PrepareBoundTargetCoreAsync(
        FastSeekBinding binding,
        long targetTimeTicks,
        CancellationToken cancellationToken)
    {
        var calibration = binding.Calibration;
        try
        {
            if (targetTimeTicks >= calibration.DurationTicks ||
                !IsFinitePositive(calibration.ByteRate) ||
                calibration.PacketStride is not 188 and not 192 and not 204 ||
                calibration.PacketOrigin < 0)
                throw new FastSeekProbeException(FastSeekSkipReason.Correction);
            var targetSeconds = TimeSpan.FromTicks(targetTimeTicks).TotalSeconds;
            var desiredSeconds = targetSeconds - PreRoll.TotalSeconds;
            var format = new TransportStreamFormat(
                calibration.PacketStride,
                calibration.PacketStride == 192 ? 4 : 0,
                calibration.PacketOrigin);
            var offset = AlignAndClamp(
                calibration.TimelineOriginPacketOffset + desiredSeconds * calibration.ByteRate,
                format,
                calibration.TotalLength);
            var probe = await probeClient.ReadAsync(
                    binding.Source,
                    offset,
                    ProbeLength(calibration.TotalLength, offset),
                    binding.Options,
                    cancellationToken)
                .ConfigureAwait(false);
            if (probe.TotalLength != calibration.TotalLength)
                throw new FastSeekProbeException(FastSeekSkipReason.RangeResponse);
            if (!TransportStreamClockParser.TryAnalyze(
                    probe.Bytes,
                    probe.RangeStart,
                    format,
                    out var analysis) ||
                !analysis!.TryGetFirstPcr(calibration.PcrPid, out var pcr))
                throw new FastSeekProbeException(FastSeekSkipReason.PcrMissing);
            var pcrSeconds = TransportStreamClockParser.SecondsBetween(
                calibration.TimelineOriginClock27Mhz,
                pcr.Clock27Mhz);
            if (!IsFinitePositive(pcrSeconds) ||
                pcrSeconds > TimeSpan.FromTicks(calibration.DurationTicks).TotalSeconds * 1.25)
                throw new FastSeekProbeException(FastSeekSkipReason.Timeline);
            if (TryCalculateRelativeSeek(
                    targetSeconds,
                    probe,
                    pcr,
                    pcrSeconds,
                    calibration.ByteRate,
                    out var relativeSeek))
                return CreateBoundTargetPlan(
                    calibration,
                    targetTimeTicks,
                    probe.RangeStart,
                    relativeSeek,
                    probeCount: 1);

            var correctedValue = pcr.PacketOffset +
                                 (desiredSeconds - pcrSeconds) * calibration.ByteRate;
            var correctedOffset = AlignAndClamp(
                correctedValue,
                format,
                calibration.TotalLength);
            if (correctedOffset == probe.RangeStart)
                throw new FastSeekProbeException(FastSeekSkipReason.Correction);
            var correctedProbe = await probeClient.ReadAsync(
                    binding.Source,
                    correctedOffset,
                    ProbeLength(calibration.TotalLength, correctedOffset),
                    binding.Options,
                    cancellationToken)
                .ConfigureAwait(false);
            if (correctedProbe.TotalLength != calibration.TotalLength)
                throw new FastSeekProbeException(FastSeekSkipReason.RangeResponse);
            if (!TransportStreamClockParser.TryAnalyze(
                    correctedProbe.Bytes,
                    correctedProbe.RangeStart,
                    format,
                    out var correctedAnalysis) ||
                !correctedAnalysis!.TryGetFirstPcr(calibration.PcrPid, out var correctedPcr))
                throw new FastSeekProbeException(FastSeekSkipReason.PcrMissing);
            var correctedSeconds = TransportStreamClockParser.SecondsBetween(
                calibration.TimelineOriginClock27Mhz,
                correctedPcr.Clock27Mhz);
            if (!IsFinitePositive(correctedSeconds) ||
                correctedSeconds > TimeSpan.FromTicks(calibration.DurationTicks).TotalSeconds * 1.25)
                throw new FastSeekProbeException(FastSeekSkipReason.Timeline);
            if (!TryCalculateRelativeSeek(
                    targetSeconds,
                    correctedProbe,
                    correctedPcr,
                    correctedSeconds,
                    calibration.ByteRate,
                    out relativeSeek))
                throw new FastSeekProbeException(FastSeekSkipReason.Correction);
            return CreateBoundTargetPlan(
                calibration,
                targetTimeTicks,
                correctedProbe.RangeStart,
                relativeSeek,
                probeCount: 2);
        }
        catch (FastSeekProbeException exception)
        {
            LogSkipped(exception.Reason);
        }
        catch (OperationCanceledException)
        {
            LogSkipped(FastSeekSkipReason.Cancelled);
        }
        catch (Exception)
        {
            LogSkipped(FastSeekSkipReason.Unavailable);
        }
        return null;
    }

    private FastSeekPlan CreateBoundTargetPlan(
        FastSeekPlan calibration,
        long targetTimeTicks,
        long byteOffset,
        TimeSpan relativeSeek,
        int probeCount)
    {
        var now = clock.UtcNow;
        return new FastSeekPlan(
            calibration.SourceFingerprint,
            calibration.MediaSourceId,
            targetTimeTicks,
            calibration.TotalLength,
            byteOffset,
            calibration.PacketStride,
            calibration.PacketOrigin,
            calibration.PcrPid,
            calibration.TimelineOriginPacketOffset,
            calibration.TimelineOriginClock27Mhz,
            calibration.ByteRate,
            calibration.DurationTicks,
            relativeSeek,
            probeCount,
            calibration.RuntimeGeneration,
            now,
            calibration.ExpiresAtUtc);
    }

    private static long AlignAndClamp(
        double candidate,
        TransportStreamFormat format,
        long totalLength)
    {
        if (double.IsNaN(candidate) || double.IsInfinity(candidate) || candidate < format.PacketOrigin ||
            candidate > totalLength - format.PacketStride)
            throw new FastSeekProbeException(FastSeekSkipReason.Correction);
        var maximumStart = Math.Max(format.PacketOrigin, totalLength - MaximumProbeBytes);
        var bounded = Math.Min(candidate, maximumStart);
        return TransportStreamClockParser.AlignDown((long)Math.Floor(bounded), format);
    }

    private static bool TryCalculateRelativeSeek(
        double targetSeconds,
        FastSeekProbeResult probe,
        PcrSample firstPcr,
        double firstPcrSeconds,
        double byteRate,
        out TimeSpan relativeSeek)
    {
        var bytesBeforePcr = firstPcr.PacketOffset - probe.RangeStart;
        var sampleStartSeconds = firstPcrSeconds - bytesBeforePcr / byteRate;
        relativeSeek = TimeSpan.FromSeconds(targetSeconds - sampleStartSeconds);
        return relativeSeek >= MinimumRelativeSeek && relativeSeek <= MaximumRelativeSeek;
    }

    private static void ValidateTotalLength(FastSeekProbeResult first, FastSeekProbeResult later)
    {
        if (first.TotalLength != later.TotalLength)
            throw new FastSeekProbeException(FastSeekSkipReason.RangeResponse);
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

    private void RemoveExpiredUnsafe()
    {
        var now = clock.UtcNow;
        foreach (var key in prepared.Where(pair => now >= pair.Value.ExpiresAtUtc).Select(pair => pair.Key).ToArray())
            prepared.Remove(key);
        foreach (var key in boundInputs
                     .Where(pair => now >= pair.Value.Calibration.ExpiresAtUtc)
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
            .OrderBy(pair => pair.Value.Calibration.ExpiresAtUtc)
            .First()
            .Key;
        boundInputs.Remove(oldest);
    }

    private void LogSkipped(FastSeekSkipReason reason) =>
        logger.Debug("STRM_BRIDGE_FAST_SEEK_SKIPPED reason=" + reason.ToString().ToLowerInvariant());

    private readonly struct PreparationKey : IEquatable<PreparationKey>
    {
        public PreparationKey(
            string sourceFingerprint,
            string mediaSourceId,
            long targetTimeTicks,
            long durationTicks,
            int runtimeGeneration)
        {
            SourceFingerprint = sourceFingerprint;
            MediaSourceId = mediaSourceId;
            TargetTimeTicks = targetTimeTicks;
            DurationTicks = durationTicks;
            RuntimeGeneration = runtimeGeneration;
        }

        private string SourceFingerprint { get; }

        private string MediaSourceId { get; }

        private long TargetTimeTicks { get; }

        private long DurationTicks { get; }

        private int RuntimeGeneration { get; }

        public bool Equals(PreparationKey other) =>
            TargetTimeTicks == other.TargetTimeTicks &&
            DurationTicks == other.DurationTicks &&
            RuntimeGeneration == other.RuntimeGeneration &&
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
                return hash * 397 ^ RuntimeGeneration;
            }
        }
    }

    private sealed class PendingFastSeek : IDisposable
    {
        private readonly CancellationTokenSource timeout;
        private readonly TaskCompletionSource<FastSeekPlan?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingFastSeek(TimeSpan lifetime)
        {
            timeout = new CancellationTokenSource();
            timeout.CancelAfter(lifetime);
        }

        public CancellationToken CancellationToken => timeout.Token;

        public Task<FastSeekPlan?> Task => completion.Task;

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
        private const int MaximumTargetPlans = 16;

        public FastSeekBinding(SourceIdentity source, PluginConfiguration options, FastSeekPlan calibration)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Options = options ?? throw new ArgumentNullException(nameof(options));
            Calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
            Plans.Add(calibration.TargetTimeTicks, calibration);
        }

        public SourceIdentity Source { get; }

        public PluginConfiguration Options { get; }

        public FastSeekPlan Calibration { get; }

        public Dictionary<long, FastSeekPlan> Plans { get; } = new();

        public void Store(FastSeekPlan plan)
        {
            if (!Plans.ContainsKey(plan.TargetTimeTicks) && Plans.Count >= MaximumTargetPlans)
            {
                var oldestTarget = Plans.Values
                    .Where(candidate => candidate.TargetTimeTicks != Calibration.TargetTimeTicks)
                    .OrderBy(candidate => candidate.CreatedAtUtc)
                    .Select(candidate => candidate.TargetTimeTicks)
                    .FirstOrDefault();
                if (oldestTarget != 0) Plans.Remove(oldestTarget);
            }
            Plans[plan.TargetTimeTicks] = plan;
        }
    }
}
