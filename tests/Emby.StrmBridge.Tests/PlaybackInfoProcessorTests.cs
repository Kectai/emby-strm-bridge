using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Services;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;

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
            response, fixture.Item.Id, "user-id", "/emby", "device-id");

        Assert.AreEqual(1, count);
        Assert.AreEqual(1, response.MediaSources.Length);
        Assert.AreEqual(original.Id, response.MediaSources[0].Id);
        Assert.AreEqual(original.Name, response.MediaSources[0].Name);
        Assert.AreEqual(original.Path, response.MediaSources[0].Path);
        Assert.AreEqual(original.ProbePath, response.MediaSources[0].ProbePath);
        Assert.AreSame(original.MediaStreams, response.MediaSources[0].MediaStreams);
        Assert.IsTrue(response.MediaSources[0].DirectStreamUrl.StartsWith(
            "/emby/StrmBridge/Playback/v3/", StringComparison.Ordinal));
        Assert.IsTrue(response.MediaSources[0].DirectStreamUrl.EndsWith("/stream.mkv", StringComparison.Ordinal));
        Assert.IsFalse(response.MediaSources[0].AddApiKeyToDirectStreamUrl);
        Assert.AreEqual(1, fixture.Runtime.Tickets.Count);
        var ticket = response.MediaSources[0].DirectStreamUrl
            .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)[^2];
        Assert.IsTrue(fixture.Runtime.Tickets.TryInspect(ticket, out var payload));
        Assert.AreEqual(PlaybackTicketPurpose.DirectClient, payload!.Purpose);
        Assert.IsTrue(payload.SourceRedirectHandoffAllowed);
        Assert.AreEqual(32, payload.DeviceBindingHash.Length);
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
        StringAssert.StartsWith(response.MediaSources[0].DirectStreamUrl, "/StrmBridge/Playback/v3/");
    }

    [TestMethod]
    public void AudioStrmInSelectedLibrary_KeepsOriginalPlaybackInfo()
    {
        using var fixture = CreateFixture(PlaybackRoutingMode.Adaptive, container: "mp3", isAudio: true);
        var source = new MediaSourceInfo
        {
            Id = "audio-source",
            ItemId = fixture.Item.Id.ToString("N"),
            Path = fixture.SourceUrl,
            ProbePath = fixture.SourceUrl,
            Container = "mp3",
        };
        var response = new PlaybackInfoResponse { MediaSources = new[] { source } };

        Assert.AreEqual(0, fixture.Processor.TryRewrite(
            response, fixture.Item.Id, "user-id", "/emby", "device-id"));
        Assert.AreSame(source, response.MediaSources[0]);
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
    }

    [TestMethod]
    [DataRow("requires-opening")]
    [DataRow("requires-closing")]
    [DataRow("open-token")]
    [DataRow("required-headers")]
    public void CurrentIneligibleMediaSource_KeepsOriginalPlaybackInfo(string constraint)
    {
        using var fixture = CreateFixture(PlaybackRoutingMode.Adaptive);
        var source = new MediaSourceInfo
        {
            Id = "current-source",
            ItemId = fixture.Item.Id.ToString("N"),
            Path = fixture.SourceUrl,
            ProbePath = fixture.SourceUrl,
            Container = "mkv",
        };
        MakeIneligible(source, constraint);
        var response = new PlaybackInfoResponse { MediaSources = new[] { source } };

        Assert.AreEqual(0, fixture.Processor.TryRewrite(
            response, fixture.Item.Id, "user-id", "/emby", "device-id"));
        Assert.AreSame(source, response.MediaSources[0]);
        Assert.AreEqual(0, fixture.Runtime.Tickets.Count);
    }

    private static Fixture CreateFixture(
        PlaybackRoutingMode mode,
        string sourceUrl = "https://source.invalid/media",
        string container = "mkv",
        bool isAudio = false)
    {
        var workspace = new TestWorkspace();
        var path = workspace.Write("playback.strm", sourceUrl);
        var library = new Folder { Id = Guid.NewGuid(), Name = "Library" };
        BaseItem item = isAudio ? new AudioItem() : new Movie();
        item.Id = Guid.NewGuid();
        item.InternalId = Random.Shared.NextInt64(1, long.MaxValue);
        item.Path = path;
        item.Container = container;
        item.Parent = library;
        item.MediaStreams = new List<MediaBrowser.Model.Entities.MediaStream>();
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
        runtime.Initialize(workspace.Path, new ManualClock());
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
            BaseItem item,
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

        public BaseItem Item { get; }

        public string SourceUrl { get; }

        public PlaybackInfoProcessor Processor { get; }

        public void Dispose()
        {
            Runtime.Dispose();
            Workspace.Dispose();
        }
    }
}
