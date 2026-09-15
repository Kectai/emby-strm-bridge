using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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

    public bool TryApply(object? command, CancellationToken cancellationToken = default) =>
        TryApply(command, cancellationToken, out _);

    internal bool TryApply(object? command, CancellationToken cancellationToken, out FastSeekCommandMutation? mutation)
    {
        mutation = null;
        if (command is null || runtime.FastSeek is null || !runtime.GetOptionsSnapshot().EnableFastSeek)
            return false;
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
            var userAgentProperty = GetWritableProperty(protocol, "user_agent");
            var headersProperty = GetWritableProperty(protocol, "headers");
            var inputSeekProperty = GetWritableProperty(inputOptions, "ss");
            var outputSeekProperty = GetWritableProperty(outputOptions, "ss");
            var outputTimestampOffsetProperty = GetWritableProperty(outputOptions, "output_ts_offset");
            var segmentDeltaProperty = GetWritableProperty(muxer, "segment_time_delta");
            var originalOffset = offsetProperty.GetValue(protocol);
            var originalSeekable = seekableProperty.GetValue(protocol);
            var originalUserAgent = userAgentProperty.GetValue(protocol);
            var originalHeaders = headersProperty.GetValue(protocol);
            var originalOutputSeek = outputSeekProperty.GetValue(outputOptions);
            var originalOutputTimestampOffset = outputTimestampOffsetProperty.GetValue(outputOptions);
            var adjustedSegmentDelta = segmentDelta + TimeSpan.FromTicks(
                Math.Min(segmentTime.Ticks / 2, MaximumFirstSegmentAdvance.Ticks));
            if (originalOffset is not null || originalSeekable is not null ||
                !string.IsNullOrEmpty(originalUserAgent as string) || !string.IsNullOrEmpty(originalHeaders as string) ||
                !HasPlainGetProfile(protocol) ||
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
                    out var plan) || plan?.Representation is null ||
                Abs(absoluteSeek - TimeSpan.FromTicks(plan.TargetTimeTicks)) > TargetTolerance ||
                plan.ByteOffset < 0 || plan.ByteOffset >= plan.TotalLength ||
                plan.RelativeSeek < FastSeekCoordinator.MinimumRelativeSeek ||
                plan.RelativeSeek > FastSeekCoordinator.MaximumRelativeSeek)
                return false;
            if (plan.VideoStreamIndex.HasValue && !MatchesVideoSelection(command, output, plan.VideoStreamIndex.Value))
            {
                logger.Debug("STRM_BRIDGE_FAST_SEEK_SKIPPED reason=streamselection");
                return false;
            }
            var snapshots = new[]
            {
                new PropertySnapshot(protocol, offsetProperty, originalOffset),
                new PropertySnapshot(protocol, seekableProperty, originalSeekable),
                new PropertySnapshot(protocol, userAgentProperty, originalUserAgent),
                new PropertySnapshot(protocol, headersProperty, originalHeaders),
                new PropertySnapshot(inputOptions, inputSeekProperty, originalInputSeek),
                new PropertySnapshot(outputOptions, outputSeekProperty, originalOutputSeek),
                new PropertySnapshot(outputOptions, outputTimestampOffsetProperty, originalOutputTimestampOffset),
                new PropertySnapshot(muxer, segmentDeltaProperty, originalSegmentDelta),
            };
            Emby.StrmBridge.Domain.TicketPayload? activatedTicket = null;
            if (!runtime.TryCommit(operation.Generation, () => true, () =>
                {
                    try
                    {
                        offsetProperty.SetValue(protocol, plan.ByteOffset);
                        seekableProperty.SetValue(protocol, false);
                        userAgentProperty.SetValue(protocol, plan.Representation.UserAgent);
                        headersProperty.SetValue(protocol, "If-Match: " + plan.Representation.StrongETag +
                            "\r\nAccept: */*\r\nAccept-Encoding: identity\r\n");
                        inputSeekProperty.SetValue(inputOptions, null);
                        outputSeekProperty.SetValue(outputOptions, plan.RelativeSeek);
                        outputTimestampOffsetProperty.SetValue(outputOptions, TimeSpan.FromTicks(plan.TargetTimeTicks));
                        segmentDeltaProperty.SetValue(muxer, adjustedSegmentDelta);
                        if (!runtime.FastSeek.TryActivateInput(inputUrl, plan, runtime.Tickets, out activatedTicket))
                            throw new InvalidOperationException("The seek job ticket is no longer available.");
                    }
                    catch
                    {
                        Restore(snapshots);
                        throw;
                    }
                })) return false;
            var coordinator = runtime.FastSeek;
            mutation = new FastSeekCommandMutation(() =>
            {
                coordinator.DisableInput(inputUrl, activatedTicket!);
                Restore(snapshots);
            }, logger, () => runtime.IsOperationCurrent(operation.Generation),
                (startNative, token) => StartNativeIfCurrentAsync(operation, startNative, token));
            logger.Debug(
                "STRM_BRIDGE_FAST_SEEK_APPLIED packets=" + plan.PacketStride +
                " preroll_ms=" + (long)plan.RelativeSeek.TotalMilliseconds +
                (plan.VideoStreamIndex.HasValue ? " video_index=" + plan.VideoStreamIndex.Value : string.Empty) +
                " timeline=" + timelineContract);
            return true;
        }
        catch (Exception)
        {
            logger.Debug("STRM_BRIDGE_FAST_SEEK_SKIPPED reason=command-mutation");
            return false;
        }
    }

    internal Emby.StrmBridge.Subtitles.SharedSubtitleJob? AttachSubtitles(object runner, ref string arguments) =>
        runtime.SubtitlePatch?.CanServe == true ? runtime.Subtitles?.Shared.Attach(runner, ref arguments) : null;

    private async Task<bool> StartNativeIfCurrentAsync(OperationContext operation,
        Func<CancellationToken, Task<bool>> startNative, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            operation.CancellationToken, cancellationToken);
        Task<bool>? start = null;
        if (!runtime.TryCommit(operation.Generation, () => !linked.IsCancellationRequested,
                () => start = startNative(linked.Token))) return false;
        return await start!.ConfigureAwait(false);
    }

    private static bool MatchesVideoSelection(object command, object output, int selectedIndex)
    {
        var indices = new HashSet<int>();
        void Collect(object? source)
        {
            if (GetProperty(source, "InputIndex") is int inputIndex && inputIndex == 0 &&
                string.Equals(GetProperty(source, "MediaKind")?.ToString(), "Video", StringComparison.Ordinal) &&
                GetProperty(GetProperty(source, "Stream"), "Index") is int index)
                indices.Add(index);
        }
        Collect(GetProperty(GetProperty(output, "Video0"), "InputStream"));
        if (GetProperty(command, "Connections") is IEnumerable connections)
            foreach (var connection in connections) Collect(GetProperty(connection, "StreamSource"));
        return indices.Count == 1 && indices.Contains(selectedIndex);
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

    private static bool HasPlainGetProfile(object protocol) =>
        string.IsNullOrEmpty(GetProperty(protocol, "referer") as string) &&
        string.IsNullOrEmpty(GetProperty(protocol, "cookies") as string) &&
        GetProperty(protocol, "post_data") is null &&
        (GetProperty(protocol, "method") is not string method || string.IsNullOrEmpty(method) ||
         string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase));

    private static void Restore(IEnumerable<PropertySnapshot> snapshots)
    {
        Exception? failure = null;
        foreach (var snapshot in snapshots)
        {
            try { snapshot.Property.SetValue(snapshot.Target, snapshot.Value); }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null) throw new InvalidOperationException("The native command could not be restored.", failure);
    }

    private readonly struct PropertySnapshot
    {
        public PropertySnapshot(object target, PropertyInfo property, object? value)
        {
            Target = target;
            Property = property;
            Value = value;
        }

        public object Target { get; }
        public PropertyInfo Property { get; }
        public object? Value { get; }
    }

    private static object? GetProperty(object? value, string name)
    {
        for (var type = value?.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public |
                                                  BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null) return property.GetValue(value);
        }
        return null;
    }

    private static PropertyInfo GetWritableProperty(object value, string name)
    {
        var property = value.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return property?.SetMethod is not null
            ? property
            : throw new MissingMemberException(value.GetType().FullName, name);
    }

    private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? -value : value;
}


