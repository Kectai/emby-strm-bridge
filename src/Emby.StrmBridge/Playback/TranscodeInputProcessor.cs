using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Policy;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
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
        Jobs = new TranscodeJobCoordinator(runtime.Clock, logger);
    }

    internal TranscodeJobCoordinator Jobs { get; }

    internal bool TryRoute(object service, object state) => TryRoute(service, state, out _, out _, out _);

    private bool TryRoute(
        object service,
        object state,
        out string? retryKey,
        out TranscodeInputResources? resources,
        out Action? rollbackStart)
    {
        retryKey = null;
        resources = null;
        rollbackStart = null;
        if (service is null || state is null) return false;
        string? issuedTicket = null;
        TranscodeInputResources? issuedResources = null;
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
                !PlaybackItemPolicy.IsVideo(requestedItem) ||
                !PlaybackItemPolicy.IsVideo(sourceItem) ||
                !IsIncludedLibraryItem(requestedItem, options.IncludedLibraryIds) ||
                !IsIncludedStrmItem(sourceItem, options.IncludedLibraryIds))
                return false;

            SourceIdentity source;
            try { source = runtime.SourcePolicy.Read(sourceItem.Path); }
            catch (SourcePolicyException) { return false; }
            if (!StaticMediaSourcePolicy.Matches(mediaSourceManager, sourceItem, source))
                return false;

            string? localApiUrl;
            try { localApiUrl = localApiUrlProvider(); }
            catch (Exception exception)
            {
                logger.Debug("STRM_BRIDGE_TRANSCODE_INPUT_SKIPPED error=" + exception.GetType().Name);
                return false;
            }

            var operation = runtime.BeginOperation();
            var userId = GetUserId(state);
            var startTimeTicks = GetLongProperty(request, "StartTimeTicks");
            var serviceRequest = GetProperty(service, "Request") as IRequest;
            var container = string.IsNullOrWhiteSpace(currentMediaSource.Container)
                ? Path.GetExtension(source.SourceUri.AbsolutePath).TrimStart('.')
                : currentMediaSource.Container;
            if (!StaticMediaSourcePolicy.MatchesPlaybackSource(currentMediaSource, source) &&
                !MatchesRoutedInput(currentMediaSource, source, sourceItem.Id, mediaSourceId, userId,
                    operation.Generation, localApiUrl, serviceRequest, container))
                return false;
            if (options.EnableFastSeek && options.PlaybackMode == PlaybackRoutingMode.Adaptive &&
                startTimeTicks >= FastSeekCoordinator.MinimumTarget.Ticks &&
                IsTransportStreamCandidate(currentMediaSource, source))
            {
                retryKey = CreateRetryKey(source, currentMediaSource, request, userId, startTimeTicks.Value,
                    GetProperty(state, "VideoStream") as MediaStream);
                Jobs.CheckRetry(retryKey, operation.Generation);
            }
            var fastSeekDurationTicks = TryPrepareFastSeek(
                source,
                sourceItem,
                currentMediaSource,
                mediaSourceId,
                startTimeTicks,
                options,
                operation,
                serviceRequest,
                GetProperty(state, "VideoStream") as MediaStream,
                out var videoSelection);
            if (!runtime.IsOperationCurrent(operation.Generation)) return false;
            issuedTicket = runtime.Tickets.IssuePlayback(
                sourceItem.Id,
                currentMediaSource.Id ?? mediaSourceId,
                userId,
                source,
                PlaybackTicketPurpose.ServerFfmpeg,
                operation.Generation,
                TicketStore.ComputePlaybackLifetime(
                    currentMediaSource.RunTimeTicks ?? sourceItem.RunTimeTicks));
            var route = GatewayRouteBuilder.CreateInternalPlaybackRoute(
                localApiUrl,
                GatewayRouteBuilder.GetApiPathBase(serviceRequest),
                issuedTicket,
                container);
            if (route is null)
                throw new InvalidOperationException("The local gateway origin is unavailable.");
            if (!runtime.Tickets.TryInspect(issuedTicket, out var issuedPayload) || issuedPayload is null)
                throw new InvalidOperationException("The transcode input ticket is unavailable.");
            issuedResources = new TranscodeInputResources(
                runtime,
                logger,
                route,
                issuedTicket,
                issuedPayload);
            if (serviceRequest?.CancellationToken.IsCancellationRequested == true)
            {
                issuedResources.Release();
                return false;
            }
            if (!runtime.TryCommit(
                    operation.Generation,
                    () => true,
                    () =>
                    {
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
                        if (startTimeTicks.HasValue && fastSeekDurationTicks.HasValue)
                            runtime.FastSeek?.TryBindInput(
                                source,
                                currentMediaSource.Id ?? mediaSourceId,
                                startTimeTicks.Value,
                                fastSeekDurationTicks.Value,
                                operation.Generation,
                                route,
                                options,
                                videoSelection,
                                issuedTicket);
                    }))
            {
                issuedResources.Release();
                return false;
            }

            resources = issuedResources;
            rollbackStart = () => RestoreState(
                state,
                mutated,
                originalStateMediaSource,
                originalMediaPath,
                originalDirectMediaPath,
                originalMediaProtocol,
                originalDirectMediaProtocol);
            logger.Debug("STRM_BRIDGE_TRANSCODE_INPUT_ROUTED item=" + ShortId(sourceItem.Id));
            return true;
        }
        catch (TranscodeStartRejectedException) { throw; }
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
            if (issuedResources is not null) issuedResources.Release();
            else if (issuedTicket is not null) runtime.Tickets.Revoke(issuedTicket);
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
            if (issuedResources is not null) issuedResources.Release();
            else if (issuedTicket is not null) runtime.Tickets.Revoke(issuedTicket);
            logger.Debug("STRM_BRIDGE_TRANSCODE_INPUT_SKIPPED error=" + exception.GetType().Name);
            return false;
        }
    }

    internal TranscodeJobCoordinator.Attempt? BeforeStart(object service, object state, string outputPath)
    {
        var startedAt = runtime.Clock.UtcNow;
        if (!TryRoute(service, state, out var retryKey, out var resources, out var rollbackStart))
        {
            Jobs.ObserveUnmanagedStart(outputPath);
            return null;
        }
        try
        {
            var source = (MediaSourceInfo)GetProperty(state, "MediaSource")!;
            return Jobs.Register(
                source,
                outputPath,
                retryKey,
                runtime.Generation,
                startedAt,
                resources!.Release,
                rollbackStart);
        }
        catch
        {
            try { rollbackStart?.Invoke(); }
            catch (Exception exception)
            {
                logger.Warn("STRM_BRIDGE_TRANSCODE_START_ROLLBACK_FAILED error=" + exception.GetType().Name);
            }
            resources!.Release();
            throw;
        }
    }

    internal static void SetRetryAfter(object service, int seconds)
    {
        if (GetProperty(service, "Request") is not IRequest request) return;
        request.Response?.AddHeader("Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
    }

    internal bool TryCleanup(object manager, object job, int retryCount, int delayMilliseconds, out Task? task)
    {
        task = null;
        if (GetProperty(job, "MediaSource") is not MediaSourceInfo source ||
            GetProperty(job, "Path") is not string path || !Jobs.TryGetOwner(source, path, out var attempt))
            return false;
        var type = GetProperty(job, "Type");
        var activeMethod = type is null ? null : manager.GetType().GetMethod("GetTranscodingJob",
            new[] { typeof(string), type.GetType() });
        var fileSystem = manager.GetType().GetField("_fileSystem",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as IFileSystem;
        if (fileSystem is null || activeMethod is null) return false;
        task = Jobs.CleanupAsync(attempt!, retryCount, delayMilliseconds,
            directory => fileSystem.DeleteDirectory(directory, true),
            output => activeMethod.Invoke(manager, new[] { output, type }) is not null);
        return true;
    }

    private static string? CreateRetryKey(SourceIdentity source, MediaSourceInfo mediaSource,
        object? request, string? userId, long targetTicks, MediaStream? selectedVideo)
    {
        var session = GetStringProperty(request, "PlaySessionId");
        var device = GetStringProperty(request, "DeviceId");
        if (string.IsNullOrWhiteSpace(session) && string.IsNullOrWhiteSpace(device)) return null;
        var parts = new[]
        {
            source.SourceFingerprint, mediaSource.Id, userId, device, session,
            source.LocalFileLength.ToString(CultureInfo.InvariantCulture),
            source.LocalLastWriteUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            selectedVideo?.Index.ToString(CultureInfo.InvariantCulture) ?? GetStringProperty(request, "VideoStreamIndex"),
            mediaSource.RunTimeTicks?.ToString(CultureInfo.InvariantCulture),
            (targetTicks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture),
        };
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Concat(
            parts.Select(value => (value?.Length ?? 0).ToString(CultureInfo.InvariantCulture) + ":" + value)))));
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

    private bool MatchesRoutedInput(MediaSourceInfo mediaSource, SourceIdentity source, Guid itemId,
        string mediaSourceId, string? userId, int generation, string? localApiUrl, IRequest? request, string? container)
    {
        if (mediaSource.RequiresOpening || !string.IsNullOrEmpty(mediaSource.OpenToken) ||
            mediaSource.RequiredHttpHeaders?.Count > 0 ||
            string.IsNullOrEmpty(mediaSource.Path) || mediaSource.Path.Length > 2048 ||
            !string.Equals(mediaSource.Path, mediaSource.ProbePath, StringComparison.Ordinal))
            return false;
        var segments = mediaSource.Path.Split('/');
        if (segments.Length < 2) return false;
        var ticket = segments[^2];
        var expected = GatewayRouteBuilder.CreateInternalPlaybackRoute(localApiUrl,
            GatewayRouteBuilder.GetApiPathBase(request), ticket, container);
        return string.Equals(mediaSource.Path, expected, StringComparison.Ordinal) &&
               runtime.Tickets.MatchesTranscodeInput(ticket, itemId, mediaSourceId, userId, source, generation);
    }

    private static string? GetUserId(object state)
    {
        var user = GetProperty(state, "User") ?? GetProperty(GetProperty(state, "AuthorizationInfo"), "User");
        var id = GetProperty(user, "Id");
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

    private static long? GetLongProperty(object? value, string name)
    {
        var property = GetProperty(value, name);
        if (property is long number) return number;
        return long.TryParse(property?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private long? TryPrepareFastSeek(
        SourceIdentity source,
        BaseItem sourceItem,
        MediaSourceInfo mediaSource,
        string mediaSourceId,
        long? startTimeTicks,
        PluginConfiguration options,
        OperationContext operation,
        IRequest? serviceRequest,
        MediaStream? selectedVideo,
        out FastSeekVideoSelection? videoSelection)
    {
        videoSelection = null;
        var fastSeek = runtime.FastSeek;
        var durationTicks = mediaSource.RunTimeTicks ?? sourceItem.RunTimeTicks;
        if (fastSeek is null || !options.EnableFastSeek ||
            options.PlaybackMode != PlaybackRoutingMode.Adaptive ||
            !startTimeTicks.HasValue || startTimeTicks.Value < FastSeekCoordinator.MinimumTarget.Ticks ||
            !durationTicks.HasValue || durationTicks.Value <= startTimeTicks.Value ||
            !IsTransportStreamCandidate(mediaSource, source))
            return null;
        if (!FastSeekVideoSelection.TryCreate(mediaSource, selectedVideo, out videoSelection))
        {
            logger.Debug("STRM_BRIDGE_FAST_SEEK_SKIPPED reason=streamselection");
            return null;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operation.CancellationToken,
            serviceRequest?.CancellationToken ?? CancellationToken.None);
        var prepared = fastSeek.PrepareAsync(
                source,
                mediaSource.Id ?? mediaSourceId,
                startTimeTicks.Value,
                durationTicks.Value,
                operation.Generation,
                options,
                cancellation.Token,
                videoSelection)
            .GetAwaiter()
            .GetResult();
        return prepared ? durationTicks.Value : null;
    }

    private static bool IsTransportStreamCandidate(MediaSourceInfo mediaSource, SourceIdentity source)
    {
        var containers = (mediaSource.Container ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim());
        if (containers.Any(value =>
                string.Equals(value, "mpegts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "mpegtsraw", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "ts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "m2ts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "mts", StringComparison.OrdinalIgnoreCase)))
            return true;
        var extension = Path.GetExtension(source.SourceUri.AbsolutePath);
        return string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".m2ts", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".mts", StringComparison.OrdinalIgnoreCase);
    }

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

    private sealed class TranscodeInputResources
    {
        private readonly PluginRuntime runtime;
        private readonly ILogger logger;
        private readonly string inputUrl;
        private readonly string ticket;
        private readonly TicketPayload payload;
        private int released;

        public TranscodeInputResources(
            PluginRuntime runtime,
            ILogger logger,
            string inputUrl,
            string ticket,
            TicketPayload payload)
        {
            this.runtime = runtime;
            this.logger = logger;
            this.inputUrl = inputUrl;
            this.ticket = ticket;
            this.payload = payload;
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            try { runtime.FastSeek?.DisableInput(inputUrl, ticket, payload); }
            catch (Exception exception)
            {
                logger.Warn("STRM_BRIDGE_FAST_SEEK_RELEASE_FAILED error=" + exception.GetType().Name);
            }
            try { runtime.Tickets.Revoke(ticket); }
            catch (Exception exception)
            {
                logger.Warn("STRM_BRIDGE_TRANSCODE_TICKET_REVOKE_FAILED error=" + exception.GetType().Name);
            }
        }
    }
}
