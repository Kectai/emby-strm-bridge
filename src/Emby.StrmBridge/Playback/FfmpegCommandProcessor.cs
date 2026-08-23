using System;
using System.Reflection;
using System.Threading;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Playback;

internal sealed class FfmpegCommandProcessor
{
    internal static readonly TimeSpan TargetTolerance = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaximumFirstSegmentAdvance = TimeSpan.FromSeconds(3);
    private readonly PluginRuntime runtime;
    private readonly ILogger logger;

    public FfmpegCommandProcessor(PluginRuntime runtime, ILogManager logManager)
        : this(
            runtime,
            (logManager ?? throw new ArgumentNullException(nameof(logManager)))
            .GetLogger(Plugin.Instance?.Name ?? "STRM Bridge"))
    {
    }

    internal FfmpegCommandProcessor(PluginRuntime runtime, ILogger logger)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool TryApply(object? command, CancellationToken cancellationToken = default)
    {
        if (command is null || runtime.FastSeek is null) return false;
        try
        {
            var operation = runtime.BeginOperation();
            var input = GetProperty(command, "Input0");
            var output = GetProperty(command, "Output0");
            var protocol = GetProperty(input, "InputProtocol");
            var inputOptions = GetProperty(input, "Options");
            var outputOptions = GetProperty(output, "Options");
            var muxer = GetProperty(output, "Muxer");
            var inputUrl = GetProperty(input, "Url") as string;
            var originalInputSeek = GetProperty(inputOptions, "ss");
            var originalSegmentDelta = GetProperty(muxer, "segment_time_delta");
            var originalSegmentTime = GetProperty(muxer, "segment_time");
            if (input is null || output is null || protocol is null || inputOptions is null ||
                outputOptions is null || muxer is null ||
                string.IsNullOrWhiteSpace(inputUrl) ||
                !string.Equals(GetProperty(protocol, "Key")?.ToString(), "http", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetProperty(muxer, "Key")?.ToString(), "segment", StringComparison.OrdinalIgnoreCase) ||
                originalInputSeek is not TimeSpan absoluteSeek ||
                originalSegmentTime is not TimeSpan segmentTime ||
                segmentTime <= TimeSpan.Zero || segmentTime > TimeSpan.FromMinutes(1) ||
                GetProperty(outputOptions, "ss") is not null ||
                GetProperty(outputOptions, "output_ts_offset") is not null)
                return false;
            if (!TryResolveSegmentTimeline(
                    command,
                    inputOptions,
                    outputOptions,
                    muxer,
                    originalSegmentDelta,
                    absoluteSeek,
                    segmentTime,
                    out var segmentDelta,
                    out var timelineContract))
                return false;

            var offsetProperty = GetWritableProperty(protocol, "offset");
            var seekableProperty = GetWritableProperty(protocol, "seekable");
            var inputSeekProperty = GetWritableProperty(inputOptions, "ss");
            var outputSeekProperty = GetWritableProperty(outputOptions, "ss");
            var outputTimestampOffsetProperty = GetWritableProperty(outputOptions, "output_ts_offset");
            var segmentDeltaProperty = GetWritableProperty(muxer, "segment_time_delta");
            var originalOffset = offsetProperty.GetValue(protocol);
            var originalSeekable = seekableProperty.GetValue(protocol);
            var originalOutputSeek = outputSeekProperty.GetValue(outputOptions);
            var originalOutputTimestampOffset = outputTimestampOffsetProperty.GetValue(outputOptions);
            var adjustedSegmentDelta = segmentDelta + TimeSpan.FromTicks(
                Math.Min(segmentTime.Ticks / 2, MaximumFirstSegmentAdvance.Ticks));
            if (originalOffset is not null || originalSeekable is not null ||
                originalOutputSeek is not null || originalOutputTimestampOffset is not null)
                return false;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                operation.CancellationToken,
                cancellationToken);
            if (!runtime.FastSeek.TryGetBoundPlan(
                    inputUrl,
                    operation.Generation,
                    absoluteSeek.Ticks,
                    linkedCancellation.Token,
                    out var plan) || plan is null ||
                Abs(absoluteSeek - TimeSpan.FromTicks(plan.TargetTimeTicks)) > TargetTolerance ||
                plan.ByteOffset < 0 || plan.ByteOffset >= plan.TotalLength ||
                plan.RelativeSeek < FastSeekCoordinator.MinimumRelativeSeek ||
                plan.RelativeSeek > FastSeekCoordinator.MaximumRelativeSeek)
                return false;
            if (!runtime.TryCommit(
                    operation.Generation,
                    () => true,
                    () => ApplyAtomically(
                        protocol,
                        inputOptions,
                        outputOptions,
                        muxer,
                        plan,
                        adjustedSegmentDelta,
                        offsetProperty,
                        seekableProperty,
                        inputSeekProperty,
                        outputSeekProperty,
                        outputTimestampOffsetProperty,
                        segmentDeltaProperty,
                        originalOffset,
                        originalSeekable,
                        originalInputSeek,
                        originalOutputSeek,
                        originalOutputTimestampOffset,
                        originalSegmentDelta)))
                return false;
            logger.Debug(
                "STRM_BRIDGE_FAST_SEEK_APPLIED packets=" + plan.PacketStride +
                " preroll_ms=" + (long)plan.RelativeSeek.TotalMilliseconds +
                " timeline=" + timelineContract);
            return true;
        }
        catch (Exception)
        {
            logger.Debug("STRM_BRIDGE_FAST_SEEK_SKIPPED reason=command-mutation");
            return false;
        }
    }

    private static bool TryResolveSegmentTimeline(
        object command,
        object inputOptions,
        object outputOptions,
        object muxer,
        object? originalSegmentDelta,
        TimeSpan absoluteSeek,
        TimeSpan segmentTime,
        out TimeSpan segmentDelta,
        out string timelineContract)
    {
        segmentDelta = default;
        timelineContract = string.Empty;
        if (originalSegmentDelta is TimeSpan nativeDelta)
        {
            if (Abs(nativeDelta + absoluteSeek) > TargetTolerance) return false;
            segmentDelta = nativeDelta;
            timelineContract = "native";
            return true;
        }
        if (originalSegmentDelta is not null ||
            GetProperty(command, "Options") is not { } globalOptions ||
            GetProperty(globalOptions, "copyts") is not true ||
            GetProperty(globalOptions, "start_at_zero") is not true ||
            !string.Equals(
                GetProperty(outputOptions, "avoid_negative_ts")?.ToString(),
                "disabled",
                StringComparison.OrdinalIgnoreCase) ||
            GetProperty(inputOptions, "itsoffset") is not null ||
            GetProperty(inputOptions, "sseof") is not null ||
            GetProperty(inputOptions, "skip_initial_bytes") is not null ||
            GetProperty(inputOptions, "seek_timestamp") is true ||
            GetProperty(muxer, "initial_offset") is not null ||
            GetProperty(muxer, "reset_timestamps") is true ||
            GetProperty(muxer, "segment_start_number") is not int segmentStartNumber ||
            segmentStartNumber < 0)
            return false;
        try
        {
            var indexedStart = TimeSpan.FromTicks(checked(segmentTime.Ticks * segmentStartNumber));
            if (Abs(indexedStart - absoluteSeek) > TargetTolerance) return false;
            segmentDelta = -absoluteSeek;
            timelineContract = "indexed";
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void ApplyAtomically(
        object protocol,
        object inputOptions,
        object outputOptions,
        object muxer,
        FastSeekPlan plan,
        TimeSpan adjustedSegmentDelta,
        PropertyInfo offsetProperty,
        PropertyInfo seekableProperty,
        PropertyInfo inputSeekProperty,
        PropertyInfo outputSeekProperty,
        PropertyInfo outputTimestampOffsetProperty,
        PropertyInfo segmentDeltaProperty,
        object? originalOffset,
        object? originalSeekable,
        object? originalInputSeek,
        object? originalOutputSeek,
        object? originalOutputTimestampOffset,
        object? originalSegmentDelta)
    {
        try
        {
            offsetProperty.SetValue(protocol, plan.ByteOffset);
            seekableProperty.SetValue(protocol, false);
            inputSeekProperty.SetValue(inputOptions, null);
            outputSeekProperty.SetValue(outputOptions, plan.RelativeSeek);
            outputTimestampOffsetProperty.SetValue(outputOptions, TimeSpan.FromTicks(plan.TargetTimeTicks));
            segmentDeltaProperty.SetValue(muxer, adjustedSegmentDelta);
        }
        catch
        {
            TryRestore(offsetProperty, protocol, originalOffset);
            TryRestore(seekableProperty, protocol, originalSeekable);
            TryRestore(inputSeekProperty, inputOptions, originalInputSeek);
            TryRestore(outputSeekProperty, outputOptions, originalOutputSeek);
            TryRestore(outputTimestampOffsetProperty, outputOptions, originalOutputTimestampOffset);
            TryRestore(segmentDeltaProperty, muxer, originalSegmentDelta);
            throw;
        }
    }

    private static object? GetProperty(object? value, string name) => value?.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?.GetValue(value);

    private static PropertyInfo GetWritableProperty(object value, string name)
    {
        var property = value.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property?.SetMethod is not null
            ? property
            : throw new MissingMemberException(value.GetType().FullName, name);
    }

    private static void TryRestore(PropertyInfo property, object target, object? value)
    {
        try { property.SetValue(target, value); }
        catch { }
    }

    private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? -value : value;
}