// One optimized Start invocation owns one rollback. Restoring first disables its job binding,
// so invoking the patched Start again cannot recursively apply the same optimization.
internal sealed class FastSeekCommandMutation
{
    private readonly Action restore;
    private readonly ILogger logger;
    private readonly Func<bool> isOperationCurrent;
    private readonly Func<Func<CancellationToken, Task<bool>>, CancellationToken, Task<bool>> startNativeIfCurrent;
    private int restored;

    public FastSeekCommandMutation(Action restore, ILogger logger, Func<bool> isOperationCurrent,
        Func<Func<CancellationToken, Task<bool>>, CancellationToken, Task<bool>> startNativeIfCurrent)
    {
        this.restore = restore;
        this.logger = logger;
        this.isOperationCurrent = isOperationCurrent;
        this.startNativeIfCurrent = startNativeIfCurrent;
    }

    internal async Task<bool> ObserveStartAsync(Task<bool> start, Func<CancellationToken, Task<bool>> startNative,
        CancellationToken cancellationToken)
    {
        // Emby's false result follows WaitAndKillAsync. Exceptions do not prove that
        // the old process stopped, so leave them to Emby's normal job cleanup.
        if (await start.ConfigureAwait(false)) return true;
        if (cancellationToken.IsCancellationRequested || !isOperationCurrent() || !TryRestore()) return false;
        logger.Debug("STRM_BRIDGE_FAST_SEEK_NATIVE_RETRY reason=startup_failed");
        return await startNativeIfCurrent(startNative, cancellationToken).ConfigureAwait(false);
    }

    private bool TryRestore()
    {
        if (Interlocked.Exchange(ref restored, 1) != 0) return false;
        try { restore(); return true; }
        catch
        {
            logger.Warn("STRM_BRIDGE_FAST_SEEK_NATIVE_RETRY_SKIPPED reason=command_restore");
            return false;
        }
    }
}
