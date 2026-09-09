using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Services;
using System.Runtime.CompilerServices;
using HarmonyLib;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Emby.StrmBridge.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TranscodeInputProcessorTests
{
    [TestMethod]
    public async Task CleanupHarmonyHook_FencesRoutedJobsAndLeavesUnownedJobsNative()
    {
        using var fixture = CreateFixture();
        fixture.Runtime.InitializeFastSeek(
            FastSeekCoordinatorTests.CreateLogger(),
            new SyntheticFastSeekProbeClient());
        var target = TimeSpan.FromSeconds(50);
        fixture.State.BaseRequest.StartTimeTicks = target.Ticks;
        fixture.State.MediaSource.RunTimeTicks = TimeSpan.FromSeconds(100).Ticks;
        var output = Path.Combine(Path.GetDirectoryName(fixture.Item.Path)!, "job", "stream.m3u8");
        var deleted = new List<string>();
        var fileSystem = TestProxy.Create<IFileSystem>((method, args) =>
        {
            if (method.Name == nameof(IFileSystem.DeleteDirectory)) deleted.Add((string)args![0]!);
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var manager = new TestEncodingManager(fileSystem);
        var harmony = new Harmony("StrmBridge.Tests.CleanupLifecycle");
        TranscodeInputPatchBridge.Attach(fixture.Processor);
        try
        {
            harmony.Patch(typeof(TestEncodingManager).GetMethod(nameof(TestEncodingManager.DeletePartialStreamFiles))!,
                prefix: new HarmonyMethod(typeof(TranscodeInputPatchBridge).GetMethod(nameof(TranscodeInputPatchBridge.CleanupPrefix))!));
            var oldAttempt = fixture.Processor.BeforeStart(fixture.Service, fixture.State, output)!;
            await fixture.Processor.Jobs.ObserveStartAsync(Task.FromResult(new object()), oldAttempt);
            var oldRoute = fixture.State.MediaPath;
            var oldTicket = oldRoute.Split('/')[^2];
            var oldJob = new TestJob { Path = output, MediaSource = fixture.State.MediaSource };
            var newAttempt = fixture.Processor.BeforeStart(fixture.Service, fixture.State, output)!;
            await fixture.Processor.Jobs.ObserveStartAsync(Task.FromResult(new object()), newAttempt);
            var newRoute = fixture.State.MediaPath;
            var newTicket = newRoute.Split('/')[^2];
            var newJob = new TestJob { Path = output, MediaSource = fixture.State.MediaSource };
            Assert.AreEqual(2, fixture.Runtime.Tickets.Count);
            Assert.IsTrue(fixture.Runtime.FastSeek!.TryGetBoundPlan(
                oldRoute, fixture.Runtime.Generation, target.Ticks, CancellationToken.None, out _));
            Assert.IsTrue(fixture.Runtime.FastSeek.TryGetBoundPlan(
                newRoute, fixture.Runtime.Generation, target.Ticks, CancellationToken.None, out _));

            manager.ActiveJob = newJob;
            await manager.DeletePartialStreamFiles(oldJob, 0, 0);
            Assert.HasCount(0, deleted);
            Assert.IsFalse(fixture.Runtime.Tickets.TryInspect(oldTicket, out _));
            Assert.IsTrue(fixture.Runtime.Tickets.TryInspect(newTicket, out _));
            Assert.IsFalse(fixture.Runtime.FastSeek.TryGetBoundPlan(
                oldRoute, fixture.Runtime.Generation, target.Ticks, CancellationToken.None, out _));
            Assert.IsTrue(fixture.Runtime.FastSeek.TryGetBoundPlan(
                newRoute, fixture.Runtime.Generation, target.Ticks, CancellationToken.None, out _));

            await manager.DeletePartialStreamFiles(newJob, 0, 0);
            Assert.HasCount(0, deleted);
            Assert.IsTrue(fixture.Runtime.Tickets.TryInspect(newTicket, out _));
            Assert.IsTrue(fixture.Runtime.FastSeek.TryGetBoundPlan(
                newRoute, fixture.Runtime.Generation, target.Ticks, CancellationToken.None, out _));

            manager.ActiveJob = null;
            await manager.DeletePartialStreamFiles(newJob, 0, 0);
            CollectionAssert.AreEqual(new[] { Path.GetDirectoryName(output)! }, deleted);
            Assert.IsFalse(fixture.Runtime.Tickets.TryInspect(newTicket, out _));
            Assert.IsFalse(fixture.Runtime.FastSeek.TryGetBoundPlan(
                newRoute, fixture.Runtime.Generation, target.Ticks, CancellationToken.None, out _));
            Assert.AreEqual(0, manager.NativeCleanups);
            await manager.DeletePartialStreamFiles(new TestJob { Path = output }, 0, 0);
            Assert.AreEqual(1, manager.NativeCleanups);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            TranscodeInputPatchBridge.Detach(fixture.Processor);
        }
    }

    [TestMethod]
    [DataRow("target")]
    [DataRow("session")]
    [DataRow("device")]
    [DataRow("user")]
    [DataRow("video")]
    [DataRow("version")]
    public void FailedSeekRetry_IsIsolatedByPlaybackIdentity(string change)
    {
        using var fixture = CreateFixture();
        fixture.State.BaseRequest.PlaySessionId = "session";
        fixture.State.BaseRequest.DeviceId = "device";
        fixture.State.BaseRequest.StartTimeTicks = TimeSpan.FromSeconds(50).Ticks;
        fixture.State.User = new TestUser { Id = Guid.NewGuid() };
        var output = Path.Combine(Path.GetDirectoryName(fixture.Item.Path)!, "job", "stream.m3u8");
        var attempt = fixture.Processor.BeforeStart(fixture.Service, fixture.State, output)!;
        fixture.Processor.Jobs.Complete(attempt, true, false);
        fixture.State.BaseRequest.StartTimeTicks += TimeSpan.FromMilliseconds(500).Ticks;
        Assert.Throws<TranscodeStartRejectedException>(() =>
            fixture.Processor.BeforeStart(fixture.Service, fixture.State, output));
        switch (change)
        {
            case "target": fixture.State.BaseRequest.StartTimeTicks = TimeSpan.FromSeconds(80).Ticks; break;
            case "session": fixture.State.BaseRequest.PlaySessionId = "other-session"; break;
            case "device": fixture.State.BaseRequest.DeviceId = "other-device"; break;
            case "user": fixture.State.User = new TestUser { Id = Guid.NewGuid() }; break;
            case "video": fixture.State.VideoStream = new MediaStream { Index = 3 }; break;
            case "version": File.AppendAllText(fixture.Item.Path, "\n"); break;
        }
        if (change is "user" or "version")
        {
            fixture.State.MediaSource.Path = fixture.State.MediaSource.ProbePath = File.ReadAllText(fixture.Item.Path).Trim();
        }
        var next = fixture.Processor.BeforeStart(fixture.Service, fixture.State, output);
        Assert.IsNotNull(next);
    }

    [TestMethod]
    public async Task HarmonyHook_ObservesTypedTaskFailureAndRejectsRetryBeforeStartingFfmpeg()
    {
        using var fixture = CreateFixture();
        fixture.Runtime.InitializeFastSeek(
            FastSeekCoordinatorTests.CreateLogger(),
            new SyntheticFastSeekProbeClient());
        fixture.State.BaseRequest.PlaySessionId = "session";
        fixture.State.BaseRequest.StartTimeTicks = TimeSpan.FromSeconds(50).Ticks;
        fixture.State.MediaSource.RunTimeTicks = TimeSpan.FromSeconds(100).Ticks;
        var output = Path.Combine(Path.GetDirectoryName(fixture.Item.Path)!, "job", "stream.m3u8");
        var harmony = new Harmony("StrmBridge.Tests.TranscodeLifecycle");
        TranscodeInputPatchBridge.Attach(fixture.Processor);
        try
        {
            harmony.Patch(typeof(TestService).GetMethod(nameof(TestService.StartFfMpeg))!,
                prefix: new HarmonyMethod(typeof(TranscodeInputPatchBridge).GetMethod(nameof(TranscodeInputPatchBridge.Prefix))!),
                postfix: new HarmonyMethod(typeof(TranscodeInputPatchBridge).GetMethod(nameof(TranscodeInputPatchBridge.Postfix))!));
            await Assert.ThrowsAsync<IOException>(() => fixture.Service.StartFfMpeg(fixture.State, output, CancellationToken.None, true));
            Assert.AreEqual(1, fixture.Service.Starts);
            Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
            Assert.IsNotNull(fixture.Service.LastInputPath);
            Assert.IsFalse(fixture.Runtime.FastSeek!.TryGetBoundPlan(
                fixture.Service.LastInputPath,
                fixture.Runtime.Generation,
                fixture.State.BaseRequest.StartTimeTicks.Value,
                CancellationToken.None,
                out _));
            await Assert.ThrowsAsync<TranscodeStartRejectedException>(() =>
                fixture.Service.StartFfMpeg(fixture.State, output, CancellationToken.None, true));
            Assert.AreEqual(1, fixture.Service.Starts);
            Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
            fixture.State.BaseRequest.StartTimeTicks = TimeSpan.FromSeconds(80).Ticks;
            await Assert.ThrowsAsync<IOException>(() => fixture.Service.StartFfMpeg(fixture.State, output, CancellationToken.None, true));
            Assert.AreEqual(2, fixture.Service.Starts);
            Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            TranscodeInputPatchBridge.Detach(fixture.Processor);
        }
    }

    [TestMethod]
    public void RegistrationFailure_RollsBackStateAndReleasesTheIssuedTicketAndBinding()
    {
        using var fixture = CreateFixture();
        fixture.Runtime.InitializeFastSeek(
            FastSeekCoordinatorTests.CreateLogger(),
            new SyntheticFastSeekProbeClient());
        var target = TimeSpan.FromSeconds(50);
        fixture.State.BaseRequest.StartTimeTicks = target.Ticks;
        fixture.State.MediaSource.RunTimeTicks = TimeSpan.FromSeconds(100).Ticks;
        var originalSource = fixture.State.MediaSource;
        var originalPath = fixture.State.MediaPath;

        Assert.Throws<ArgumentException>(() =>
            fixture.Processor.BeforeStart(fixture.Service, fixture.State, "relative-output.m3u8"));

        Assert.AreSame(originalSource, fixture.State.MediaSource);
        Assert.AreEqual(originalPath, fixture.State.MediaPath);
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
    }

    [TestMethod]
    public void MatchingStaticStrmState_RoutesTheFfmpegInputThroughTheLocalGateway()
    {
        using var fixture = CreateFixture();
        var originalMediaSource = fixture.State.MediaSource;

        Assert.IsTrue(fixture.Processor.TryRoute(fixture.Service, fixture.State));

        Assert.AreNotSame(originalMediaSource, fixture.State.MediaSource);
        StringAssert.StartsWith(
            fixture.State.MediaPath,
            "http://127.0.0.1:8096/emby/StrmBridge/Playback/v3/");
        StringAssert.EndsWith(fixture.State.MediaPath, "/stream.m2ts");
        Assert.AreEqual(fixture.State.MediaPath, fixture.State.DirectMediaPath);
        Assert.AreEqual(fixture.State.MediaPath, fixture.State.MediaSource.Path);
        Assert.AreEqual(fixture.State.MediaPath, fixture.State.MediaSource.ProbePath);
        Assert.AreEqual(MediaProtocol.Http, fixture.State.MediaProtocol);
        Assert.AreEqual(MediaProtocol.Http, fixture.State.DirectMediaProtocol);
        Assert.AreEqual(MediaProtocol.Http, fixture.State.MediaSource.Protocol);
        Assert.AreEqual(MediaProtocol.Http, fixture.State.MediaSource.ProbeProtocol);
        Assert.IsFalse(
            fixture.State.MediaSource.RequiredHttpHeaders?.ContainsKey("User-Agent") == true);
        Assert.AreEqual(1, fixture.Runtime.Tickets.Count);
        var ticket = fixture.State.MediaPath
            .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)[^2];
        Assert.IsTrue(fixture.Runtime.Tickets.TryInspect(ticket, out var payload));
        Assert.AreEqual(PlaybackTicketPurpose.ServerFfmpeg, payload!.Purpose);
    }

    [TestMethod]
    public void UnmatchedMediaSourceOrNonStrmItem_KeepsTheOriginalState()
    {
        using var unmatched = CreateFixture();
        unmatched.State.BaseRequest.MediaSourceId = "different-source";
        var originalPath = unmatched.State.MediaPath;
        var originalMediaSource = unmatched.State.MediaSource;
        Assert.IsFalse(unmatched.Processor.TryRoute(unmatched.Service, unmatched.State));
        Assert.AreSame(originalMediaSource, unmatched.State.MediaSource);
        Assert.AreEqual(originalPath, unmatched.State.MediaPath);
        Assert.AreEqual(0, unmatched.Runtime.Tickets.Count);

        using var localFile = CreateFixture("video.m2ts");
        originalPath = localFile.State.MediaPath;
        originalMediaSource = localFile.State.MediaSource;
        Assert.IsFalse(localFile.Processor.TryRoute(localFile.Service, localFile.State));
        Assert.AreSame(originalMediaSource, localFile.State.MediaSource);
        Assert.AreEqual(originalPath, localFile.State.MediaPath);
        Assert.AreEqual(0, localFile.Runtime.Tickets.Count);
    }

    [TestMethod]
    public void AudioStrmState_KeepsTheOriginalState()
    {
        using var fixture = CreateFixture("audio.strm", isAudio: true);
        var originalPath = fixture.State.MediaPath;
        var originalMediaSource = fixture.State.MediaSource;

        Assert.IsFalse(fixture.Processor.TryRoute(fixture.Service, fixture.State));

        Assert.AreSame(originalMediaSource, fixture.State.MediaSource);
        Assert.AreEqual(originalPath, fixture.State.MediaPath);
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
    }

    [TestMethod]
    [DataRow("requires-opening")]
    [DataRow("requires-closing")]
    [DataRow("open-token")]
    [DataRow("required-headers")]
    public void CurrentIneligibleMediaSource_KeepsTheOriginalState(string constraint)
    {
        using var fixture = CreateFixture();
        MakeIneligible(fixture.State.MediaSource, constraint);
        var originalPath = fixture.State.MediaPath;
        var originalMediaSource = fixture.State.MediaSource;

        Assert.IsFalse(fixture.Processor.TryRoute(fixture.Service, fixture.State));

        Assert.AreSame(originalMediaSource, fixture.State.MediaSource);
        Assert.AreEqual(originalPath, fixture.State.MediaPath);
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
    }

    [TestMethod]
    public void InvalidLocalOrigin_RevokesTheTicketAndKeepsTheOriginalState()
    {
        using var fixture = CreateFixture(localApiUrl: "http://external.invalid:8096/emby");
        var originalMediaSource = fixture.State.MediaSource;
        var originalPath = fixture.State.MediaPath;

        Assert.IsFalse(fixture.Processor.TryRoute(fixture.Service, fixture.State));

        Assert.AreSame(originalMediaSource, fixture.State.MediaSource);
        Assert.AreEqual(originalPath, fixture.State.MediaPath);
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
    }

    [TestMethod]
    public void MatchingAdaptiveSeekJob_PreparesAndBindsTheIssuedLoopbackInput()
    {
        using var fixture = CreateFixture();
        var logger = FastSeekCoordinatorTests.CreateLogger();
        var probe = new SyntheticFastSeekProbeClient();
        fixture.Runtime.InitializeFastSeek(logger, probe);
        var target = TimeSpan.FromSeconds(50);
        fixture.State.BaseRequest.StartTimeTicks = target.Ticks;
        fixture.State.MediaSource.RunTimeTicks = TimeSpan.FromSeconds(100).Ticks;

        Assert.IsTrue(fixture.Processor.TryRoute(fixture.Service, fixture.State));

        Assert.HasCount(2, probe.Offsets);
        Assert.IsTrue(fixture.Runtime.FastSeek!.TryGetBoundPlan(
            fixture.State.MediaPath,
            fixture.Runtime.Generation,
            target.Ticks,
            CancellationToken.None,
            out var plan));
        Assert.AreEqual(target.Ticks, plan!.TargetTimeTicks);
    }

    [TestMethod]
    public void DisabledFastSeek_RoutesNormallyWithoutPreparingOrBindingASeekPlan()
    {
        using var fixture = CreateFixture();
        var probe = new SyntheticFastSeekProbeClient();
        fixture.Runtime.InitializeFastSeek(FastSeekCoordinatorTests.CreateLogger(), probe);
        var options = fixture.Runtime.GetOptionsSnapshot();
        options.EnableFastSeek = false;
        fixture.Runtime.UpdateOptions(options, invalidateSensitiveState: false);
        var target = TimeSpan.FromSeconds(50);
        fixture.State.BaseRequest.StartTimeTicks = target.Ticks;
        fixture.State.MediaSource.RunTimeTicks = TimeSpan.FromSeconds(100).Ticks;

        Assert.IsTrue(fixture.Processor.TryRoute(fixture.Service, fixture.State));

        Assert.IsEmpty(probe.Offsets);
        Assert.IsFalse(fixture.Runtime.FastSeek!.TryGetBoundPlan(
            fixture.State.MediaPath,
            fixture.Runtime.Generation,
            target.Ticks,
            CancellationToken.None,
            out _));
    }

    [TestMethod]
    public void ReusedState_RebindsEachSeekAndRetryWithoutDiscardingPreparedPlans()
    {
        using var fixture = CreateFixture();
        var probe = new SyntheticFastSeekProbeClient();
        fixture.Runtime.InitializeFastSeek(FastSeekCoordinatorTests.CreateLogger(), probe);
        fixture.State.MediaSource.RunTimeTicks = TimeSpan.FromSeconds(100).Ticks;
        var previousRoutes = new List<(string Route, long Target)>();
        foreach (var seconds in new[] { 20, 50, 80, 50 })
        {
            var target = TimeSpan.FromSeconds(seconds).Ticks;
            fixture.State.BaseRequest.StartTimeTicks = target;
            // Emby can copy a source between jobs, so acceptance must not depend on object identity.
            fixture.State.MediaSource = new MediaSourceInfo(fixture.State.MediaSource);
            Assert.IsTrue(fixture.Processor.TryRoute(fixture.Service, fixture.State));
            Assert.IsFalse(previousRoutes.Any(previous => previous.Route == fixture.State.MediaPath));
            Assert.IsTrue(fixture.Runtime.FastSeek!.TryGetBoundPlan(fixture.State.MediaPath,
                fixture.Runtime.Generation, target, CancellationToken.None, out var plan));
            Assert.AreEqual(target, plan!.TargetTimeTicks);
            foreach (var previous in previousRoutes)
                Assert.IsTrue(fixture.Runtime.FastSeek.TryGetBoundPlan(previous.Route,
                    fixture.Runtime.Generation, previous.Target, CancellationToken.None, out _));
            previousRoutes.Add((fixture.State.MediaPath, target));
        }
        Assert.HasCount(6, probe.Offsets);
    }

    [TestMethod]
    [DataRow("ticket")]
    [DataRow("origin")]
    [DataRow("probe")]
    [DataRow("user")]
    [DataRow("generation")]
    [DataRow("source")]
    [DataRow("headers")]
    public void ReusedState_RejectsChangedRouteOrBinding(string change)
    {
        using var fixture = CreateFixture();
        fixture.State.User = new TestUser { Id = Guid.NewGuid() };
        Assert.IsTrue(fixture.Processor.TryRoute(fixture.Service, fixture.State));
        switch (change)
        {
            case "ticket":
                var parts = fixture.State.MediaSource.Path.Split('/');
                parts[^2] = new string('a', parts[^2].Length);
                fixture.State.MediaSource.Path = fixture.State.MediaSource.ProbePath = string.Join('/', parts);
                break;
            case "origin":
                fixture.State.MediaSource.Path = fixture.State.MediaSource.ProbePath =
                    fixture.State.MediaSource.Path.Replace("127.0.0.1", "untrusted.invalid");
                break;
            case "probe": fixture.State.MediaSource.ProbePath += "?unexpected=1"; break;
            case "user": fixture.State.User = new TestUser { Id = Guid.NewGuid() }; break;
            case "generation": fixture.Runtime.UpdateOptions(fixture.Runtime.GetOptionsSnapshot(), true); break;
            case "source": File.AppendAllText(fixture.Item.Path, "\n"); break;
            case "headers": fixture.State.MediaSource.RequiredHttpHeaders = new() { ["Authorization"] = "test" }; break;
        }
        var mediaSource = fixture.State.MediaSource;
        var ticketCount = fixture.Runtime.Tickets.Count;
        Assert.IsFalse(fixture.Processor.TryRoute(fixture.Service, fixture.State));
        Assert.AreSame(mediaSource, fixture.State.MediaSource);
        Assert.AreEqual(ticketCount, fixture.Runtime.Tickets.Count);
    }

    [TestMethod]
    public void SelectedVideoTrack_IsPassedThroughOnBothInitialAndReusedState()
    {
        using var fixture = CreateFixture();
        var probe = new MultiVideoFastSeekProbeClient(192);
        fixture.Runtime.InitializeFastSeek(FastSeekCoordinatorTests.CreateLogger(), probe);
        fixture.State.MediaSource.MediaStreams = MultiVideoFastSeekProbeClient.Streams();
        fixture.State.MediaSource.RunTimeTicks = TimeSpan.FromSeconds(100).Ticks;
        fixture.State.BaseRequest.StartTimeTicks = TimeSpan.FromSeconds(50).Ticks;
        foreach (var video in fixture.State.MediaSource.MediaStreams.ToArray())
        {
            fixture.State.VideoStream = video;
            Assert.IsTrue(fixture.Processor.TryRoute(fixture.Service, fixture.State));
            Assert.IsTrue(fixture.Runtime.FastSeek!.TryGetBoundPlan(fixture.State.MediaPath,
                fixture.Runtime.Generation, fixture.State.BaseRequest.StartTimeTicks.Value,
                CancellationToken.None, out var plan));
            Assert.AreEqual(video.Index, plan!.VideoStreamIndex);
        }
        Assert.AreEqual(4, probe.ReadCount);
    }

    private static Fixture CreateFixture(
        string fileName = "video.strm",
        string localApiUrl = "http://127.0.0.1:8096/emby",
        bool isAudio = false)
    {
        var workspace = new TestWorkspace();
        var sourceUrl = isAudio
            ? "https://source.invalid/media.mp3"
            : "https://source.invalid/media.m2ts";
        var container = isAudio ? "mp3" : "m2ts";
        var path = workspace.Write(fileName, sourceUrl);
        var library = new Folder { Id = Guid.NewGuid(), Name = "Library" };
        BaseItem item = isAudio ? new AudioItem() : new Movie();
        item.Id = Guid.NewGuid();
        item.InternalId = Random.Shared.NextInt64(1, long.MaxValue);
        item.Path = path;
        item.Container = container;
        item.Parent = library;
        item.SetParent(library);
        item.SetCachedParent(library);
        const string mediaSourceId = "selected-source";
        var mediaSource = new MediaSourceInfo
        {
            Id = mediaSourceId,
            ItemId = item.Id.ToString("N"),
            Path = sourceUrl,
            ProbePath = sourceUrl,
            Protocol = MediaProtocol.Http,
            ProbeProtocol = MediaProtocol.Http,
            Container = container,
        };
        var libraryManager = TestProxy.Create<ILibraryManager>((method, _) => method.Name switch
        {
            nameof(ILibraryManager.GetItemById) => item,
            nameof(ILibraryManager.GetCollectionFolders) => new[] { library },
            _ => TestDispatchProxy.DefaultValue(method.ReturnType),
        });
        var mediaSourceManager = TestProxy.Create<IMediaSourceManager>((method, _) =>
            method.Name == nameof(IMediaSourceManager.GetStaticMediaSources)
                ? new List<MediaSourceInfo> { mediaSource }
                : TestDispatchProxy.DefaultValue(method.ReturnType));
        var logger = TestProxy.Create<ILogger>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
        var request = TestProxy.Create<IRequest>((method, _) => method.Name switch
        {
            "get_RawUrl" => isAudio
                ? "/emby/Audio/item/stream.mp3?MediaSourceId=selected-source"
                : "/emby/Videos/item/master.m3u8?MediaSourceId=selected-source",
            _ => TestDispatchProxy.DefaultValue(method.ReturnType),
        });
        var runtime = new PluginRuntime();
        runtime.Initialize(workspace.Path, new ManualClock());
        runtime.UpdateOptions(new PluginConfiguration
        {
            Enabled = true,
            PlaybackMode = PlaybackRoutingMode.Adaptive,
            IncludedLibraryIds = new[] { library.Id.ToString("N") },
        }, invalidateSensitiveState: false);
        var state = new TestStreamState
        {
            BaseRequest = new TestEncodingRequest
            {
                Id = item.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MediaSourceId = mediaSourceId,
            },
            MediaSource = new MediaSourceInfo(mediaSource),
            MediaPath = sourceUrl,
            DirectMediaPath = sourceUrl,
            MediaProtocol = MediaProtocol.Http,
            DirectMediaProtocol = MediaProtocol.Http,
        };
        var processor = new TranscodeInputProcessor(
            runtime,
            libraryManager,
            mediaSourceManager,
            logger,
            () => localApiUrl);
        return new Fixture(workspace, runtime, processor, new TestService(request), state, item);
    }

    private static void MakeIneligible(MediaSourceInfo source, string constraint)
    {
        switch (constraint)
        {
            case "requires-opening": source.RequiresOpening = true; break;
            case "requires-closing": source.RequiresClosing = true; break;
            case "open-token": source.OpenToken = "open-token"; break;
            case "required-headers": source.RequiredHttpHeaders = new() { ["Authorization"] = "secret" }; break;
            default: throw new ArgumentOutOfRangeException(nameof(constraint));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            TestWorkspace workspace,
            PluginRuntime runtime,
            TranscodeInputProcessor processor,
            TestService service,
            TestStreamState state,
            BaseItem item)
        {
            Workspace = workspace;
            Runtime = runtime;
            Processor = processor;
            Service = service;
            State = state;
            Item = item;
        }

        private TestWorkspace Workspace { get; }
        public PluginRuntime Runtime { get; }
        public TranscodeInputProcessor Processor { get; }
        public TestService Service { get; }
        public TestStreamState State { get; }
        public BaseItem Item { get; }

        public void Dispose()
        {
            Runtime.Dispose();
            Workspace.Dispose();
        }
    }

    private sealed class TestService
    {
        public TestService(IRequest request) => Request = request;
        public IRequest Request { get; }
        public int Starts { get; private set; }
        public string? LastInputPath { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task<object> StartFfMpeg(TestStreamState state, string output, CancellationToken cancellationToken, bool acquireResources)
        {
            Starts++;
            LastInputPath = state.MediaPath;
            return Task.FromException<object>(new IOException());
        }
    }

    private sealed class TestJob
    {
        public MediaSourceInfo MediaSource { get; set; } = new();
        public string Path { get; set; } = string.Empty;
        public TestJobType Type { get; set; }
    }

    private enum TestJobType { Hls }

    private sealed class TestEncodingManager
    {
        private readonly IFileSystem _fileSystem;
        public TestEncodingManager(IFileSystem fileSystem) => _fileSystem = fileSystem;
        public int NativeCleanups { get; private set; }
        public TestJob? ActiveJob { get; set; }
        public TestJob? GetTranscodingJob(string path, TestJobType type) =>
            ActiveJob is { } active && active.Path == path && active.Type == type ? active : null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task DeletePartialStreamFiles(TestJob job, int retryCount, int delayMs)
        {
            NativeCleanups++;
            _fileSystem.DeleteDirectory(Path.GetDirectoryName(job.Path)!, true);
            return Task.CompletedTask;
        }
    }

    private sealed class TestEncodingRequest
    {
        public string Id { get; set; } = string.Empty;
        public string MediaSourceId { get; set; } = string.Empty;
        public long? StartTimeTicks { get; set; }
        public string? PlaySessionId { get; set; }
        public string? DeviceId { get; set; }
    }

    private sealed class TestStreamState
    {
        public TestEncodingRequest BaseRequest { get; set; } = new();
        public MediaSourceInfo MediaSource { get; set; } = new();
        public string MediaPath { get; set; } = string.Empty;
        public string DirectMediaPath { get; set; } = string.Empty;
        public MediaProtocol MediaProtocol { get; set; }
        public MediaProtocol DirectMediaProtocol { get; set; }
        public object? AuthorizationInfo { get; set; }
        public TestUser? User { get; set; }
        public MediaStream? VideoStream { get; set; }
    }

    private sealed class TestUser
    {
        public Guid Id { get; set; }
    }
}
