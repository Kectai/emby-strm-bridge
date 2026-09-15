using System.Text;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using Emby.StrmBridge.Subtitles;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class SharedSubtitleTests
{
    private const string Header = "[Script Info]\nScriptType: v4.00+\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";
    private const string Cue = "Dialogue: 0,0:00:27.00,0:00:29.00,Default,,0,0,0,,共享字幕\n";

    [TestMethod]
    public async Task VideoRunner_UsesOneInputAndSharesCompletedLocalOutputAcrossWindows()
    {
        using var f = new Fixture();
        var runner = f.Runner(); var arguments = "-y -i \"" + f.InputUrl + "\" -map 0:0 -sn video.ts";
        var job = f.Output.Attach(runner, ref arguments);
        Assert.IsNotNull(job);
        Assert.AreEqual(1, arguments.Split("-i ").Length - 1);
        StringAssert.Contains(arguments, "-map 0:1 -vn -an -dn -c:s copy");
        StringAssert.Contains(arguments, "-ignore_readorder 1");
        StringAssert.Contains(arguments, "-fs 16777216");
        Assert.IsNull(f.Output.Attach(runner, ref arguments), "A runner must never receive duplicate outputs.");
        File.WriteAllText(Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Single(), Header + Cue, new UTF8Encoding(false));
        runner.Exit();
        for (var i = 0; i < 2; i++)
        {
            using var stream = await f.Output.OpenAsync(f.Context, CancellationToken.None);
            using var reader = new StreamReader(stream);
            StringAssert.Contains(await reader.ReadToEndAsync(), "共享字幕");
        }
        Assert.AreEqual(0, f.Runtime.Gateway!.ActiveRequests);
    }

    [TestMethod]
    public async Task UnobservableNativeClockIsRejectedInsteadOfGuessingARunnerOffset()
    {
        using var f = new Fixture();
        f.Context.NativeHlsClock = true;
        var native = await Assert.ThrowsExactlyAsync<SubtitleProblem>(() => f.Output.OpenAsync(f.Context, CancellationToken.None));
        Assert.AreEqual("outside-scope", native.Reason);
        f.Context.NativeHlsClock = false;
        f.Context.MseTimestampOffsetTicks = null;
        var unknown = await Assert.ThrowsExactlyAsync<SubtitleProblem>(() => f.Output.OpenAsync(f.Context, CancellationToken.None));
        Assert.AreEqual("timeline-unavailable", unknown.Reason);
        Assert.AreEqual(0, f.Runtime.Gateway!.ActiveRequests);
    }

    [TestMethod]
    public void HlsPlaylistStartDoesNotRejectTheRoundedSegmentRunner()
    {
        using var f = new Fixture();
        f.Context.VideoStartTicks = TimeSpan.FromSeconds(180).Ticks;
        var runner = f.Runner(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
        var job = f.Output.Attach(runner, ref arguments)!;
        f.Context.VideoStartTicks = 1842869822; // Real master.m3u8 StartTimeTicks.
        f.Context.Start = 1840000000;
        Assert.IsTrue(f.Output.HasSession(f.Context), "HLS starts FFmpeg at segment 30 (180 s), not the playlist seek value.");
        job.Complete();
    }

    [TestMethod]
    public async Task HlsBackwardSeekReadsTheEarlierOutputWithAnUnchangedPlaylistUrl()
    {
        using var f = new Fixture();
        foreach (var seconds in new[] { 150, 180 })
        {
            f.Context.VideoStartTicks = TimeSpan.FromSeconds(seconds).Ticks;
            var runner = f.Runner(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
            var before = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories);
            f.Output.Attach(runner, ref arguments);
            var path = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Except(before).Single();
            File.WriteAllText(path, Header + (seconds == 150
                ? "Dialogue: 0,0:02:32.00,0:02:34.00,Default,,0,0,0,,earlier job\n"
                : "Dialogue: 0,0:03:02.00,0:03:04.00,Default,,0,0,0,,later job\n"));
            runner.Exit();
        }
        f.Context.VideoStartTicks = 1842869822; // The playlist URL is unchanged by native HLS seeking.
        f.Context.Start = TimeSpan.FromSeconds(150).Ticks;
        f.Context.End = TimeSpan.FromSeconds(180).Ticks;
        using var stream = await f.Output.OpenAsync(f.Context, CancellationToken.None);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        StringAssert.Contains(text, "earlier job");
        Assert.IsFalse(text.Contains("later job"));
        Assert.AreEqual(0, f.Runtime.Gateway!.ActiveRequests);
    }

    [TestMethod]
    public async Task MultipleSeeksSelectTheOutputThatActuallyContainsTheRequestedTime()
    {
        using var f = new Fixture();
        foreach (var sample in new[] { (1044, 1194, 1203), (102, 328, 329), (618, 1090, 1098) })
        {
            f.Context.VideoStartTicks = TimeSpan.FromSeconds(sample.Item1).Ticks;
            var runner = f.Runner(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
            var before = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories);
            f.Output.Attach(runner, ref arguments);
            var path = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Except(before).Single();
            var start = TimeSpan.FromSeconds(sample.Item2).ToString(@"h\:mm\:ss\.ff");
            var end = TimeSpan.FromSeconds(sample.Item3).ToString(@"h\:mm\:ss\.ff");
            File.WriteAllText(path, Header + "Dialogue: 0," + start + "," + end + ",Default,,0,0,0,,fixture\n");
            runner.Exit();
        }
        f.Context.Start = TimeSpan.FromSeconds(1190).Ticks;
        f.Context.End = TimeSpan.FromSeconds(1200).Ticks;
        using var stream = await f.Output.OpenAsync(f.Context, CancellationToken.None);
        using var reader = new StreamReader(stream);
        StringAssert.Contains(await reader.ReadToEndAsync(), "0:19:54.00,0:20:03.00");
        Assert.AreEqual(0, f.Runtime.Gateway!.ActiveRequests);
    }

    [TestMethod]
    public async Task LongEarlyCueCannotShadowNewlyDecodedSpeechAfterSeek()
    {
        using var f = new Fixture();
        foreach (var sample in new[] { (0, "Dialogue: 0,0:00:00.00,1:00:00.00,Default,,0,0,0,,long sign\n"),
            (180, "Dialogue: 0,0:03:00.00,0:03:05.00,Default,,0,0,0,,new speech\n") })
        {
            f.Context.VideoStartTicks = TimeSpan.FromSeconds(sample.Item1).Ticks;
            var before = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories);
            var runner = f.Runner(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
            Assert.IsNotNull(f.Output.Attach(runner, ref arguments));
            var path = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Except(before).Single();
            File.WriteAllText(path, Header + sample.Item2); runner.Exit();
        }
        f.Context.Start = TimeSpan.FromSeconds(181).Ticks; f.Context.End = TimeSpan.FromSeconds(190).Ticks;
        using var stream = await f.Output.OpenAsync(f.Context, CancellationToken.None);
        using var reader = new StreamReader(stream);
        StringAssert.Contains(await reader.ReadToEndAsync(), "new speech");
    }

    [TestMethod]
    public async Task UnreadableOldCandidateDoesNotPoisonHealthyOutputOrSwallowAuthorization()
    {
        using var f = new Fixture();
        foreach (var text in new[] { "Dialogue: malformed\n", Cue })
        {
            var before = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories);
            var runner = f.Runner(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
            Assert.IsNotNull(f.Output.Attach(runner, ref arguments));
            File.WriteAllText(Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Except(before).Single(), Header + text);
            runner.Exit();
        }
        using (var stream = await f.Output.OpenAsync(f.Context, CancellationToken.None))
        using (var reader = new StreamReader(stream)) StringAssert.Contains(await reader.ReadToEndAsync(), "共享字幕");
        f.Context.StillAuthorized = () => false;
        var denied = await Assert.ThrowsExactlyAsync<SubtitleProblem>(() => f.Output.OpenAsync(f.Context, CancellationToken.None));
        Assert.AreEqual("authorization-changed", denied.Reason);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => f.Output.OpenAsync(f.Context, cancelled.Token));
    }

    [TestMethod]
    public async Task CoverageTracksStartSeparatelyFromLongEndAndResetsAfterTruncation()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("track.ass", Header + "Dialogue: 0,0:00:00.00,1:00:00.00,Default,,0,0,0,,long sign\n");
        var coverage = new SharedSubtitleCoverage(path);
        var first = await coverage.ReadAsync(CancellationToken.None);
        Assert.AreEqual(0L, first.LastStart); Assert.AreEqual(TimeSpan.FromHours(1).Ticks, first.LastEnd);
        File.AppendAllText(path, Cue);
        var grown = await coverage.ReadAsync(CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromSeconds(27).Ticks, grown.LastStart); Assert.AreEqual(first.LastEnd, grown.LastEnd);
        File.WriteAllText(path, Cue);
        var reset = await coverage.ReadAsync(CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromSeconds(27).Ticks, reset.LastStart);
        Assert.AreEqual(TimeSpan.FromSeconds(29).Ticks, reset.LastEnd);
    }

    [TestMethod]
    public void MaintenanceReclaimsCompletedBytesWithoutAnotherVideoAttach()
    {
        using var f = new Fixture();
        for (var index = 0; index < 5; index++)
        {
            var before = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories);
            var runner = f.Runner(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
            Assert.IsNotNull(f.Output.Attach(runner, ref arguments));
            using (var file = File.OpenWrite(Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Except(before).Single()))
                file.SetLength(SharedSubtitleOutput.MaximumTrackBytes);
            runner.Exit();
        }
        Assert.IsTrue(Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) > SharedSubtitleOutput.MaximumRetainedBytes);
        typeof(SharedSubtitleOutput).GetMethod("Sweep", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(f.Output, null);
        Assert.IsTrue(Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) <= SharedSubtitleOutput.MaximumRetainedBytes);
    }

    [TestMethod]
    public async Task CoverageIgnoresIncompleteUtf8TailAndRefreshesWhenLocalOutputGrows()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.Write("track.ass", Header);
        var coverage = new SharedSubtitleCoverage(path);
        Assert.AreEqual(-1L, await coverage.ReadLastEndAsync(CancellationToken.None));
        File.AppendAllText(path, Cue.TrimEnd('\n'));
        Assert.AreEqual(-1L, await coverage.ReadLastEndAsync(CancellationToken.None));
        File.AppendAllText(path, "\n");
        Assert.AreEqual(TimeSpan.FromSeconds(29).Ticks, await coverage.ReadLastEndAsync(CancellationToken.None));
        Assert.AreEqual(TimeSpan.FromSeconds(29).Ticks, await coverage.ReadLastEndAsync(CancellationToken.None));
    }

    [TestMethod]
    public void Sessions_EnforceOwnershipCapacityAndExpiry()
    {
        using var f = new Fixture();
        var logger = TestProxy.Create<ILogger>((m, _) => TestDispatchProxy.DefaultValue(m.ReturnType));
        using var coordinator = new SubtitleCoordinator(f.Runtime, null!, null!, logger);
        var context = f.Context;
        var runner = f.Runner(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
        var job = coordinator.Shared.Attach(runner, ref arguments);
        Assert.IsNotNull(job);
        job.Complete();
        var session = coordinator.CreateSession(context); var stranger = Guid.NewGuid();
        Assert.IsNull(coordinator.FindSession(session.Id, stranger));
        Assert.IsFalse(coordinator.EndSession(session.Id, stranger));
        for (var i = 1; i < 64; i++) coordinator.CreateSession(context);
        Assert.ThrowsExactly<SubtitleBusyException>(() => coordinator.CreateSession(context));
        session.LastUsed = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(11);
        Assert.IsNull(coordinator.FindSession(session.Id, context.UserId));
        var replacement = coordinator.CreateSession(context);
        Assert.IsTrue(session.Token.IsCancellationRequested);
        Assert.IsTrue(coordinator.EndSession(replacement.Id, context.UserId));
        Assert.IsTrue(replacement.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void MissingVideoJobIsTransientAndMustNotReturnNativeFallbackStatus()
    {
        using var workspace = new TestWorkspace();
        using var runtime = new PluginRuntime(); runtime.Initialize(workspace.Path);
        var logger = TestProxy.Create<ILogger>((m, _) => TestDispatchProxy.DefaultValue(m.ReturnType));
        using var coordinator = new SubtitleCoordinator(runtime, null!, null!, logger);
        var error = Assert.ThrowsExactly<SubtitleProblem>(() => coordinator.CreateSession(new SubtitleRequestContext { PlaySessionId = "not-ready" }));
        Assert.AreEqual("video-input-unavailable", error.Reason);
        Assert.AreEqual(503, SubtitleRequestProcessor.ProblemStatus(error.Reason));
        Assert.AreNotEqual(422, SubtitleRequestProcessor.ProblemStatus(error.Reason));
    }

    [TestMethod]
    public void Binding_DoesNotReuseAnotherUserSessionOrSource()
    {
        using var f = new Fixture(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
        var job = f.Output.Attach(f.Runner(), ref arguments)!;
        Assert.IsTrue(job.Matches(f.Context));
        var originalUser = f.Context.UserId; f.Context.UserId = Guid.NewGuid();
        Assert.IsFalse(job.Matches(f.Context)); f.Context.UserId = originalUser;
        f.Context.PlaySessionId = "different"; Assert.IsFalse(job.Matches(f.Context)); f.Context.PlaySessionId = "play";
        f.Context.VideoStartTicks++; Assert.IsTrue(job.Matches(f.Context)); f.Context.VideoStartTicks--;
        f.Context.Index = 9; Assert.IsFalse(job.Matches(f.Context)); f.Context.Index = 1;
        File.AppendAllText(f.Context.Source.LocalPath, "\n");
        f.Context.Source = f.Runtime.SourcePolicy!.Read(f.Context.Source.LocalPath);
        Assert.IsFalse(job.Matches(f.Context));
        job.Complete();
    }

    [TestMethod]
    public void DisabledFeatureAndForeignInputLeaveVideoArgumentsUntouched()
    {
        using var f = new Fixture(); var arguments = "original";
        var runner = f.Runner();
        f.Runtime.UpdateOptions(new PluginConfiguration { Enabled = true, EnableSubtitles = false }, false);
        Assert.IsNull(f.Output.Attach(runner, ref arguments));
        Assert.AreEqual("original", arguments);
        Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task GrowingFile_EmitsCompleteUtf8CuesBeforeVideoEofAndCancelDoesNotStopVideo()
    {
        using var f = new Fixture(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
        var runner = f.Runner(); var job = f.Output.Attach(runner, ref arguments)!;
        var path = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Single();
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await writer.WriteAsync(Encoding.UTF8.GetBytes(Header)); await writer.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var stream = await f.Output.OpenAsync(f.Context, timeout.Token);
        var buffer = new byte[8192];
        var length = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token);
        Assert.AreEqual(Header, Encoding.UTF8.GetString(buffer, 0, length));
        var bytes = Encoding.UTF8.GetBytes(Cue);
        // Split inside a UTF-8 character and before the final newline.
        await writer.WriteAsync(bytes.AsMemory(0, bytes.Length - 3)); await writer.FlushAsync();
        var read = stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token);
        await Task.Delay(150); Assert.IsFalse(read.IsCompleted);
        await writer.WriteAsync(bytes.AsMemory(bytes.Length - 3)); await writer.FlushAsync();
        length = await read;
        Assert.AreEqual(Cue, Encoding.UTF8.GetString(buffer, 0, length));
        Assert.IsFalse(job.Completed, "First subtitle must precede video EOF.");
        // The original open request must still cancel the returned stream after
        // its temporary candidate-search deadline has been disposed.
        var waiting = stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
        timeout.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);
        Assert.IsFalse(job.Completed); Assert.IsFalse(job.Revoked);
        runner.Exit();
    }

    [TestMethod]
    public async Task RevokingSessionAccessStopsGrowingFileReads()
    {
        using var f = new Fixture(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
        var runner = f.Runner(); f.Output.Attach(runner, ref arguments);
        var allowed = true; f.Context.StillAuthorized = () => allowed;
        using var stream = await f.Output.OpenAsync(f.Context, CancellationToken.None);
        allowed = false;
        var error = await Assert.ThrowsExactlyAsync<SubtitleProblem>(() => stream.ReadAsync(new byte[8], 0, 8));
        Assert.AreEqual("authorization-changed", error.Reason);
        runner.Exit();
    }

    [TestMethod]
    public void InvalidationWaitsForVideoExitBeforeDeletingOutputs()
    {
        using var f = new Fixture(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
        var runner = f.Runner(); var job = f.Output.Attach(runner, ref arguments)!;
        f.Output.Clear();
        Assert.IsTrue(job.Revoked); Assert.IsFalse(job.Completed);
        Assert.AreEqual(1, Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Length);
        runner.Exit();
        Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task MoreThanEightSeeksRetainAnEarlierBufferedSubtitleOutput()
    {
        using var f = new Fixture();
        for (var i = 0; i < 16; i++)
        {
            f.Context.VideoStartTicks = TimeSpan.FromSeconds(i * 60).Ticks;
            var runner = f.Runner(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
            var before = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories);
            Assert.IsNotNull(f.Output.Attach(runner, ref arguments));
            var path = Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Except(before).Single();
            File.WriteAllText(path, Header + Cue);
            runner.Exit();
        }
        f.Context.Start = TimeSpan.FromSeconds(27).Ticks;
        f.Context.End = TimeSpan.FromSeconds(30).Ticks;
        using var stream = await f.Output.OpenAsync(f.Context, CancellationToken.None);
        using var reader = new StreamReader(stream);
        StringAssert.Contains(await reader.ReadToEndAsync(), "共享字幕");
    }

    [TestMethod]
    public void RepeatedSeeksEvictCompletedOutputsInsteadOfExhaustingCapacity()
    {
        using var f = new Fixture();
        for (var i = 0; i < SharedSubtitleOutput.MaximumRetainedJobs * 2; i++)
        {
            f.Context.VideoStartTicks = TimeSpan.FromSeconds(i * 60).Ticks;
            var runner = f.Runner(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
            Assert.IsNotNull(f.Output.Attach(runner, ref arguments));
            runner.Exit();
        }
        Assert.IsTrue(Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Length <= SharedSubtitleOutput.MaximumRetainedJobs);
    }

    [TestMethod]
    public async Task LocalReaderQuotaIsReleasedOnDispose()
    {
        using var f = new Fixture(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
        var runner = f.Runner(); f.Output.Attach(runner, ref arguments);
        var streams = new List<Stream>();
        try
        {
            for (var i = 0; i < SharedSubtitleOutput.MaximumReaders; i++)
                streams.Add(await f.Output.OpenAsync(f.Context, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<SubtitleBusyException>(() => f.Output.OpenAsync(f.Context, CancellationToken.None));
            streams[0].Dispose();
            using var replacement = await f.Output.OpenAsync(f.Context, CancellationToken.None);
        }
        finally { foreach (var stream in streams) stream.Dispose(); runner.Exit(); }
    }

    [TestMethod]
    public async Task OversizedUnterminatedLineFailsWithinTheLocalBudget()
    {
        using var f = new Fixture(); var arguments = "-y -i \"" + f.InputUrl + "\" video.ts";
        var runner = f.Runner(); f.Output.Attach(runner, ref arguments);
        File.WriteAllText(Directory.GetFiles(f.Root, "*.ass", SearchOption.AllDirectories).Single(), new string('x', 1024 * 1024 + 1));
        var error = await Assert.ThrowsExactlyAsync<SubtitleProblem>(() => f.Output.OpenAsync(f.Context, CancellationToken.None));
        Assert.AreEqual("output-budget", error.Reason);
        runner.Exit();
    }

    [TestMethod]
    public void WindowKeepsLongOverlappingEventsWithoutMovingTheirTimestamp()
    {
        Assert.IsTrue(SharedSubtitleStream.Intersects("Dialogue: 0,0:00:02.00,0:03:29.00,Default", TimeSpan.FromSeconds(120).Ticks, TimeSpan.FromSeconds(180).Ticks));
        Assert.IsFalse(SharedSubtitleStream.Intersects(Cue, TimeSpan.FromSeconds(29).Ticks, TimeSpan.FromSeconds(60).Ticks));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestWorkspace workspace = new();
        internal readonly PluginRuntime Runtime = new();
        internal readonly SharedSubtitleOutput Output;
        internal readonly SubtitleRequestContext Context;
        internal readonly string Root;
        internal readonly string InputUrl;
        internal Fixture()
        {
            Runtime.Initialize(workspace.Path);
            Runtime.UpdateOptions(new PluginConfiguration { Enabled = true, EnableSubtitles = true }, false);
            Root = Path.Combine(workspace.Path, "outputs");
            var logger = TestProxy.Create<ILogger>((m, _) => TestDispatchProxy.DefaultValue(m.ReturnType));
            Output = new SharedSubtitleOutput(Runtime, Root, logger);
            Context = new SubtitleRequestContext
            {
                UserId = Guid.NewGuid(),
                MseTimestampOffsetTicks = 0,
                ItemId = Guid.NewGuid(),
                MediaSourceId = "source",
                PlaySessionId = "play",
                VideoStartTicks = TimeSpan.FromSeconds(25).Ticks,
                Start = TimeSpan.FromSeconds(25).Ticks,
                End = TimeSpan.FromSeconds(30).Ticks,
                Index = 1,
                Generation = Runtime.Generation,
                Source = Runtime.SourcePolicy!.Read(workspace.Write("input.strm", "https://source.invalid/file.mkv"))
            };
            var ticket = Runtime.Tickets.IssuePlayback(Context.ItemId, Context.MediaSourceId, Context.UserId.ToString("N"),
                Context.Source, PlaybackTicketPurpose.ServerFfmpeg, Context.Generation, TimeSpan.FromHours(1));
            InputUrl = GatewayRouteBuilder.CreateInternalPlaybackRoute("http://127.0.0.1:8096", "", ticket, "mkv")!;
        }
        internal FakeRunner Runner() => new(InputUrl, Context);
        public void Dispose() { Output.Dispose(); Runtime.Dispose(); workspace.Dispose(); }
    }
    internal sealed class FakeRunner
    {
        private readonly object command;
        private readonly object jobState;
        public event EventHandler<GenericEventArgs<int>>? Exited;
        internal FakeRunner(string url, SubtitleRequestContext context)
        {
            command = new
            {
                Input0 = new { Url = url, Options = new { ss = TimeSpan.FromTicks(context.VideoStartTicks) } },
                Options = new { copyts = true, start_at_zero = true }
            };
            var media = new MediaSourceInfo
            {
                Id = context.MediaSourceId,
                Container = "mkv",
                MediaStreams = new List<MediaStream> {
                    new() { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
                    new() { Index = 1, Type = MediaStreamType.Subtitle, Codec = "ass" } }
            };
            context.StreamFingerprint = SubtitleDigest.Streams(media.MediaStreams);
            jobState = new
            {
                MediaSource = media,
                BaseRequest = new { PlaySessionId = context.PlaySessionId, StartTimeTicks = context.VideoStartTicks },
                User = new { Id = context.UserId }
            };
        }
        internal void Exit() { GC.KeepAlive(command); GC.KeepAlive(jobState); Exited?.Invoke(this, new GenericEventArgs<int>(0)); }
    }
}
