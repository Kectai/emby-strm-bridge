using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Emby.StrmBridge.Api;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Policy;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Playback;

internal sealed class NativeVideoStreamProcessor
{
    private readonly PluginRuntime runtime;
    private readonly ILibraryManager libraryManager;
    private readonly IMediaSourceManager mediaSourceManager;
    private readonly ILogger logger;
    private readonly Func<IRequest, string?> resolveUserId;
    private readonly Func<IRequest, string?> resolveDeviceId;
    private readonly Func<IRequest, string, string, bool, Task<object>> invokeGateway;

    public NativeVideoStreamProcessor(
        PluginRuntime runtime,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IAuthorizationContext authorizationContext,
        IHttpResultFactory resultFactory,
        ILogManager logManager)
        : this(
            runtime,
            libraryManager,
            mediaSourceManager,
            (logManager ?? throw new ArgumentNullException(nameof(logManager)))
                .GetLogger(Plugin.Instance?.Name ?? "STRM Bridge"),
            request => ResolveUserId(authorizationContext, request),
            request => ResolveDeviceId(authorizationContext, request),
            (request, ticket, fileName, isHead) => InvokeGateway(
                libraryManager,
                authorizationContext,
                resultFactory,
                logManager,
                request,
                ticket,
                fileName,
                isHead))
    {
    }

