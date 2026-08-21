using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Policy;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace Emby.StrmBridge.Playback;

public sealed class StrmMediaSourceProvider : IMediaSourceProvider
{
    private readonly IMediaSourceManager mediaSourceManager;
    private readonly ILibraryManager libraryManager;
    private readonly ILogger logger;
    private readonly Func<PluginRuntime?> runtimeProvider;
    private readonly Func<string> localApiUrlProvider;

    public StrmMediaSourceProvider(
        IMediaSourceManager mediaSourceManager,
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        ILogManager logManager)
    {
        this.mediaSourceManager = mediaSourceManager ?? throw new ArgumentNullException(nameof(mediaSourceManager));
        this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        logger = (logManager ?? throw new ArgumentNullException(nameof(logManager)))
            .GetLogger(Plugin.Instance?.Name ?? "STRM Bridge");
        runtimeProvider = () => Plugin.Runtime;
        if (applicationHost is null) throw new ArgumentNullException(nameof(applicationHost));
        localApiUrlProvider = () => applicationHost.GetLocalApiUrl(IPAddress.Loopback);
    }

    internal StrmMediaSourceProvider(
        IMediaSourceManager mediaSourceManager,
        ILibraryManager libraryManager,
        ILogger logger,
        Func<PluginRuntime?> runtimeProvider,
        Func<string> localApiUrlProvider)
    {
        this.mediaSourceManager = mediaSourceManager ?? throw new ArgumentNullException(nameof(mediaSourceManager));
        this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.runtimeProvider = runtimeProvider ?? throw new ArgumentNullException(nameof(runtimeProvider));
        this.localApiUrlProvider = localApiUrlProvider ?? throw new ArgumentNullException(nameof(localApiUrlProvider));
    }

    public Task<List<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        var runtime = runtimeProvider();
        var options = runtime?.GetOptionsSnapshot();
        if (runtime?.SourcePolicy is null ||
            options is null || !options.Enabled || !options.EnablePlaybackSource ||
            string.IsNullOrWhiteSpace(item?.Path) ||
            !string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase) ||
            !IsIncludedLibrary(item, options.IncludedLibraryIds))
        {
            return Task.FromResult(new List<MediaSourceInfo>());
        }
        logger.Debug("STRM_BRIDGE_PLAYBACK_SOURCE_REQUESTED item=" + ShortId(item.Id));

        SourceIdentity source;
        try { source = runtime.SourcePolicy.Read(item.Path); }
        catch
        {
            logger.Debug("STRM_BRIDGE_PLAYBACK_SOURCE_SKIPPED item=" + ShortId(item.Id) +
                         " reason=source_rejected");
            return Task.FromResult(new List<MediaSourceInfo>());
        }

        if (!StaticMediaSourcePolicy.Matches(mediaSourceManager, item, source))
        {
            logger.Debug("STRM_BRIDGE_PLAYBACK_SOURCE_SKIPPED item=" + ShortId(item.Id) +
                         " reason=static_source_mismatch");
            return Task.FromResult(new List<MediaSourceInfo>());
        }

        if (runtime.ExtractionState?.GetRedirectBridgeRequirement(
                source.StorageKey,
                source.SourceFingerprint) != true)
        {
            logger.Debug("STRM_BRIDGE_PLAYBACK_SOURCE_SKIPPED item=" + ShortId(item.Id) +
                         " reason=redirect_not_confirmed");
            return Task.FromResult(new List<MediaSourceInfo>());
        }

        var id = "strmbridge-" + source.StorageKey.Substring(0, 16);
        if (!TryGetLocalApiBase(out var localApiBase))
        {
            logger.Debug("STRM_BRIDGE_PLAYBACK_SOURCE_SKIPPED item=" + ShortId(item.Id) +
                         " reason=local_api_unavailable");
            return Task.FromResult(new List<MediaSourceInfo>());
        }
        string ticket;
        try
        {
            ticket = runtime.Tickets.Issue(
                TicketScope.PlaybackRedirect,
                item.Id,
                id,
                source,
                boundLifetime: TicketStore.ComputeBoundPlaybackLifetime(item.RunTimeTicks));
        }
        catch (TicketCapacityException)
        {
            logger.Debug("STRM_BRIDGE_PLAYBACK_SOURCE_SKIPPED item=" + ShortId(item.Id) +
                         " reason=ticket_capacity");
            return Task.FromResult(new List<MediaSourceInfo>());
        }
        var gatewayRelativePath = "StrmBridge/Gateway/" + ticket + "/stream";
        var gatewayUri = new Uri(localApiBase, gatewayRelativePath);
        var gatewayPath = gatewayUri.PathAndQuery;
        var gatewayUrl = gatewayUri.AbsoluteUri;
        var bitrate = item.TotalBitrate > 0 ? item.TotalBitrate : (int?)null;
        var result = new MediaSourceInfo
        {
            Id = id,
            Name = "STRM Bridge",
            ItemId = item.Id.ToString("N"),
            Path = gatewayUrl,
            ProbePath = gatewayUrl,
            DirectStreamUrl = gatewayPath,
            Protocol = MediaProtocol.Http,
            ProbeProtocol = MediaProtocol.Http,
            IsRemote = true,
            RequiresOpening = false,
            RequiresClosing = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            Container = string.Equals(item.Container, "strm", StringComparison.OrdinalIgnoreCase) ? null : item.Container,
            RunTimeTicks = item.RunTimeTicks,
            Bitrate = bitrate,
            DefaultAudioStreamIndex = item.AudioStreamIndex,
            DefaultSubtitleStreamIndex = item.SubtitleStreamIndex,
            MediaStreams = mediaSourceManager.GetMediaStreams(item, cancellationToken),
        };
        logger.Debug("STRM_BRIDGE_PLAYBACK_SOURCE_ISSUED item=" + ShortId(item.Id));
        return Task.FromResult(new List<MediaSourceInfo> { result });
    }

    private bool IsIncludedLibrary(BaseItem item, string[] includedLibraryIds)
    {
        if (includedLibraryIds is null || includedLibraryIds.Length == 0) return false;
        var allowed = new HashSet<string>(includedLibraryIds, StringComparer.OrdinalIgnoreCase);
        return libraryManager.GetCollectionFolders(item)
            .Any(folder => allowed.Contains(folder.Id.ToString("N")) || allowed.Contains(folder.Id.ToString()));
    }

    private static string ShortId(Guid itemId) => itemId.ToString("N").Substring(0, 8);

    private bool TryGetLocalApiBase(out Uri localApiBase)
    {
        localApiBase = null!;
        try
        {
            if (!Uri.TryCreate(localApiUrlProvider(), UriKind.Absolute, out var candidate) ||
                !(string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
                !string.IsNullOrEmpty(candidate.UserInfo) ||
                !string.IsNullOrEmpty(candidate.Query) ||
                !string.IsNullOrEmpty(candidate.Fragment) ||
                !IPAddress.TryParse(candidate.Host, out var address) ||
                !IPAddress.IsLoopback(address))
                return false;
            var builder = new UriBuilder(candidate)
            {
                Path = candidate.AbsolutePath.TrimEnd('/') + "/",
            };
            localApiBase = builder.Uri;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<ILiveStream> OpenMediaSource(
        string openToken,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken) =>
        Task.FromException<ILiveStream>(new NotSupportedException("STRM Bridge sources do not require opening."));
}
