using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Subtitles;

// Subtitles are additional outputs of the VIDEO process. This component never opens
// the source URL or starts an extraction process, including on a cache miss or seek.
internal sealed class SharedSubtitleOutput : IDisposable
{
    internal const int MaximumJobs = 8;
    internal const int MaximumRetainedJobs = 64;
    internal const int MaximumRetainedClocks = 128;
    internal static readonly TimeSpan ClockRetention = TimeSpan.FromHours(6);
    internal const long MaximumRetainedBytes = 64 * 1024 * 1024;
    internal const int MaximumTracks = 8;
    internal const int MaximumReaders = 64;
    internal const int MaximumTrackBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan MaximumOpenWait = TimeSpan.FromSeconds(10);
    private readonly object sync = new();
    private readonly List<SharedSubtitleJob> jobs = new();
    private readonly List<ExternalSubtitleClock> clocks = new();
    private readonly PluginRuntime runtime;
    private readonly ILogger logger;
    private readonly string directory;
    private readonly Timer maintenance;
    private bool disposed;

    internal SharedSubtitleOutput(PluginRuntime runtime, string directory, ILogger logger)
    {
        this.runtime = runtime; this.directory = directory; this.logger = logger;
        Directory.CreateDirectory(directory);
        // A held owner file protects outputs belonging to another live server instance.
        foreach (var path in Directory.GetDirectories(directory))
            TryDeleteAbandoned(path);
        maintenance = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal int ActiveJobs { get { lock (sync) return jobs.Count(j => !j.Completed); } }

    internal SharedSubtitleJob? Attach(object runner, ref string arguments)
    {
        SharedSubtitleJob? job = null;
        var originalArguments = arguments;
        try
        {
            var options = runtime.GetOptionsSnapshot();
            if (!options.Enabled || !options.EnableSubtitles || options.PlaybackMode == Configuration.PlaybackRoutingMode.Native)
                return null;
            var command = Field(runner, "command");
            var state = Field(runner, "jobState");
            var input = Property(command, "Input0");
            var inputOptions = Property(input, "Options");
            var global = Property(command, "Options");
            var source = Property(state, "MediaSource") as MediaSourceInfo;
            var request = Property(state, "BaseRequest") ?? Property(state, "Request");
            var playSession = Property(request, "PlaySessionId") as string;
            var user = Property(state, "User") ?? Property(Property(state, "AuthorizationInfo"), "User");
            var userId = Property(user, "Id") is Guid id ? id : Guid.Empty;
            var inputUrl = Property(input, "Url") as string;
            if (source is null || !string.Equals(source.Container, "mkv", StringComparison.OrdinalIgnoreCase) ||
                source.RequiredHttpHeaders?.Count > 0 || userId == Guid.Empty ||
                string.IsNullOrWhiteSpace(playSession) || playSession.Length > 128 ||
                string.IsNullOrEmpty(inputUrl) || !arguments.Contains(inputUrl, StringComparison.Ordinal) ||
                !System.Text.RegularExpressions.Regex.IsMatch(arguments, @"(^|\s)-y(\s|$)") || !Uri.TryCreate(inputUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
                Property(global, "copyts") is not true || Property(global, "start_at_zero") is not true ||
                Property(inputOptions, "itsoffset") is not null || Property(inputOptions, "sseof") is not null ||
                Property(inputOptions, "seek_timestamp") is true)
                return null;
            var segments = uri.AbsolutePath.Split('/');
            if (segments.Length < 6 || segments[^4] != "Playback" || segments[^3] != "v3") return null;
            var ticket = segments[^2];
            if (!runtime.Tickets.TryInspect(ticket, out var payload) || payload is null ||
                payload.Purpose != PlaybackTicketPurpose.ServerFfmpeg ||
                !runtime.Tickets.MatchesTranscodeInput(ticket, payload.ItemId, source.Id, userId.ToString("N"),
                    payload.Source, runtime.Generation)) return null;
            var tracks = (source.MediaStreams ?? new List<MediaStream>()).Where(IsTextTrack).ToArray();
            var external = (source.MediaStreams ?? new List<MediaStream>()).Where(IsExternalAssTrack).Select(t => t.Index).ToArray();
            // The extraction budget does not apply to host-loaded sidecars.
            if (tracks.Length > MaximumTracks) tracks = Array.Empty<MediaStream>();
            if ((tracks.Length == 0 && external.Length == 0) ||
                tracks.Select(t => t.Index).Concat(external).Distinct().Count() != tracks.Length + external.Length)
                return null;
            var exitEvent = runner.GetType().GetEvent("Exited");
            if (exitEvent?.EventHandlerType != typeof(EventHandler<GenericEventArgs<int>>)) return null;
            var start = Property(inputOptions, "ss") is TimeSpan seek ? seek.Ticks : 0;
            var requestedStart = Property(request, "StartTimeTicks") is long ticks ? ticks : 0;
            var hasAudio = source.MediaStreams!.Any(stream => stream.Type == MediaStreamType.Audio &&
                System.Text.RegularExpressions.Regex.IsMatch(originalArguments, @"(^|\s)-map\s+0:" + stream.Index + @"(\s|$)"));
            var timeline = SharedSubtitleTimeline.Create(arguments, Property(Property(command, "Output0"), "Url") as string, requestedStart, hasAudio);
            if (tracks.Length == 0 && timeline is null) return null;
            lock (sync)
            {
                if (disposed || jobs.Any(j => ReferenceEquals(j.Runner, runner))) return null;
                foreach (var old in jobs.Where(j => j.Completed && !j.HasReaders).OrderBy(j => j.LastUsed).ToArray())
                {
                    if (jobs.Count < MaximumRetainedJobs && jobs.Where(j => j.Completed).Sum(j => j.OutputBytes) <= MaximumRetainedBytes) break;
                    RetainClock(old); old.Revoke(); old.Dispose(); jobs.Remove(old);
                }
                if (jobs.Count >= MaximumRetainedJobs || jobs.Count(j => !j.Completed) >= MaximumJobs) return null;
                job = new SharedSubtitleJob(directory, runner, payload, userId, playSession!, start, requestedStart, tracks, SubtitleDigest.Streams(source.MediaStreams!), timeline, external);
                var captured = job;
                EventHandler<GenericEventArgs<int>> handler = (_, _) => captured.Complete();
                exitEvent.AddEventHandler(runner, handler);
                job.Detach = () => exitEvent.RemoveEventHandler(runner, handler);
                jobs.Add(job);
                arguments += job.OutputArguments;
            }
            logger.Info((tracks.Length == 0 ? "STRM_BRIDGE_SUBTITLE_CLOCK_ATTACHED" : "STRM_BRIDGE_SUBTITLE_SHARED_ATTACHED") +
                " tracks=" + tracks.Length + " start_ms=" + start / 10000);
            return job;
        }
        catch (Exception exception)
        {
            arguments = originalArguments;
            if (job is not null) { job.Complete(); job.Revoke(); lock (sync) jobs.Remove(job); }
            logger.Warn("STRM_BRIDGE_SUBTITLE_SHARED_SKIPPED error=" + exception.GetType().Name);
            return null;
        }
    }

    // External ASS stays on SubtitleService. Only the source-to-MSE clock is read
    // here; no subtitle file, media request or additional FFmpeg input is opened.
    internal long ReadExternalClock(SubtitleRequestContext context)
    {
        if (!context.IsExternal || context.NativeHlsClock) throw new SubtitleProblem("outside-scope");
        if (!context.MseTimestampOffsetTicks.HasValue) throw new SubtitleProblem("timeline-unavailable");
        lock (sync)
        {
            PruneClocks();
            var candidates = disposed ? Array.Empty<SharedSubtitleJob>() : jobs.Where(j => j.Matches(context)).ToArray();
            var retained = disposed ? Array.Empty<ExternalSubtitleClock>() : clocks.Where(c => c.Matches(context)).ToArray();
            if (candidates.Length == 0 && retained.Length == 0) throw new SubtitleProblem("video-input-unavailable");
            if (candidates.Any(j => j.Timeline is null) || retained.Any(c => !c.MuxDelay.HasValue))
                throw new SubtitleProblem("timeline-unavailable");
            // Retain only authenticated clock metadata after output-file cleanup.
            // Old and new runners must still agree, including after eviction.
            var offsets = candidates.Select(j => j.Timeline!.MapMseOffset(context.MseTimestampOffsetTicks.Value))
                .Concat(retained.Select(c => SharedSubtitleTimeline.MapMseOffset(c.MuxDelay!.Value, context.MseTimestampOffsetTicks.Value)))
                .Distinct().ToArray();
            if (offsets.Length != 1) throw new SubtitleProblem("timeline-unavailable");
            foreach (var job in candidates) job.Touch();
            foreach (var clock in retained) clock.LastUsed = DateTimeOffset.UtcNow;
            return offsets[0];
        }
    }

    internal bool HasSession(SubtitleRequestContext context)
    {
        lock (sync)
        {
            var job = disposed ? null : jobs.LastOrDefault(candidate => candidate.Matches(context));
            if (job is null) logger.Debug("STRM_BRIDGE_SUBTITLE_SESSION_UNAVAILABLE reason=video-input-unavailable");
            else logger.Debug("STRM_BRIDGE_SUBTITLE_SESSION_BOUND playlist_start_ms=" + context.VideoStartTicks / 10000 +
                " input_start_ms=" + job.Start / 10000);
            return job is not null;
        }
    }

    internal async Task<SharedSubtitleStream> OpenAsync(SubtitleRequestContext context, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(MaximumOpenWait);
        try { return await OpenWithinDeadlineAsync(context, deadline.Token, cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new SubtitleProblem("video-input-unavailable"); }
    }
    private async Task<SharedSubtitleStream> OpenWithinDeadlineAsync(SubtitleRequestContext context, CancellationToken cancellation, CancellationToken requestCancellation)
    {
        if (context.NativeHlsClock || context.IsExternal) throw new SubtitleProblem("outside-scope");
        if (!context.MseTimestampOffsetTicks.HasValue)
            throw new SubtitleProblem("timeline-unavailable");
        // A video seek may start a new runner just after the browser requests subtitles.
        // Wait locally for that runner; never fall back to a second source read.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (context.StillAuthorized?.Invoke() == false) throw new SubtitleProblem("authorization-changed");
            SharedSubtitleJob[] candidates;
            lock (sync)
            {
                if (disposed) throw new SubtitleProblem("cache-invalidated");
                if (jobs.Sum(j => j.ReaderCount) >= MaximumReaders) throw new SubtitleBusyException();
                candidates = jobs.Where(j => j.Matches(context)).Reverse().ToArray();
            }
            SubtitleProblem? localFailure = null;
            var indexed = new List<(SharedSubtitleJob Job, SharedSubtitleExtent Extent)>();
            foreach (var candidate in candidates)
            {
                if (!candidate.CanServe(context.Start)) continue;
                try { indexed.Add((candidate, await candidate.Coverage(context.Index).ReadAsync(cancellation).ConfigureAwait(false))); }
                catch (Exception error) when (IsLocalCandidateFailure(error))
                {
                    localFailure = error as SubtitleProblem ?? new SubtitleProblem("video-input-unavailable");
                    logger.Debug("STRM_BRIDGE_SUBTITLE_CANDIDATE_SKIPPED reason=local-output-unavailable");
                }
            }
            // A long cue's end cannot rank an old partial output ahead of newly
            // decoded speech. Resolve only the candidates needed, so an unused
            // old TS file cannot delay or fail a healthy window.
            SharedSubtitleJob? selected = null;
            long selectedOffset = 0, selectedEnd = -1;
            foreach (var entry in indexed.OrderByDescending(value => value.Extent.LastStart))
            {
                var candidate = entry.Job;
                try
                {
                    var offset = candidate.Timeline is null ? 0 : await candidate.Timeline.ReadOffsetAsync(cancellation,
                        false, context.MseTimestampOffsetTicks, !candidate.Completed).ConfigureAwait(false);
                    var availableEnd = entry.Extent.LastEnd < 0 ? -1 : entry.Extent.LastEnd + offset;
                    if (availableEnd > context.Start)
                    { selected = candidate; selectedOffset = offset; selectedEnd = availableEnd; break; }
                    if (selected is null && !candidate.Completed)
                    { selected = candidate; selectedOffset = offset; }
                }
                catch (Exception error) when (IsLocalCandidateFailure(error))
                {
                    localFailure = error as SubtitleProblem ?? new SubtitleProblem("video-input-unavailable");
                    logger.Debug("STRM_BRIDGE_SUBTITLE_CANDIDATE_SKIPPED reason=local-output-unavailable");
                }
            }
            if (selected is not null)
            {
                lock (sync)
                {
                    if (disposed) throw new SubtitleProblem("cache-invalidated");
                    if (selected.Revoked) continue;
                    if (jobs.Sum(j => j.ReaderCount) >= MaximumReaders) throw new SubtitleBusyException();
                    cancellation.ThrowIfCancellationRequested();
                    if (context.StillAuthorized?.Invoke() == false) throw new SubtitleProblem("authorization-changed");
                    logger.Debug("STRM_BRIDGE_SUBTITLE_SHARED_OPEN index=" + context.Index + " start_ms=" + context.Start / 10000 +
                        " input_start_ms=" + selected.Start / 10000 + " available_end_ms=" + selectedEnd / 10000 +
                        " timeline_offset_ms=" + selectedOffset / 10000 + " hls_clock=" + (context.NativeHlsClock ? "native" : "hlsjs"));
                    return selected.Open(context, requestCancellation, selectedOffset);
                }
            }
            if (localFailure is not null && (indexed.Count == 0 || indexed.All(entry => entry.Job.Completed))) throw localFailure;
            await Task.Delay(100, cancellation).ConfigureAwait(false);
        }
        throw new SubtitleProblem("video-input-unavailable");
    }

    private static bool IsLocalCandidateFailure(Exception error) => error is IOException or UnauthorizedAccessException or DecoderFallbackException ||
        error is SubtitleProblem problem && problem.Reason is "timeline-unavailable" or "parse-invalid" or "output-budget" or "cache-invalidated";

    internal void Clear()
    {
        lock (sync)
        {
            clocks.Clear();
            foreach (var job in jobs) job.Revoke();
        }
        Sweep();
    }
    private void PruneClocks()
    {
        clocks.RemoveAll(c => !runtime.IsOperationCurrent(c.Generation) || DateTimeOffset.UtcNow - c.LastUsed > ClockRetention);
    }
    private void RetainClock(SharedSubtitleJob job)
    {
        if (disposed || job.Revoked || !runtime.IsOperationCurrent(job.Generation)) return;
        var clock = job.CopyExternalClock();
        if (clock is null) return;
        PruneClocks();
        if (clocks.Count >= MaximumRetainedClocks) clocks.Remove(clocks.OrderBy(c => c.LastUsed).First());
        clocks.Add(clock);
    }
    private void Sweep()
    {
        lock (sync)
        {
            PruneClocks();
            foreach (var job in jobs.ToArray())
            {
                if (!runtime.IsOperationCurrent(job.Generation)) job.Revoke();
                if (job.Completed && (job.Revoked || DateTimeOffset.UtcNow - job.LastUsed > TimeSpan.FromMinutes(10)))
                {
                    RetainClock(job); job.Revoke(); job.Dispose(); jobs.Remove(job);
                }
            }
            foreach (var job in jobs.Where(j => j.Completed && !j.HasReaders).OrderBy(j => j.LastUsed).ToArray())
            {
                if (jobs.Where(j => j.Completed).Sum(j => j.OutputBytes) <= MaximumRetainedBytes) break;
                RetainClock(job); job.Revoke(); job.Dispose(); jobs.Remove(job);
            }
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; maintenance.Dispose(); clocks.Clear();
            // Do not stop the video process or unlink files it is still writing.
            foreach (var job in jobs) job.Revoke();
        }
        Sweep();
    }
    internal static bool IsTextTrack(MediaStream stream) => stream.Index >= 0 &&
        stream.Type == MediaStreamType.Subtitle && !stream.IsExternal &&
        stream.Codec?.ToLowerInvariant() is "ass" or "ssa" or "srt" or "subrip";
    internal static bool IsExternalAssTrack(MediaStream stream) => stream.Index >= 0 &&
        stream.Type == MediaStreamType.Subtitle && stream.IsExternal &&
        stream.Codec?.ToLowerInvariant() is "ass" or "ssa";
    private static object? Property(object? value, string name) => value?.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
    private static object? Field(object value, string name) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value);
    internal static void TryDeleteAbandoned(string path)
    {
        try
        {
            using (new FileStream(Path.Combine(path, ".owner"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Directory.Delete(path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class SharedSubtitleJob : IDisposable
{
    private readonly object sync = new();
    private readonly string directory;
    private readonly FileStream owner;
    private readonly Dictionary<int, string> files = new();
    private readonly HashSet<int> externalTracks;
    private readonly Dictionary<int, SharedSubtitleCoverage> coverage = new();
    private readonly TicketPayload payload;
    private readonly Guid user;
    private readonly string playSession;
    private readonly string streamFingerprint;
    private readonly CancellationTokenSource revoked = new();
    private int readers;
    internal int ReaderCount => Volatile.Read(ref readers);
    internal bool HasReaders => ReaderCount != 0;
    private bool completed;
    private bool disposed;
    internal object Runner { get; }
    internal long Start { get; }
    internal long RequestedStart { get; }
    internal SharedSubtitleTimeline? Timeline { get; }
    internal int Generation => payload.RuntimeGeneration;
    internal long OutputBytes
    {
        get
        {
            long bytes = 0;
            foreach (var path in files.Values)
            {
                try { bytes += new FileInfo(path).Length; }
                catch (IOException) { }
            }
            return bytes;
        }
    }
    internal bool Completed { get { lock (sync) return completed; } }
    internal bool Revoked => revoked.IsCancellationRequested;
    internal Action? Detach;
    internal DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.UtcNow;
    internal string OutputArguments { get; }

    internal SharedSubtitleJob(string root, object runner, TicketPayload payload, Guid user, string playSession,
        long start, long requestedStart, IEnumerable<MediaStream> tracks, string streamFingerprint, SharedSubtitleTimeline? timeline = null, IEnumerable<int>? externalTracks = null)
    {
        this.streamFingerprint = streamFingerprint; Timeline = timeline;
        this.externalTracks = new HashSet<int>(externalTracks ?? Array.Empty<int>());
        Runner = runner; this.payload = payload; this.user = user; this.playSession = playSession; Start = start; RequestedStart = requestedStart;
        directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        owner = new FileStream(Path.Combine(directory, ".owner"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var arguments = new StringBuilder();
        try
        {
            foreach (var track in tracks)
            {
                var path = Path.Combine(directory, track.Index.ToString(CultureInfo.InvariantCulture) + ".ass");
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
                files.Add(track.Index, path);
                coverage.Add(track.Index, new SharedSubtitleCoverage(path));
                arguments.Append(BuildOutputArguments(path, track.Index, track.Codec));
            }
            OutputArguments = arguments.ToString();
        }
        catch { owner.Dispose(); Directory.Delete(directory, true); throw; }
    }
    internal static string BuildOutputArguments(string path, int index, string? codec)
    {
        if (index < 0 || path.IndexOfAny(new[] { '\r', '\n', '\0', '\"' }) >= 0)
            throw new ArgumentException("Unsafe subtitle output.");
        return " -map 0:" + index.ToString(CultureInfo.InvariantCulture) + " -vn -an -dn -c:s " +
            (codec?.ToLowerInvariant() is "ass" or "ssa" ? "copy" : "ass") +
            " -map_metadata -1 -map_chapters -1 -avoid_negative_ts disabled -flush_packets 1 -f ass -ignore_readorder 1 -fs " +
            SharedSubtitleOutput.MaximumTrackBytes.ToString(CultureInfo.InvariantCulture) + " \"" + path + "\"";
    }
    internal bool Matches(SubtitleRequestContext context) => !Revoked && context.Generation == Generation &&
        user == context.UserId && payload.ItemId == context.ItemId && payload.MediaSourceId == context.MediaSourceId &&
        playSession == context.PlaySessionId && streamFingerprint == context.StreamFingerprint && payload.Source.HasSameFileVersion(context.Source) &&
        (context.IsExternal ? externalTracks.Contains(context.Index) : files.ContainsKey(context.Index));
    // The playlist seek is not a job identifier: DynamicHlsService replaces it
    // with the requested segment's start before starting FFmpeg. The URL also
    // stays unchanged during a native HLS seek. Use the actual display window.
    // The browser floors window start to seconds; allow that rounding only.
    internal SharedSubtitleCoverage Coverage(int index) => coverage[index];
    internal bool CanServe(long windowStart) => Start <= windowStart + TimeSpan.TicksPerSecond - 1;

    internal SharedSubtitleStream Open(SubtitleRequestContext context, CancellationToken cancellation, long timelineOffset = 0)
    {
        lock (sync)
        {
            if (disposed || Revoked) throw new SubtitleProblem("cache-invalidated");
            LastUsed = DateTimeOffset.UtcNow;
            var stream = new SharedSubtitleStream(files[context.Index], context, () => Completed, cancellation, revoked.Token, timelineOffset);
            Interlocked.Increment(ref readers);
            stream.Release = () => Interlocked.Decrement(ref readers);
            return stream;
        }
    }
    internal void Touch() { lock (sync) { LastUsed = DateTimeOffset.UtcNow; } }
    internal ExternalSubtitleClock? CopyExternalClock() => externalTracks.Count == 0 ? null :
        new ExternalSubtitleClock(payload, user, playSession, streamFingerprint, externalTracks, Timeline?.MuxDelay);

    internal void ObserveStart(bool ready)
    {
        if (!ready) Revoke();
        try
        {
            if (Runner.GetType().GetProperty("IsRunning")?.GetValue(Runner) is false) Complete();
        }
        catch { /* An active process remains owned until its Exited event. */ }
    }
    internal void Complete()
    {
        lock (sync) { completed = true; }
        if (Revoked) Dispose();
    }
    internal void Revoke()
    {
        revoked.Cancel();
        if (Completed) Dispose();
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed || !completed) return;
            disposed = true;
            Detach?.Invoke(); Detach = null;
            owner.Dispose();
            SharedSubtitleOutput.TryDeleteAbandoned(directory);
        }
    }
}

// Reads a growing ASS file without waiting for video EOF. The same file can be read
// by multiple authenticated windows; it is never an HTTP input to another FFmpeg.
internal sealed class SharedSubtitleStream : Stream
{
    internal IDisposable? RequestLifetime;
    internal Action? Release;
    private readonly FileStream reader;
    private readonly Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly SubtitleRequestContext context;
    private readonly Func<bool> completed;
    private readonly long timelineOffset;
    private readonly CancellationTokenSource lifetime;
    private readonly StringBuilder partial = new();
    private byte[] pending = Array.Empty<byte>();
    private int offset;
    private long readBytes;
    private DateTimeOffset nextAccessCheck;
    private bool disposed;
    internal SharedSubtitleStream(string path, SubtitleRequestContext context, Func<bool> completed,
        CancellationToken request, CancellationToken job, long timelineOffset = 0)
    {
        reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        this.context = context; this.completed = completed; this.timelineOffset = timelineOffset;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(request, job);
        lifetime.CancelAfter(TimeSpan.FromMinutes(3));
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
        if (count == 0) return 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (disposed) throw new ObjectDisposedException(nameof(SharedSubtitleStream));
            if (DateTimeOffset.UtcNow >= nextAccessCheck)
            {
                if (context.StillAuthorized?.Invoke() == false) throw new SubtitleProblem("authorization-changed");
                nextAccessCheck = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
            }
            if (this.offset < pending.Length)
            {
                var n = Math.Min(count, pending.Length - this.offset);
                Array.Copy(pending, this.offset, buffer, offset, n); this.offset += n; return n;
            }
            // Read chunks ourselves: ReadLine at a growing-file EOF can return a
            // partial Dialogue, which must not reach the browser as a complete cue.
            var bytes = new byte[4096];
            var length = await reader.ReadAsync(bytes, 0, bytes.Length, linked.Token).ConfigureAwait(false);
            if (length == 0)
            {
                if (readBytes >= SharedSubtitleOutput.MaximumTrackBytes) throw new SubtitleProblem("output-budget");
                if (completed())
                {
                    decoder.GetChars(Array.Empty<byte>(), 0, 0, new char[4], 0, true);
                    if (partial.Length > 0) throw new SubtitleProblem("parse-invalid");
                    return 0;
                }
                await Task.Delay(100, linked.Token).ConfigureAwait(false);
                continue;
            }
            readBytes += length;
            if (readBytes > SharedSubtitleOutput.MaximumTrackBytes + 1024 * 1024) throw new SubtitleProblem("output-budget");
            var chars = new char[4096];
            var charCount = decoder.GetChars(bytes, 0, length, chars, 0, false);
            var end = charCount == 0 ? -1 : Array.LastIndexOf(chars, '\n', charCount - 1, charCount);
            if (partial.Length + charCount > 1024 * 1024) throw new SubtitleProblem("output-budget");
            if (end < 0) { partial.Append(chars, 0, charCount); continue; }
            partial.Append(chars, 0, end + 1);
            var text = partial.ToString();
            partial.Clear(); partial.Append(chars, end + 1, charCount - end - 1);
            var selected = new StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                var shifted = line.StartsWith("Dialogue:", StringComparison.Ordinal) ? Shift(line, timelineOffset) : line;
                if (!shifted.StartsWith("Dialogue:", StringComparison.Ordinal) || Intersects(shifted, context.Start, context.End))
                    selected.Append(shifted).Append('\n');
            }
            pending = Encoding.UTF8.GetBytes(selected.ToString()); this.offset = 0;
        }
    }
    internal static string Shift(string line, long offset)
    {
        if (offset == 0) return line;
        var fields = line.Split(new[] { ',' }, 4);
        if (fields.Length < 4 || !TimeSpan.TryParse(fields[1], CultureInfo.InvariantCulture, out var from) ||
            !TimeSpan.TryParse(fields[2], CultureInfo.InvariantCulture, out var to)) throw new SubtitleProblem("parse-invalid");
        string Stamp(long ticks)
        {
            var time = TimeSpan.FromTicks(Math.Max(0, ticks));
            return ((int)time.TotalHours).ToString(CultureInfo.InvariantCulture) + time.ToString(@"\:mm\:ss\.ff", CultureInfo.InvariantCulture);
        }
        return fields[0] + "," + Stamp(checked(from.Ticks + offset)) + "," + Stamp(checked(to.Ticks + offset)) + "," + fields[3];
    }
    internal static bool Intersects(string line, long start, long end)
    {
        var fields = line.Split(new[] { ',' }, 4);
        if (fields.Length < 4 || !TimeSpan.TryParse(fields[1], CultureInfo.InvariantCulture, out var from) ||
            !TimeSpan.TryParse(fields[2], CultureInfo.InvariantCulture, out var to)) throw new SubtitleProblem("parse-invalid");
        return from.Ticks < end && to.Ticks > start;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed) { disposed = true; lifetime.Cancel(); reader.Dispose(); RequestLifetime?.Dispose(); lifetime.Dispose(); Release?.Invoke(); Release = null; }
        base.Dispose(disposing);
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// No runner, output files, readers or process handles survive in this bounded cache.
internal sealed class ExternalSubtitleClock
{
    private readonly TicketPayload payload;
    private readonly Guid user;
    private readonly string session;
    private readonly string fingerprint;
    private readonly HashSet<int> tracks;
    internal int Generation => payload.RuntimeGeneration;
    internal long? MuxDelay { get; }
    internal DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    internal ExternalSubtitleClock(TicketPayload payload, Guid user, string session, string fingerprint, IEnumerable<int> tracks, long? muxDelay)
    {
        this.payload = payload; this.user = user; this.session = session; this.fingerprint = fingerprint;
        this.tracks = new HashSet<int>(tracks); MuxDelay = muxDelay;
    }
    internal bool Matches(SubtitleRequestContext context) => context.IsExternal && context.Generation == Generation &&
        context.UserId == user && context.ItemId == payload.ItemId && context.MediaSourceId == payload.MediaSourceId &&
        context.PlaySessionId == session && context.StreamFingerprint == fingerprint && tracks.Contains(context.Index) &&
        payload.Source.HasSameFileVersion(context.Source);
}