    internal NativeVideoStreamProcessor(
        PluginRuntime runtime,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ILogger logger,
        Func<IRequest, string?> resolveUserId,
        Func<IRequest, string?> resolveDeviceId,
        Func<IRequest, string, string, bool, Task<object>> invokeGateway)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        this.mediaSourceManager = mediaSourceManager ?? throw new ArgumentNullException(nameof(mediaSourceManager));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.resolveUserId = resolveUserId ?? throw new ArgumentNullException(nameof(resolveUserId));
        this.resolveDeviceId = resolveDeviceId ?? throw new ArgumentNullException(nameof(resolveDeviceId));
        this.invokeGateway = invokeGateway ?? throw new ArgumentNullException(nameof(invokeGateway));
    }

    internal bool TryHandle(object service, object request, bool isHeadRequest, out Task<object>? result)
    {
        result = null;
        string? issuedTicket = null;
        try
        {
            var httpRequest = GetProperty(service, "Request") as IRequest;
            if (httpRequest is null) return false;
            var expectedMethod = isHeadRequest ? "HEAD" : "GET";
            if (!string.Equals(httpRequest.HttpMethod, expectedMethod, StringComparison.OrdinalIgnoreCase))
                return false;
            if (GetProperty(request, "Static") is not bool isStatic || !isStatic) return false;

            var options = runtime.GetOptionsSnapshot();
            if (!options.Enabled || options.PlaybackMode == PlaybackRoutingMode.Native ||
                options.IncludedLibraryIds.Length == 0 || runtime.SourcePolicy is null)
                return false;

            var requestedItem = ResolveItem(GetStringProperty(request, "Id"));
            var mediaSourceId = GetStringProperty(request, "MediaSourceId")?.Trim();
            if (requestedItem is null) return false;
            if (string.IsNullOrEmpty(mediaSourceId))
            {
                LogSkipped(requestedItem, "media-source-id");
                return false;
            }
            if (!IsIncludedLibraryItem(requestedItem, options.IncludedLibraryIds))
            {
                LogSkipped(requestedItem, "library-scope");
                return false;
            }
            if (!TryResolveSource(requestedItem, mediaSourceId, options.IncludedLibraryIds,
                    out var sourceItem, out var mediaSource, out var source))
            {
                LogSkipped(requestedItem, "media-source-match");
                return false;
            }
            var matchedItem = sourceItem!;
            var matchedMediaSource = mediaSource!;
            var matchedSource = source!;

            var operation = runtime.BeginOperation();
            var userId = resolveUserId(httpRequest);
            var deviceId = resolveDeviceId(httpRequest);
            if (!runtime.TryCommit(
                    operation.Generation,
                    () => true,
                    () => issuedTicket = runtime.Tickets.IssuePlayback(
                        matchedItem.Id,
                        matchedMediaSource.Id ?? mediaSourceId,
                        userId,
                        matchedSource,
                        PlaybackTicketPurpose.DirectClient,
                        operation.Generation,
                        TicketStore.ComputePlaybackLifetime(
                            matchedMediaSource.RunTimeTicks ?? matchedItem.RunTimeTicks),
                        deviceId)))
                return false;

            var container = string.IsNullOrWhiteSpace(matchedMediaSource.Container)
                ? Path.GetExtension(matchedSource.SourceUri.AbsolutePath).TrimStart('.')
                : matchedMediaSource.Container;
            result = invokeGateway(
                httpRequest,
                issuedTicket!,
                GatewayRouteBuilder.CreatePlaybackFileName(container),
                isHeadRequest);
            if (result is null) throw new InvalidOperationException("The gateway returned no task.");
            logger.Debug("STRM_BRIDGE_NATIVE_STREAM_ROUTED item=" + ShortId(matchedItem.Id));
            return true;
        }
        catch (TicketCapacityException)
        {
            if (issuedTicket is not null) runtime.Tickets.Revoke(issuedTicket);
            logger.Warn("STRM_BRIDGE_NATIVE_STREAM_CAPACITY");
            return false;
        }
        catch (Exception exception)
        {
            if (issuedTicket is not null) runtime.Tickets.Revoke(issuedTicket);
            logger.Debug("STRM_BRIDGE_NATIVE_STREAM_SKIPPED error=" + exception.GetType().Name);
            return false;
        }
    }

    private bool TryResolveSource(
        BaseItem requestedItem,
        string mediaSourceId,
        string[] includedLibraryIds,
        out BaseItem? sourceItem,
        out MediaSourceInfo? mediaSource,
        out SourceIdentity? source)
    {
        sourceItem = null;
        mediaSource = null;
        source = null;
        List<MediaSourceInfo> candidates;
        try
        {
            candidates = mediaSourceManager.GetStaticMediaSources(
                requestedItem,
                enablePathSubstitution: false,
                fillChapters: false,
                deviceProfile: null,
                user: null);
        }
        catch
        {
            return false;
        }

        foreach (var candidate in candidates.Where(candidate =>
                     string.Equals(candidate.Id, mediaSourceId, StringComparison.Ordinal)))
        {
            var candidateItem = ResolveItem(candidate.ItemId) ?? requestedItem;
            if (!IsIncludedStrmItem(candidateItem, includedLibraryIds)) continue;
            SourceIdentity candidateSource;
            try { candidateSource = runtime.SourcePolicy!.Read(candidateItem.Path); }
            catch (SourcePolicyException) { continue; }
            if (!StaticMediaSourcePolicy.Matches(candidate, candidateSource)) continue;
            if (mediaSource is not null) return false;
            sourceItem = candidateItem;
            mediaSource = candidate;
            source = candidateSource;
        }
        return mediaSource is not null;
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

    private bool IsIncludedStrmItem(BaseItem item, string[] includedLibraryIds) =>
        !string.IsNullOrWhiteSpace(item.Path) &&
        string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase) &&
        IsIncludedLibraryItem(item, includedLibraryIds);

    private bool IsIncludedLibraryItem(BaseItem item, string[] includedLibraryIds)
    {
        var allowed = new HashSet<string>(includedLibraryIds, StringComparer.OrdinalIgnoreCase);
        return libraryManager.GetCollectionFolders(item).Any(folder =>
            allowed.Contains(folder.Id.ToString("N")) || allowed.Contains(folder.Id.ToString()));
    }

    private void LogSkipped(BaseItem item, string reason)
    {
        if (!string.IsNullOrWhiteSpace(item.Path) &&
            string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase))
            logger.Debug("STRM_BRIDGE_NATIVE_STREAM_SKIPPED reason=" + reason + " item=" + ShortId(item.Id));
    }

    private static string? ResolveUserId(IAuthorizationContext authorizationContext, IRequest request)
    {
        if (authorizationContext is null) throw new ArgumentNullException(nameof(authorizationContext));
        var user = authorizationContext.GetAuthorizationInfo(request)?.User;
        if (user is not null && !user.Policy.EnableMediaPlayback) throw new UnauthorizedAccessException();
        return user?.Id.ToString("N");
    }

    private static string? ResolveDeviceId(IAuthorizationContext authorizationContext, IRequest request)
    {
        if (authorizationContext is null) throw new ArgumentNullException(nameof(authorizationContext));
        return authorizationContext.GetAuthorizationInfo(request)?.ReportedDeviceId;
    }

    private static Task<object> InvokeGateway(
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext,
        IHttpResultFactory resultFactory,
        ILogManager logManager,
        IRequest request,
        string ticket,
        string fileName,
        bool isHead)
    {
        var service = new GatewayService(libraryManager, authorizationContext, resultFactory, logManager)
        {
            Request = request,
        };
        var gatewayRequest = new GetStrmBridgePlayback { Ticket = ticket, FileName = fileName };
        return isHead ? service.Head(gatewayRequest) : service.Get(gatewayRequest);
    }

    private static object? GetProperty(object value, string name) =>
        value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(value);

    private static string? GetStringProperty(object value, string name) => GetProperty(value, name)?.ToString();

    private static string ShortId(Guid itemId) => itemId.ToString("N").Substring(0, 8);
}
