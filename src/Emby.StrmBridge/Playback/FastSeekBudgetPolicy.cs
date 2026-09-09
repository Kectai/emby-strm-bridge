using System;

namespace Emby.StrmBridge.Playback;

// Media byte rate determines how much evidence to look for. Only measured response-body
// throughput may influence a request's transfer size; it does not change the absolute deadline.
internal static class FastSeekBudgetPolicy
{
    public static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(7);
    public static readonly TimeSpan TargetScanDuration = TimeSpan.FromSeconds(4);
    public const int InitialProbeBytes = 512 * 1024;
    public const int MaximumProbeBytes = 8 * 1024 * 1024;
    public const int MaximumTargetScanBytes = 24 * 1024 * 1024;
    public const int MaximumPreparationBytes = 32 * 1024 * 1024;
    public const int MaximumCorrections = 3;

    // These are engineering guardrails, not values established as optimal across CDNs.
    // Reserve half the estimated transfer capacity for variability and local processing.
    // A minimum attempt avoids rejecting a potentially useful sample on a noisy estimate.
    private const double TransferCapacityFraction = 0.5;
    private const int MinimumAdaptiveProbeBytes = InitialProbeBytes;
    private const int MinimumSyncPackets = 5;

    public static int CalculateTargetScanBytes(long totalLength, double durationSeconds, int packetStride)
    {
        ValidatePacketStride(packetStride);
        if (totalLength <= 0) throw new ArgumentOutOfRangeException(nameof(totalLength));
        if (!IsFinitePositive(durationSeconds)) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        var mediaBytesPerSecond = totalLength / durationSeconds;
        if (!IsFinitePositive(mediaBytesPerSecond)) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        var desiredBytes = mediaBytesPerSecond * TargetScanDuration.TotalSeconds;
        var boundedBytes = Math.Max(InitialProbeBytes, Math.Min(MaximumTargetScanBytes, desiredBytes));
        return AlignDown((int)Math.Ceiling(boundedBytes), packetStride);
    }

    public static int CalculatePreparationBytes(int targetScanBytes)
    {
        if (targetScanBytes <= 0) throw new ArgumentOutOfRangeException(nameof(targetScanBytes));
        var plannedBytes = InitialProbeBytes + (long)targetScanBytes * (1 + MaximumCorrections);
        return (int)Math.Min(MaximumPreparationBytes, plannedBytes);
    }

    public static int CalculateRequestBytes(
        int candidateBytes,
        int packetStride,
        TimeSpan remainingPreparationTime,
        TimeSpan? latestSetupDuration,
        double? observedBodyBytesPerSecond)
    {
        ValidatePacketStride(packetStride);
        if (candidateBytes < 0) throw new ArgumentOutOfRangeException(nameof(candidateBytes));
        var selectedBytes = Math.Min(candidateBytes, MaximumProbeBytes);
        if (latestSetupDuration is TimeSpan setup && setup >= TimeSpan.Zero &&
            observedBodyBytesPerSecond is double bodyBytesPerSecond && IsFinitePositive(bodyBytesPerSecond))
        {
            var transferSeconds = Math.Max(0, remainingPreparationTime.TotalSeconds - setup.TotalSeconds);
            var estimatedBytes = transferSeconds * bodyBytesPerSecond * TransferCapacityFraction;
            // Clamp before converting: very large finite rates can overflow the product to infinity.
            selectedBytes = (int)Math.Min(selectedBytes, Math.Max(MinimumAdaptiveProbeBytes, estimatedBytes));
        }
        var alignedBytes = AlignDown(selectedBytes, packetStride);
        // The caller enforces real cancellation/deadline and remaining byte/window bounds.
        // Estimated transfer time alone never returns zero for a viable packet-sized candidate.
        return alignedBytes >= MinimumSyncPackets * packetStride ? alignedBytes : 0;
    }

    private static int AlignDown(int bytes, int packetStride) => bytes / packetStride * packetStride;

    private static bool IsFinitePositive(double value) =>
        value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);

    private static void ValidatePacketStride(int packetStride)
    {
        if (packetStride is not (188 or 192 or 204))
            throw new ArgumentOutOfRangeException(nameof(packetStride));
    }
}
