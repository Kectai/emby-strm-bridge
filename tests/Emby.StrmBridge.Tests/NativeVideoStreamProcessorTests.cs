using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class NativeVideoStreamProcessorTests
{
    [TestMethod]
    public async Task MatchingStaticStrmRequest_IsHandledByTheGateway()
    {
        using var fixture = CreateFixture();
        var sentinel = new object();
        var invocationCount = 0;
        fixture.InvokeGateway = (_, ticket, fileName, isHead) =>
        {
            invocationCount++;
            Assert.IsFalse(isHead);
            Assert.IsFalse(string.IsNullOrWhiteSpace(ticket));
            Assert.AreEqual("stream.mp4", fileName);
            return Task.FromResult(sentinel);
        };

        var handled = fixture.Processor.TryHandle(
            new TestService(fixture.Request),
            new TestVideoRequest
            {
                Id = fixture.Item.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                MediaSourceId = fixture.MediaSourceId,
                Static = true,
            },
            false,
            out var result);

        Assert.IsTrue(handled);
        Assert.IsNotNull(result);
        Assert.AreSame(sentinel, await result);
        Assert.AreEqual(1, invocationCount);
        Assert.AreEqual(1, fixture.Runtime.Tickets.Count);
    }

    [TestMethod]
    public void DynamicOrUnmatchedSourceRequests_KeepTheNativeService()
    {
        using var fixture = CreateFixture();
        var service = new TestService(fixture.Request);
        Assert.IsFalse(fixture.Processor.TryHandle(
            service,
            new TestVideoRequest
            {
                Id = fixture.Item.Id.ToString("N"),
                MediaSourceId = fixture.MediaSourceId,
                Static = false,
            },
            false,
            out _));
        Assert.IsFalse(fixture.Processor.TryHandle(
            service,
            new TestVideoRequest
            {
                Id = fixture.Item.Id.ToString("N"),
                MediaSourceId = "another-source",
                Static = true,
            },
            false,
            out _));
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
    }

    [TestMethod]
    public void NonStrmItems_KeepTheNativeService()
    {
        using var fixture = CreateFixture("video.mp4");
        Assert.IsFalse(fixture.Processor.TryHandle(
            new TestService(fixture.Request),
            new TestVideoRequest
            {
                Id = fixture.Item.Id.ToString("N"),
                MediaSourceId = fixture.MediaSourceId,
                Static = true,
            },
            false,
            out _));
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
    }

    private static Fixture CreateFixture(string fileName = "video.strm")
    {
        var workspace = new TestWorkspace();
        const string sourceUrl = "https://source.invalid/media.mp4";
        var path = workspace.Write(fileName, sourceUrl);
        var library = new Folder { Id = Guid.NewGuid(), Name = "Library" };
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            InternalId = Random.Shared.NextInt64(1, long.MaxValue),
            Path = path,
            Container = "mp4",
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
            Container = "mp4",
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
            "get_HttpMethod" => "GET",
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
        var fixture = new Fixture(workspace, runtime, item, mediaSourceId, request);
        fixture.RebuildProcessor(libraryManager, mediaSourceManager, logger);
        return fixture;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            TestWorkspace workspace,
            PluginRuntime runtime,
            Movie item,
            string mediaSourceId,
            IRequest request)
        {
            Workspace = workspace;
            Runtime = runtime;
            Item = item;
            MediaSourceId = mediaSourceId;
            Request = request;
        }

        private TestWorkspace Workspace { get; }
        public PluginRuntime Runtime { get; }
        public Movie Item { get; }
        public string MediaSourceId { get; }
        public IRequest Request { get; }
        public NativeVideoStreamProcessor Processor { get; private set; } = null!;
        public Func<IRequest, string, string, bool, Task<object>> InvokeGateway { get; set; } =
            (_, _, _, _) => Task.FromResult<object>(new object());

        public void RebuildProcessor(
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            ILogger logger)
        {
            Processor = new NativeVideoStreamProcessor(
                Runtime,
                libraryManager,
                mediaSourceManager,
                logger,
                _ => null,
                (request, ticket, fileName, isHead) => InvokeGateway(request, ticket, fileName, isHead));
        }

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

    private sealed class TestVideoRequest
    {
        public string Id { get; set; } = string.Empty;
        public string MediaSourceId { get; set; } = string.Empty;
        public bool Static { get; set; }
    }
}
