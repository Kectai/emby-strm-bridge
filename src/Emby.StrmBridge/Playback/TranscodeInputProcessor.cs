using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Policy;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Playback;

internal sealed class TranscodeInputProcessor
{
    private readonly PluginRuntime runtime;
    private readonly ILibraryManager libraryManager;
    private readonly IMediaSourceManager mediaSourceManager;
    private readonly ILogger logger;
    private readonly Func<string?> localApiUrlProvider;

    public TranscodeInputProcessor(
        PluginRuntime runtime,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IServerApplicationHost applicationHost,
        ILogManager logManager)
        : this(
            runtime,
            libraryManager,
            mediaSourceManager,
            (logManager ?? throw new ArgumentNullException(nameof(logManager)))
                .GetLogger(Plugin.Instance?.Name ?? "STRM Bridge"),
            () => (applicationHost ?? throw new ArgumentNullException(nameof(applicationHost)))
                .GetLocalApiUrl(IPAddress.Loopback))
    {
    }

    internal TranscodeInputProcessor(
        PluginRuntime runtime,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ILogger logger,
        Func<string?> localApiUrlProvider)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        this.mediaSourceManager = mediaSourceManager ?? throw new ArgumentNullException(nameof(mediaSourceManager));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.localApiUrlProvider = localApiUrlProvider ??
            throw new ArgumentNullException(nameof(localApiUrlProvider));
    }

    internal bool TryRoute(object service, object state)
    {
        if (service is null || state is null) return false;
        string? issuedTicket = null;
        object? originalStateMediaSource = null;
        object? originalMediaPath = null;
        object? originalDirectMediaPath = null;
        object? originalMediaProtocol = null;
        object? originalDirectMediaProtocol = null;
        var mutated = false;
        try
        {
            var options = runtime.GetOptionsSnapshot();
            if (!options.Enabled || options.PlaybackMode == PlaybackRoutingMode.Native ||
                options.IncludedLibraryIds.Length == 0 || runtime.SourcePolicy is null)
                return false;

            if (GetProperty(state, "MediaSource") is not MediaSourceInfo currentMediaSource)
                return false;
            var request = GetProperty(state, "BaseRequest") ?? GetProperty(state, "Request");
            var mediaSourceId = GetStringProperty(request, "MediaSourceId")?.Trim();
            if (string.IsNullOrEmpty(mediaSourceId) ||
                !string.Equals(mediaSourceId, currentMediaSource.Id, StringComparison.Ordinal))
                return false;

            var requestedItem = ResolveItem(GetStringProperty(request, "Id"));
            var sourceItem = ResolveItem(currentMediaSource.ItemId) ?? requestedItem;
            if (requestedItem is null || sourceItem is null ||
                !IsIncludedLibraryItem(requestedItem, options.IncludedLibraryIds) ||
                !IsIncludedStrmItem(sourceItem, options.IncludedLibraryIds))
                return false;

            SourceIdentity source;
            try { source = runtime.SourcePolicy.Read(sourceItem.Path); }
            catch (SourcePolicyException) { return false; }
            if (!StaticMediaSourcePolicy.Matches(mediaSourceManager, sourceItem, source) ||
                !StaticMediaSourcePolicy.MatchesPlaybackSource(currentMediaSource, source))
                return false;

            string? localApiUrl;
            try { localApiUrl = localApiUrlProvider(); }
            catch (Exception exception)
            {
                logger.Debug("STRM_BRIDGE_TRANSCODE_INPUT_SKIPPED error=" + exception.GetType().Name);
                return false;
            }

            var operation = runtime.BeginOperation();
            var userId = GetUserId(GetProperty(state, "AuthorizationInfo"));
            var container = string.IsNullOrWhiteSpace(currentMediaSource.Container)
                ? Path.GetExtension(source.SourceUri.AbsolutePath).TrimStart('.')
                : currentMediaSource.Container;
            if (!runtime.TryCommit(
                    operation.Generation,
                    () => true,
                    () =>
                    {
                        issuedTicket = runtime.Tickets.IssuePlayback(
                            sourceItem.Id,
                            currentMediaSource.Id ?? mediaSourceId,
                            userId,
                            source,
                            operation.Generation,
                            TicketStore.ComputePlaybackLifetime(
                                currentMediaSource.RunTimeTicks ?? sourceItem.RunTimeTicks));
                        var route = GatewayRouteBuilder.CreateInternalPlaybackRoute(
                            localApiUrl,
                            GatewayRouteBuilder.GetApiPathBase(GetProperty(service, "Request") as IRequest),
                            issuedTicket,
                            container);
                        if (route is null)
                            throw new InvalidOperationException("The local gateway origin is unavailable.");

                        originalStateMediaSource = currentMediaSource;
                        originalMediaPath = GetProperty(state, "MediaPath");
                        originalDirectMediaPath = GetProperty(state, "DirectMediaPath");
                        originalMediaProtocol = GetProperty(state, "MediaProtocol");
                        originalDirectMediaProtocol = GetProperty(state, "DirectMediaProtocol");
                        mutated = true;

                        var routedMediaSource = new MediaSourceInfo(currentMediaSource)
                        {
                            Path = route,
                            ProbePath = route,
                            Protocol = MediaProtocol.Http,
                            ProbeProtocol = MediaProtocol.Http,
                        };
                        SetProperty(state, "MediaSource", routedMediaSource);
                        SetProperty(state, "MediaPath", route);
                        SetProperty(state, "DirectMediaPath", route);
                        SetProperty(state, "MediaProtocol", MediaProtocol.Http);
                        SetProperty(state, "DirectMediaProtocol", MediaProtocol.Http);
                    }))
                return false;

            logger.Debug("STRM_BRIDGE_TRANSCODE_INPUT_ROUTED item=" + ShortId(sourceItem.Id));
            return true;
        }
        catch (TicketCapacityException)
        {
            RestoreState(
                state,
                mutated,
                originalStateMediaSource,
                originalMediaPath,
                originalDirectMediaPath,
                originalMediaProtocol,
                originalDirectMediaProtocol);
            if (issuedTicket is not null) runtime.Tickets.Revoke(issuedTicket);
            logger.Warn("STRM_BRIDGE_TRANSCODE_INPUT_CAPACITY");
            return false;
        }
        catch (Exception exception)
        {
            RestoreState(
                state,
                mutated,
                originalStateMediaSource,
                originalMediaPath,
                originalDirectMediaPath,
                originalMediaProtocol,
                originalDirectMediaProtocol);
            if (issuedTicket is not null) runtime.Tickets.Revoke(issuedTicket);
            logger.Debug("STRM_BRIDGE_TRANSCODE_INPUT_SKIPPED error=" + exception.GetType().Name);
            return false;
        }
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

    private static string? GetUserId(object? authorizationInfo)
    {
        var id = GetProperty(GetProperty(authorizationInfo, "User"), "Id");
        return id is Guid guid ? guid.ToString("N") : id?.ToString();
    }

    private static void RestoreState(
        object state,
        bool mutated,
        object? mediaSource,
        object? mediaPath,
        object? directMediaPath,
        object? mediaProtocol,
        object? directMediaProtocol)
    {
        if (!mutated) return;
        TrySetProperty(state, "MediaSource", mediaSource);
        TrySetProperty(state, "MediaPath", mediaPath);
        TrySetProperty(state, "DirectMediaPath", directMediaPath);
        TrySetProperty(state, "MediaProtocol", mediaProtocol);
        TrySetProperty(state, "DirectMediaProtocol", directMediaProtocol);
    }

    private static object? GetProperty(object? value, string name) => value?.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?.GetValue(value);

    private static string? GetStringProperty(object? value, string name) =>
        GetProperty(value, name)?.ToString();

    private static void SetProperty(object value, string name, object? propertyValue)
    {
        var property = value.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.SetMethod is null) throw new MissingMemberException(value.GetType().FullName, name);
        property.SetValue(value, propertyValue);
    }

    private static void TrySetProperty(object value, string name, object? propertyValue)
    {
        try { SetProperty(value, name, propertyValue); }
        catch { }
    }

    private static string ShortId(Guid itemId) => itemId.ToString("N").Substring(0, 8);
}
