using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class PlaybackInfoProcessorTests
{
    [TestMethod]
    public void MatchingSource_IsRewrittenInPlaceWithoutChangingVersionIdentity()
    {
        using var fixture = CreateFixture(PlaybackRoutingMode.Adaptive);
        var original = new MediaSourceInfo
        {
            Id = "version-one",
            ItemId = fixture.Item.Id.ToString("N"),
            Name = "Original version",
            Path = fixture.SourceUrl,
            ProbePath = fixture.SourceUrl,
            Container = "mkv",
            SupportsDirectPlay = true,
            AddApiKeyToDirectStreamUrl = true,
            MediaStreams = fixture.Item.MediaStreams,
        };
        var response = new PlaybackInfoResponse { MediaSources = new[] { original } };

        var count = fixture.Processor.TryRewrite(
            response, fixture.Item.Id, "user-id", "/emby");

        Assert.AreEqual(1, count);
        Assert.AreEqual(1, response.MediaSources.Length);
        Assert.AreEqual(original.Id, response.MediaSources[0].Id);
        Assert.AreEqual(original.Name, response.MediaSources[0].Name);
        Assert.AreEqual(original.Path, response.MediaSources[0].Path);
        Assert.AreEqual(original.ProbePath, response.MediaSources[0].ProbePath);
        Assert.AreSame(original.MediaStreams, response.MediaSources[0].MediaStreams);
        Assert.IsTrue(response.MediaSources[0].DirectStreamUrl.StartsWith(
            "/emby/StrmBridge/Playback/v2/", StringComparison.Ordinal));
        Assert.IsTrue(response.MediaSources[0].DirectStreamUrl.EndsWith("/stream.mkv", StringComparison.Ordinal));
        Assert.IsFalse(response.MediaSources[0].AddApiKeyToDirectStreamUrl);
        Assert.AreEqual(1, fixture.Runtime.Tickets.Count);
    }

    [TestMethod]
    public void NativeModeAndUnmatchedSource_KeepOriginalResponse()
    {
        using var native = CreateFixture(PlaybackRoutingMode.Native);
        var source = new MediaSourceInfo
        {
            Id = "source",
            ItemId = native.Item.Id.ToString("N"),
            Path = native.SourceUrl,
        };
        var response = new PlaybackInfoResponse { MediaSources = new[] { source } };
        Assert.AreEqual(0, native.Processor.TryRewrite(response, native.Item.Id, null, string.Empty));
        Assert.AreSame(source, response.MediaSources[0]);

        using var adaptive = CreateFixture(PlaybackRoutingMode.Adaptive);
        var unmatched = new MediaSourceInfo
        {
            Id = "source",
            ItemId = adaptive.Item.Id.ToString("N"),
            Path = "https://other.invalid/media",
        };
        response = new PlaybackInfoResponse { MediaSources = new[] { unmatched } };
        Assert.AreEqual(0, adaptive.Processor.TryRewrite(response, adaptive.Item.Id, null, string.Empty));
        Assert.AreSame(unmatched, response.MediaSources[0]);
    }

    [TestMethod]
    public void NumericHostItemIds_MapToTheUnderlyingLibraryItem()
    {
        using var fixture = CreateFixture(PlaybackRoutingMode.Adaptive);
        var source = new MediaSourceInfo
        {
            Id = "numeric-source",
            ItemId = fixture.Item.InternalId.ToString(CultureInfo.InvariantCulture),
            Path = fixture.SourceUrl,
            ProbePath = fixture.SourceUrl,
            Container = "mkv",
        };
        var response = new PlaybackInfoResponse { MediaSources = new[] { source } };

        var count = fixture.Processor.TryRewriteForRequest(
            response,
            fixture.Item.InternalId.ToString(CultureInfo.InvariantCulture),
            null,
            string.Empty);

        Assert.AreEqual(1, count);
        StringAssert.StartsWith(response.MediaSources[0].DirectStreamUrl, "/StrmBridge/Playback/v2/");
    }

    private static Fixture CreateFixture(PlaybackRoutingMode mode)
    {
        var workspace = new TestWorkspace();
        var sourceUrl = "https://source.invalid/media";
        var path = workspace.Write("playback.strm", sourceUrl);
        var library = new Folder { Id = Guid.NewGuid(), Name = "Library" };
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            InternalId = Random.Shared.NextInt64(1, long.MaxValue),
            Path = path,
            Container = "mkv",
            Parent = library,
            MediaStreams = new List<MediaBrowser.Model.Entities.MediaStream>(),
        };
        item.SetParent(library);
        item.SetCachedParent(library);
        var libraryManager = TestProxy.Create<ILibraryManager>((method, args) => method.Name switch
        {
            nameof(ILibraryManager.GetItemById) => item,
            nameof(ILibraryManager.GetCollectionFolders) => new[] { library },
            _ => TestDispatchProxy.DefaultValue(method.ReturnType),
        });
        var mediaSourceManager = TestProxy.Create<IMediaSourceManager>((method, _) =>
            method.Name == nameof(IMediaSourceManager.GetStaticMediaSources)
                ? new List<MediaSourceInfo> { new() { Path = sourceUrl } }
                : TestDispatchProxy.DefaultValue(method.ReturnType));
        var logger = TestProxy.Create<ILogger>((method, _) => TestDispatchProxy.DefaultValue(method.ReturnType));
        var runtime = new PluginRuntime();
        runtime.Initialize(workspace.Path, new ManualClock(), new StubRedirectClient((_, _, _, _) =>
            Task.FromResult(new RedirectSourceResponse(404, null, null))));
        runtime.UpdateOptions(new PluginConfiguration
        {
            Enabled = true,
            PlaybackMode = mode,
            IncludedLibraryIds = new[] { library.Id.ToString("N") },
        }, invalidateSensitiveState: false);
        return new Fixture(
            workspace,
            runtime,
            item,
            sourceUrl,
            new PlaybackInfoProcessor(
                runtime,
                libraryManager,
                mediaSourceManager,
                logger));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            TestWorkspace workspace,
            PluginRuntime runtime,
            Movie item,
            string sourceUrl,
            PlaybackInfoProcessor processor)
        {
            Workspace = workspace;
            Runtime = runtime;
            Item = item;
            SourceUrl = sourceUrl;
            Processor = processor;
        }

        private TestWorkspace Workspace { get; }

        public PluginRuntime Runtime { get; }

        public Movie Item { get; }

        public string SourceUrl { get; }

        public PlaybackInfoProcessor Processor { get; }

        public void Dispose()
        {
            Runtime.Dispose();
            Workspace.Dispose();
        }
    }
}
