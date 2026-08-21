using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class StrmMediaSourceProviderTests
{
    [TestMethod]
    public async Task EnabledPlayback_IssuesOpaqueGatewaySourceForIncludedStrm()
    {
        using var fixture = CreateFixture(enablePlayback: true);

        var sources = await fixture.Provider.GetMediaSources(fixture.Item, CancellationToken.None);

        Assert.AreEqual(1, sources.Count);
        var source = sources[0];
        Assert.AreEqual("STRM Bridge", source.Name);
        Assert.IsTrue(source.Path.StartsWith("http://127.0.0.1:8096/StrmBridge/Gateway/", StringComparison.Ordinal));
        Assert.IsTrue(source.DirectStreamUrl.StartsWith("/StrmBridge/Gateway/", StringComparison.Ordinal));
        Assert.AreEqual(new Uri(source.Path).AbsolutePath, source.DirectStreamUrl);
        Assert.AreEqual(source.Path, source.ProbePath);
        Assert.IsFalse(source.Path.Contains("source.invalid", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(source.SupportsDirectPlay);
        Assert.IsTrue(source.SupportsDirectStream);
        Assert.IsTrue(source.SupportsTranscoding);
        Assert.IsTrue(fixture.Logs.Any(message => message.Contains(
            "STRM_BRIDGE_PLAYBACK_SOURCE_ISSUED",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DisabledPlayback_ReturnsNoBridgeSource()
    {
        using var fixture = CreateFixture(enablePlayback: false);

        var sources = await fixture.Provider.GetMediaSources(fixture.Item, CancellationToken.None);

        Assert.IsEmpty(sources);
        Assert.IsFalse(fixture.Logs.Any(message => message.Contains(
            "STRM_BRIDGE_PLAYBACK_SOURCE_ISSUED",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DirectOrUnclassifiedSource_ReturnsNoBridgeCandidate()
    {
        using var fixture = CreateFixture(enablePlayback: true, redirectRequirement: false);

        var sources = await fixture.Provider.GetMediaSources(fixture.Item, CancellationToken.None);

        Assert.IsEmpty(sources);
        Assert.IsTrue(fixture.Logs.Any(message => message.Contains(
            "reason=redirect_not_confirmed",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NonLoopbackServerBase_IsRejectedWithoutIssuingSource()
    {
        using var fixture = CreateFixture(enablePlayback: true, localApiUrl: "https://server.invalid");

        var sources = await fixture.Provider.GetMediaSources(fixture.Item, CancellationToken.None);

        Assert.IsEmpty(sources);
        Assert.IsTrue(fixture.Logs.Any(message => message.Contains(
            "reason=local_api_unavailable",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CredentialBearingServerBase_IsRejectedWithoutIssuingSource()
    {
        using var fixture = CreateFixture(
            enablePlayback: true,
            localApiUrl: "http://user:password@127.0.0.1:8096");

        var sources = await fixture.Provider.GetMediaSources(fixture.Item, CancellationToken.None);

        Assert.IsEmpty(sources);
        Assert.IsTrue(fixture.Logs.Any(message => message.Contains(
            "reason=local_api_unavailable",
            StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ServerBasePath_IsPreservedInAbsoluteAndClientGatewayRoutes()
    {
        using var fixture = CreateFixture(
            enablePlayback: true,
            localApiUrl: "http://127.0.0.1:8096/emby");

        var sources = await fixture.Provider.GetMediaSources(
            fixture.Item,
            CancellationToken.None);
        Assert.AreEqual(1, sources.Count);
        var source = sources[0];

        Assert.IsTrue(source.Path.StartsWith(
            "http://127.0.0.1:8096/emby/StrmBridge/Gateway/",
            StringComparison.Ordinal));
        Assert.IsTrue(source.DirectStreamUrl.StartsWith(
            "/emby/StrmBridge/Gateway/",
            StringComparison.Ordinal));
    }

    private static Fixture CreateFixture(
        bool enablePlayback,
        string localApiUrl = "http://127.0.0.1:8096",
        bool? redirectRequirement = true)
    {
        var workspace = new TestWorkspace();
        var sourceUrl = "https://source.invalid/media";
        var itemPath = workspace.Write("playback.strm", sourceUrl);
        var libraryId = Guid.NewGuid();
        var parent = new Folder
        {
            Id = libraryId,
            InternalId = Random.Shared.NextInt64(1, long.MaxValue),
            Name = "Library",
        };
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Item",
            Path = itemPath,
            Parent = parent,
            ParentId = parent.InternalId,
            Container = "mkv",
            RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
            TotalBitrate = 4_000_000,
            MediaStreams = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "h264" },
            },
        };
        item.SetParent(parent);
        item.SetCachedParent(parent);

        var mediaSourceManager = TestProxy.Create<IMediaSourceManager>((method, args) =>
        {
            if (method.Name == nameof(IMediaSourceManager.GetStaticMediaSources))
            {
                return new List<MediaSourceInfo>
                {
                    new() { Path = sourceUrl, RequiresOpening = false },
                };
            }
            if (method.Name == nameof(IMediaSourceManager.GetMediaStreams))
                return item.MediaStreams;
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var libraryManager = TestProxy.Create<ILibraryManager>((method, _) =>
        {
            if (method.Name == nameof(ILibraryManager.GetCollectionFolders))
                return new[] { parent };
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var logs = new List<string>();
        var logger = TestProxy.Create<ILogger>((method, args) =>
        {
            if (method.Name == nameof(ILogger.Debug) && args is { Length: > 0 } && args[0] is string message)
                logs.Add(message);
            return TestDispatchProxy.DefaultValue(method.ReturnType);
        });
        var runtime = new PluginRuntime();
        runtime.Initialize(workspace.Path, new ManualClock(), new StubRedirectClient((_, _, _, _) =>
            Task.FromResult(new RedirectSourceResponse(404, null, null))));
        runtime.UpdateOptions(
            new PluginConfiguration
            {
                ConfigurationVersion = PluginConfiguration.CurrentConfigurationVersion,
                Enabled = true,
                EnablePlaybackSource = enablePlayback,
                IncludedLibraryIds = new[] { libraryId.ToString("N") },
            },
            invalidateSensitiveState: false);
        if (redirectRequirement.HasValue)
        {
            var source = runtime.SourcePolicy!.Read(itemPath);
            runtime.ExtractionState!.RecordSuccess(
                source.StorageKey,
                source.SourceFingerprint,
                redirectRequirement.Value);
        }
        var provider = new StrmMediaSourceProvider(
            mediaSourceManager,
            libraryManager,
            logger,
            () => runtime,
            () => localApiUrl);
        return new Fixture(workspace, runtime, provider, item, logs);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            TestWorkspace workspace,
            PluginRuntime runtime,
            StrmMediaSourceProvider provider,
            Movie item,
            List<string> logs)
        {
            Workspace = workspace;
            Runtime = runtime;
            Provider = provider;
            Item = item;
            Logs = logs;
        }

        private TestWorkspace Workspace { get; }

        private PluginRuntime Runtime { get; }

        public StrmMediaSourceProvider Provider { get; }

        public Movie Item { get; }

        public List<string> Logs { get; }

        public void Dispose()
        {
            Runtime.Dispose();
            Workspace.Dispose();
        }
    }
}
