using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class TranscodeInputProcessorTests
{
    [TestMethod]
    public void MatchingStaticStrmState_RoutesTheFfmpegInputThroughTheLocalGateway()
    {
        using var fixture = CreateFixture();
        var originalMediaSource = fixture.State.MediaSource;

        Assert.IsTrue(fixture.Processor.TryRoute(fixture.Service, fixture.State));

        Assert.AreNotSame(originalMediaSource, fixture.State.MediaSource);
        StringAssert.StartsWith(
            fixture.State.MediaPath,
            "http://127.0.0.1:8096/emby/StrmBridge/Playback/v2/");
        StringAssert.EndsWith(fixture.State.MediaPath, "/stream.m2ts");
        Assert.AreEqual(fixture.State.MediaPath, fixture.State.DirectMediaPath);
        Assert.AreEqual(fixture.State.MediaPath, fixture.State.MediaSource.Path);
        Assert.AreEqual(fixture.State.MediaPath, fixture.State.MediaSource.ProbePath);
        Assert.AreEqual(MediaProtocol.Http, fixture.State.MediaProtocol);
        Assert.AreEqual(MediaProtocol.Http, fixture.State.DirectMediaProtocol);
        Assert.AreEqual(MediaProtocol.Http, fixture.State.MediaSource.Protocol);
        Assert.AreEqual(MediaProtocol.Http, fixture.State.MediaSource.ProbeProtocol);
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

    private static Fixture CreateFixture(
        string fileName = "video.strm",
        string localApiUrl = "http://127.0.0.1:8096/emby")
    {
        var workspace = new TestWorkspace();
        const string sourceUrl = "https://source.invalid/media.m2ts";
        var path = workspace.Write(fileName, sourceUrl);
        var library = new Folder { Id = Guid.NewGuid(), Name = "Library" };
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            InternalId = Random.Shared.NextInt64(1, long.MaxValue),
            Path = path,
            Container = "m2ts",
            Parent = library,
        };
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
            Container = "m2ts",
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
            "get_RawUrl" => "/emby/Videos/item/master.m3u8?MediaSourceId=selected-source",
            _ => TestDispatchProxy.DefaultValue(method.ReturnType),
        });
        var runtime = new PluginRuntime();
        runtime.Initialize(workspace.Path, new ManualClock(), new StubRedirectClient((_, _, _, _) =>
            Task.FromResult(new RedirectSourceResponse(404, null, null))));
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

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            TestWorkspace workspace,
            PluginRuntime runtime,
            TranscodeInputProcessor processor,
            TestService service,
            TestStreamState state,
            Movie item)
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
        public Movie Item { get; }

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
    }

    private sealed class TestEncodingRequest
    {
        public string Id { get; set; } = string.Empty;
        public string MediaSourceId { get; set; } = string.Empty;
        public long? StartTimeTicks { get; set; }
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
    }
}
