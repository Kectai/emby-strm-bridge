using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Policy;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Playback;

public sealed class PlaybackInfoProcessor
{
    private readonly PluginRuntime runtime;
    private readonly ILibraryManager libraryManager;
    private readonly IMediaSourceManager mediaSourceManager;
    private readonly ILogger logger;

    public PlaybackInfoProcessor(
        PluginRuntime runtime,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ILogManager logManager)
        : this(runtime, libraryManager, mediaSourceManager,
            (logManager ?? throw new ArgumentNullException(nameof(logManager)))
            .GetLogger(Plugin.Instance?.Name ?? "STRM Bridge"))
    {
    }

    internal PlaybackInfoProcessor(
        PluginRuntime runtime,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ILogger logger)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        this.mediaSourceManager = mediaSourceManager ?? throw new ArgumentNullException(nameof(mediaSourceManager));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal async Task<object> WrapAsync(Task<object> original, object service, object request)
    {
        var response = await original.ConfigureAwait(false);
        try
        {
            var playbackInfo = FindPlaybackInfoResponse(response);
            if (playbackInfo is null)
            {
                logger.Debug("STRM_BRIDGE_PLAYBACK_SKIPPED reason=response-shape");
                return response;
            }
            var itemIdText = GetStringProperty(request, "Id");
            var userId = GetStringProperty(request, "UserId");
            var serviceRequest = GetProperty(service, "Request") as IRequest;
            TryRewriteForRequest(
                playbackInfo,
                itemIdText,
                userId,
                GatewayRouteBuilder.GetApiPathBase(serviceRequest));
        }
        catch (Exception exception)
        {
            logger.Warn("STRM_BRIDGE_PLAYBACK_REWRITE_FAILED error=" + exception.GetType().Name);
        }
        return response;
    }

    internal int TryRewrite(
        PlaybackInfoResponse response,
        Guid requestedItemId,
        string? userId,
        string apiPathBase)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));
        var requestedItem = libraryManager.GetItemById(requestedItemId);
        return requestedItem is null ? 0 : TryRewrite(response, requestedItem, userId, apiPathBase);
    }

    internal int TryRewriteForRequest(
        PlaybackInfoResponse response,
        string? requestedItemId,
        string? userId,
        string apiPathBase)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));
        var requestedItem = ResolveItem(requestedItemId);
        if (requestedItem is not null) return TryRewrite(response, requestedItem, userId, apiPathBase);
        logger.Debug("STRM_BRIDGE_PLAYBACK_SKIPPED reason=request-item-unresolved");
        return 0;
    }

    private int TryRewrite(
        PlaybackInfoResponse response,
        BaseItem requestedItem,
        string? userId,
        string apiPathBase)
    {
        var operation = runtime.BeginOperation();
        var options = runtime.GetOptionsSnapshot();
        if (!options.Enabled || options.PlaybackMode == PlaybackRoutingMode.Native ||
            options.IncludedLibraryIds.Length == 0 || runtime.SourcePolicy is null ||
            response.MediaSources is null || response.MediaSources.Length == 0)
            return 0;

        if (!IsIncludedLibraryItem(requestedItem, options.IncludedLibraryIds)) return 0;

        var result = response.MediaSources.ToArray();
        var issuedTickets = new List<string>();
        var rewritten = 0;
        try
        {
            for (var index = 0; index < result.Length; index++)
            {
                var original = result[index];
                if (original is null) continue;
                var sourceItem = ResolveSourceItem(original, requestedItem);
                if (!IsIncludedItem(sourceItem, options.IncludedLibraryIds)) continue;

                SourceIdentity source;
                try { source = runtime.SourcePolicy.Read(sourceItem!.Path); }
                catch (SourcePolicyException) { continue; }
                if (!StaticMediaSourcePolicy.Matches(mediaSourceManager, sourceItem!, source) ||
                    !StaticMediaSourcePolicy.MatchesPlaybackSource(original, source))
                    continue;

                string? ticket = null;
                if (!runtime.TryCommit(
                        operation.Generation,
                        () => true,
                        () => ticket = runtime.Tickets.IssuePlayback(
                            sourceItem!.Id,
                            original.Id ?? string.Empty,
                            userId,
                            source,
                            operation.Generation,
                            TicketStore.ComputePlaybackLifetime(original.RunTimeTicks ?? sourceItem.RunTimeTicks))))
                {
                    foreach (var issued in issuedTickets) runtime.Tickets.Revoke(issued);
                    return 0;
                }
                issuedTickets.Add(ticket!);
                var playbackRoute = GatewayRouteBuilder.CreatePlaybackRoute(
                    apiPathBase, ticket!, original.Container);
                var clone = new MediaSourceInfo(original)
                {
                    DirectStreamUrl = playbackRoute,
                    AddApiKeyToDirectStreamUrl = false,
                };
                result[index] = clone;
                rewritten++;
            }
        }
        catch (TicketCapacityException)
        {
            foreach (var ticket in issuedTickets) runtime.Tickets.Revoke(ticket);
            logger.Warn("STRM_BRIDGE_TICKET_CAPACITY item=" + ShortId(requestedItem.Id));
            return 0;
        }
        catch
        {
            foreach (var ticket in issuedTickets) runtime.Tickets.Revoke(ticket);
            throw;
        }

        if (rewritten == 0) return 0;
        if (!runtime.TryCommit(
                operation.Generation,
                () => result.Length == response.MediaSources.Length &&
                      !result.Where((source, index) => !string.Equals(
                          source.Id, response.MediaSources[index].Id, StringComparison.Ordinal)).Any(),
                () => response.MediaSources = result))
        {
            foreach (var ticket in issuedTickets) runtime.Tickets.Revoke(ticket);
            return 0;
        }
        logger.Debug("STRM_BRIDGE_PLAYBACK_REWRITTEN item=" + ShortId(requestedItem.Id) +
                     " count=" + rewritten);
        return rewritten;
    }

    private BaseItem? ResolveSourceItem(MediaSourceInfo source, BaseItem? fallback)
    {
        return ResolveItem(source.ItemId) ?? fallback;
    }

    private BaseItem? ResolveItem(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;
        var normalized = itemId.Trim();
        if (long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId) &&
            internalId > 0)
            return libraryManager.GetItemById(internalId);
        return Guid.TryParse(normalized, out var id) ? libraryManager.GetItemById(id) : null;
    }

    private bool IsIncludedItem(BaseItem? item, string[] includedLibraryIds)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) ||
            !string.Equals(System.IO.Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase))
            return false;
        return IsIncludedLibraryItem(item, includedLibraryIds);
    }

    private bool IsIncludedLibraryItem(BaseItem? item, string[] includedLibraryIds)
    {
        if (item is null) return false;
        var allowed = new HashSet<string>(includedLibraryIds, StringComparer.OrdinalIgnoreCase);
        return libraryManager.GetCollectionFolders(item).Any(folder =>
            allowed.Contains(folder.Id.ToString("N")) || allowed.Contains(folder.Id.ToString()));
    }

    private static object? GetProperty(object value, string name) =>
        value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(value);

    private static string? GetStringProperty(object value, string name) => GetProperty(value, name)?.ToString();

    private static PlaybackInfoResponse? FindPlaybackInfoResponse(object response)
    {
        if (response is PlaybackInfoResponse direct) return direct;
        foreach (var propertyName in new[] { "Response", "Result", "Value" })
        {
            try
            {
                if (GetProperty(response, propertyName) is PlaybackInfoResponse nested) return nested;
            }
            catch { }
        }
        return null;
    }

    private static string ShortId(Guid itemId) => itemId.ToString("N").Substring(0, 8);
}
