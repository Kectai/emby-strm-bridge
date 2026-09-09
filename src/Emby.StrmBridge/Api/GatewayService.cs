using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Api;

public sealed class GatewayService : IService, IRequiresRequest
{
    internal static readonly TimeSpan ServerFfmpegRedirectLifetime = TimeSpan.FromSeconds(20);
    private static readonly string[] ForwardedRequestHeaders =
    {
        "Range", "If-Range", "If-None-Match", "If-Modified-Since", "Accept", "Accept-Language",
        "Cache-Control", "Pragma",
    };
    private readonly ILibraryManager libraryManager;
    private readonly IAuthorizationContext authorizationContext;
    private readonly IHttpResultFactory resultFactory;
    private readonly ILogger logger;
    private readonly PluginRuntime? runtimeOverride;

    public GatewayService(
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext,
        IHttpResultFactory resultFactory,
        ILogManager logManager)
    {
        this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        this.authorizationContext = authorizationContext ?? throw new ArgumentNullException(nameof(authorizationContext));
        this.resultFactory = resultFactory ?? throw new ArgumentNullException(nameof(resultFactory));
        logger = (logManager ?? throw new ArgumentNullException(nameof(logManager)))
            .GetLogger(Plugin.Instance?.Name ?? "STRM Bridge");
    }

    public IRequest Request { get; set; } = null!;

    internal GatewayService(ILibraryManager libraryManager, IAuthorizationContext authorizationContext,
        IHttpResultFactory resultFactory, ILogManager logManager, PluginRuntime runtime)
        : this(libraryManager, authorizationContext, resultFactory, logManager) => runtimeOverride = runtime;

    public Task<object> Get(GetStrmBridgePlayback request) =>
        ResolveAsync(request.Ticket, null, request.FileName);

    public Task<object> Head(GetStrmBridgePlayback request) =>
        ResolveAsync(request.Ticket, null, request.FileName);

    public Task<object> Get(GetStrmBridgeHlsResource request) =>
        ResolveAsync(request.Ticket, request.ParentTicket, request.FileName);

    public Task<object> Head(GetStrmBridgeHlsResource request) =>
        ResolveAsync(request.Ticket, request.ParentTicket, request.FileName);

    private async Task<object> ResolveAsync(string ticketValue, string? parentTicketValue, string fileName)
    {
        if (!IsValidFileName(fileName)) throw Unavailable();
        var runtime = runtimeOverride ?? Plugin.Runtime;
        if (runtime?.SourcePolicy is null || runtime.Gateway is null)
            throw Unavailable();
        var operation = runtime.BeginOperation();
        var options = runtime.GetOptionsSnapshot();
        if (!options.Enabled || !runtime.Tickets.TryInspect(ticketValue, out var inspected))
            throw Unavailable();
        var isProbe = inspected!.Purpose == PlaybackTicketPurpose.ExtractionProbe;
        TicketPayload? probeRoot = isProbe ? inspected : null;
        if (options.PlaybackMode == PlaybackRoutingMode.Native && !isProbe) throw Unavailable();
        if (isProbe) options.PlaybackMode = PlaybackRoutingMode.RelayOnly;

        if (parentTicketValue is not null)
        {
            if (!runtime.Tickets.TryInspect(parentTicketValue, out var parent) ||
                parent!.Scope != TicketScope.Playback || inspected!.Scope != TicketScope.HlsResource ||
                !runtime.Tickets.IsHlsResourceOfRoot(parentTicketValue, ticketValue) ||
                parent.ItemId != inspected.ItemId ||
                !string.Equals(parent.MediaSourceId, inspected.MediaSourceId, StringComparison.Ordinal))
                throw Unavailable();
            if (parent.Purpose == PlaybackTicketPurpose.ExtractionProbe) probeRoot = parent;
        }
        if (parentTicketValue is null && inspected!.Scope != TicketScope.Playback) throw Unavailable();
        if (inspected.Purpose != PlaybackTicketPurpose.DirectClient && !IsLoopbackRequest(Request.RemoteIp))
        {
            runtime.Tickets.Revoke(ticketValue);
            throw Unavailable();
        }
        MarkProbeRequestObserved(probeRoot);

        AuthorizationInfo? authorization = null;
        try { authorization = authorizationContext.GetAuthorizationInfo(Request); }
        catch (UnauthorizedAccessException) { }
        catch (Exception exception)
        {
            MarkProbeLocalFailure(probeRoot);
            logger.Debug("STRM_BRIDGE_GATEWAY_AUTH_FAILED error=" + exception.GetType().Name);
            throw Unavailable();
        }
        var user = authorization?.User;
        if (user is not null && !user.Policy.EnableMediaPlayback)
        {
            MarkProbeLocalFailure(probeRoot);
            throw Unavailable();
        }
        var authenticatedUserId = user?.Id.ToString("N");
        if (!runtime.Tickets.TryRedeem(ticketValue, authenticatedUserId, out var ticket))
        {
            MarkProbeLocalFailure(probeRoot);
            throw Unavailable();
        }
        if (ticket!.RuntimeGeneration != operation.Generation ||
            !runtime.IsOperationCurrent(operation.Generation))
        {
            if (!operation.CancellationToken.IsCancellationRequested) MarkProbeLocalFailure(probeRoot);
            runtime.Tickets.Revoke(ticketValue);
            throw Unavailable();
        }

        var item = libraryManager.GetItemById(ticket.ItemId);
        if (!IsIncludedItem(item, options.IncludedLibraryIds, isProbe) || (user is not null && !item!.IsVisible(user)))
        {
            MarkProbeLocalFailure(probeRoot);
            throw Unavailable();
        }
        SourceIdentity currentSource;
        try { currentSource = runtime.SourcePolicy.Read(item!.Path); }
        catch (SourcePolicyException)
        {
            MarkProbeLocalFailure(probeRoot);
            throw Unavailable();
        }
        if (!ticket.Source.HasSameFileVersion(currentSource))
        {
            MarkProbeLocalFailure(probeRoot);
            runtime.Tickets.Revoke(ticketValue);
            throw Unavailable();
        }

        var forwardedHeaders = GetForwardedRequestHeaders();
        var forceFreshDirectRedirect = RequestsFreshRedirect(forwardedHeaders);
        var requestUserAgent = GetRequestUserAgent();
        var directRouteScope = ticket.Purpose == PlaybackTicketPurpose.DirectClient &&
                               options.PlaybackMode is PlaybackRoutingMode.Adaptive or PlaybackRoutingMode.RedirectOnly
            ? GatewayTransport.CreateDirectRouteScope(ticket, ticketValue)
            : null;
        var sourceRedirectHandoff = ticket.Purpose == PlaybackTicketPurpose.DirectClient &&
                                    (options.PlaybackMode == PlaybackRoutingMode.RedirectOnly ||
                                     options.PlaybackMode == PlaybackRoutingMode.Adaptive &&
                                     ticket.SourceRedirectHandoffAllowed);
        GatewayTransport.DirectRouteGateLease? directRouteGate = null;
        GatewayTransportLease? lease = null;
        CancellationTokenSource? operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            Request.CancellationToken, operation.CancellationToken, inspected.ProbeCancellation);
        var operationToken = operationCancellation.Token;
        using var headerBudget = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        headerBudget.CancelAfter(TimeSpan.FromSeconds(options.GatewayTimeoutSeconds));
        var expectedRepresentation = options.EnableFastSeek &&
            ticket.Purpose == PlaybackTicketPurpose.ServerFfmpeg &&
            runtime.FastSeek?.TryGetInputRepresentation(ticket, operation.Generation, out var expected) == true
            ? expected
            : null;
        if (expectedRepresentation is not null)
            forwardedHeaders["If-Match"] = expectedRepresentation.StrongETag;

        Task<GatewayTransportLease> OpenLeaseAsync(string method, IReadOnlyDictionary<string, string> headers) =>
            sourceRedirectHandoff
                ? runtime.Gateway.OpenSourceRedirectHandoffAsync(
                    ticket.UpstreamUri, method, requestUserAgent, headers, options, operationToken,
                    headerBudget.Token)
                : directRouteScope is not null && directRouteGate is not null
                ? runtime.Gateway.OpenDirectAsync(
                    ticket.UpstreamUri, directRouteScope, directRouteGate.Generation, method,
                    requestUserAgent, headers, options, operationToken, headerBudget.Token)
                : isProbe
                    ? runtime.Gateway.OpenProbeAsync(
                        ticket.UpstreamUri, ticketValue, null, method, requestUserAgent,
                        headers, options, operationToken, headerBudget.Token)
                    : runtime.Gateway.OpenAsync(
                        ticket.UpstreamUri, ticketValue,
                        ticket.Purpose == PlaybackTicketPurpose.ServerFfmpeg
                            ? GatewayTransport.CreateRedirectCandidateScope(ticket.Source) : null,
                        method, requestUserAgent, headers, options, operationToken, headerBudget.Token);
        try
        {
            if (directRouteScope is not null)
            {
                directRouteGate = await runtime.Gateway.AcquireDirectRouteGateAsync(
                        directRouteScope,
                        Request.HttpMethod,
                        requestUserAgent,
                        forwardedHeaders,
                        headerBudget.Token)
                    .ConfigureAwait(false);
                if (!runtime.IsOperationCurrent(operation.Generation)) throw Unavailable();
                if (runtime.Gateway.TryGetDirectRoute(
                        ticket.UpstreamUri,
                        directRouteScope,
                        Request.HttpMethod,
                        requestUserAgent,
                        forwardedHeaders,
                        out var rememberedRoute))
                {
                    if (options.PlaybackMode == PlaybackRoutingMode.RedirectOnly ||
                        rememberedRoute.Behavior == SourceTransportBehavior.FileBody)
                    {
                        if (rememberedRoute.ResolveSourceRedirect || forceFreshDirectRedirect)
                        {
                            sourceRedirectHandoff = true;
                            if (forceFreshDirectRedirect && !rememberedRoute.ResolveSourceRedirect)
                                logger.Debug("STRM_BRIDGE_GATEWAY_DIRECT_ROUTE_REFRESH item=" + ShortId(ticket.ItemId));
                        }
                        else
                        {
                            if (!runtime.TryCommit(
                                    operation.Generation,
                                    () => true,
                                    () =>
                                    {
                                        headerBudget.Token.ThrowIfCancellationRequested();
                                        WriteRedirectResponse(
                                            ticket,
                                            rememberedRoute.EffectiveUri!,
                                            runtime.Clock.UtcNow,
                                            rememberedRoute.ExpiresAtUtc,
                                            options.DirectRedirectCacheSeconds);
                                    }))
                                throw Unavailable();
                            logger.Debug("STRM_BRIDGE_GATEWAY_DIRECT_ROUTE_HIT item=" + ShortId(ticket.ItemId));
                            return string.Empty;
                        }
                    }
                }
            }

            var canRefreshClassifiedFile = directRouteScope is not null && directRouteGate is not null &&
                                           options.PlaybackMode == PlaybackRoutingMode.Adaptive &&
                                           !sourceRedirectHandoff;
            int? classifiedSourceRedirectGeneration = null;
            var forceRelayCurrentResponse = false;
            lease = await OpenLeaseAsync(Request.HttpMethod, forwardedHeaders).ConfigureAwait(false);
            while (true)
            {
                if (!runtime.IsOperationCurrent(operation.Generation)) throw Unavailable();
                if (lease.IsRedirectHandoff)
                {
                    if (options.PlaybackMode == PlaybackRoutingMode.Adaptive && lease.IsHlsManifest)
                    {
                        if (directRouteScope is not null && directRouteGate is not null)
                            runtime.Gateway.ForgetDirectRouteState(
                                directRouteScope,
                                Request.HttpMethod,
                                requestUserAgent,
                                forwardedHeaders,
                                directRouteGate.Generation);
                        lease.Dispose();
                        sourceRedirectHandoff = false;
                        canRefreshClassifiedFile = false;
                        lease = await runtime.Gateway.OpenAsync(
                                ticket.UpstreamUri,
                                redirectLeaseScope: null,
                                redirectCandidateScope: null,
                                Request.HttpMethod,
                                requestUserAgent,
                                forwardedHeaders,
                                options,
                                operationToken,
                                headerBudget.Token)
                            .ConfigureAwait(false);
                        classifiedSourceRedirectGeneration = null;
                        forceRelayCurrentResponse = true;
                        continue;
                    }
                    if ((!sourceRedirectHandoff && !classifiedSourceRedirectGeneration.HasValue) ||
                        ticket.Purpose != PlaybackTicketPurpose.DirectClient)
                        throw new InvalidOperationException("An unexpected source redirect handoff was returned.");
                    var routeGeneration = classifiedSourceRedirectGeneration ?? directRouteGate?.Generation;
                    var rememberRedirectTarget = options.DirectRedirectCacheSeconds > 0;
                    var shouldRememberRoute = routeGeneration.HasValue &&
                                              (rememberRedirectTarget || classifiedSourceRedirectGeneration.HasValue);
                    if (!runtime.TryCommit(
                            operation.Generation,
                            () => true,
                            () =>
                            {
                                headerBudget.Token.ThrowIfCancellationRequested();
                                WriteSourceRedirectResponse(
                                    ticket,
                                    lease.EffectiveUri,
                                    (int)lease.Response.StatusCode,
                                    runtime.Clock.UtcNow,
                                    options.DirectRedirectCacheSeconds);
                                if (shouldRememberRoute && directRouteScope is not null)
                                    runtime.Gateway.RememberDirectSourceRedirect(
                                        ticket.UpstreamUri,
                                        directRouteScope,
                                        Request.HttpMethod,
                                        requestUserAgent,
                                        forwardedHeaders,
                                        rememberRedirectTarget ? lease.EffectiveUri : null,
                                        SourceTransportBehavior.FileBody,
                                        rememberRedirectTarget
                                            ? TimeSpan.FromSeconds(options.DirectRedirectCacheSeconds)
                                            : TimeSpan.Zero,
                                        routeGeneration.GetValueOrDefault());
                            }))
                        throw Unavailable();
                    logger.Debug("STRM_BRIDGE_GATEWAY_SOURCE_REDIRECT item=" + ShortId(ticket.ItemId));
                    return string.Empty;
                }
                if (lease.UsedCachedRedirect)
                    logger.Debug("STRM_BRIDGE_GATEWAY_REDIRECT_LEASE_HIT item=" + ShortId(ticket.ItemId));
                if (lease.RetriedRejectedRedirect)
                    logger.Debug("STRM_BRIDGE_GATEWAY_REDIRECT_RETRIED item=" + ShortId(ticket.ItemId));
                if (expectedRepresentation is not null &&
                    (!string.Equals(requestUserAgent?.Trim(), expectedRepresentation.UserAgent, StringComparison.Ordinal) ||
                     !expectedRepresentation.Matches(lease.Response)))
                {
                    Request.Response.StatusCode = 412;
                    AddSafeHeaders();
                    return string.Empty;
                }
                if (options.PlaybackMode != PlaybackRoutingMode.RedirectOnly &&
                    HasResponseBody(lease.Response, Request.HttpMethod) &&
                    HasNonIdentityContentEncoding(lease.Response))
                    throw new IOException("An encoded upstream body cannot be classified safely.");
                var behavior = lease.IsHlsManifest ? SourceTransportBehavior.HlsManifest :
                    SourceBehaviorClassifier.Classify(lease.Response, lease.EffectiveUri);
                if (behavior != SourceTransportBehavior.HlsManifest &&
                    HasResponseBody(lease.Response, Request.HttpMethod) &&
                    (int)lease.Response.StatusCode is 200 or 206)
                {
                    var prefix = await lease.PeekPrefixAsync(10, headerBudget.Token).ConfigureAwait(false);
                    behavior = SourceBehaviorClassifier.Classify(
                        lease.Response,
                        lease.EffectiveUri,
                        prefix);
                }
                if (behavior == SourceTransportBehavior.HlsManifest)
                {
                    runtime.Gateway.MarkHlsManifest(lease);
                    if (options.PlaybackMode != PlaybackRoutingMode.RedirectOnly &&
                        NeedsCompleteManifest(lease.Response, Request.HttpMethod))
                    {
                        lease.Dispose();
                        var completeHeaders = new Dictionary<string, string>(forwardedHeaders, StringComparer.OrdinalIgnoreCase);
                        foreach (var name in new[] { "Range", "If-Range", "If-None-Match", "If-Modified-Since" })
                            completeHeaders.Remove(name);
                        lease = await OpenLeaseAsync("GET", completeHeaders).ConfigureAwait(false);
                        if (!runtime.IsOperationCurrent(operation.Generation)) throw Unavailable();
                        if (HasResponseBody(lease.Response, "GET") &&
                            HasNonIdentityContentEncoding(lease.Response))
                            throw new IOException("An encoded upstream body cannot be classified safely.");
                        if (expectedRepresentation is not null && !expectedRepresentation.Matches(lease.Response))
                        {
                            Request.Response.StatusCode = 412;
                            AddSafeHeaders();
                            return string.Empty;
                        }
                        if ((int)lease.Response.StatusCode == 206 && !IsCompleteRepresentation(lease.Response))
                            throw new IOException("The source did not return a complete HLS manifest.");
                        runtime.Gateway.MarkHlsManifest(lease);
                    }
                }
                var responseStatusCode = (int)lease.Response.StatusCode;
                var relayCurrentResponse = forceRelayCurrentResponse ||
                                           TransportPlanner.RequiresAdaptiveRelay(responseStatusCode);
                var plan = TransportPlanner.Create(options.PlaybackMode, behavior, relayCurrentResponse);
                var canHandoff = TransportPlanner.CanHandoffRedirect(responseStatusCode);
                if (plan == GatewayTransportPlan.Redirect && canHandoff)
                {
                    if (canRefreshClassifiedFile && lease.RedirectCount > 0 &&
                        behavior == SourceTransportBehavior.FileBody)
                    {
                        var routeGeneration = directRouteGate!.Generation;
                        canRefreshClassifiedFile = false;
                        runtime.Gateway.ForgetDirectRouteState(
                            directRouteScope!,
                            Request.HttpMethod,
                            requestUserAgent,
                            forwardedHeaders,
                            routeGeneration);
                        lease.Dispose();
                        lease = await runtime.Gateway.OpenSourceRedirectHandoffAsync(
                                ticket.UpstreamUri,
                                Request.HttpMethod,
                                requestUserAgent,
                                forwardedHeaders,
                                options,
                                operationToken,
                                headerBudget.Token)
                            .ConfigureAwait(false);
                        classifiedSourceRedirectGeneration = routeGeneration;
                        continue;
                    }
                    if (!runtime.TryCommit(
                            operation.Generation,
                            () => true,
                            () =>
                            {
                                headerBudget.Token.ThrowIfCancellationRequested();
                                if (directRouteScope is not null)
                                    runtime.Gateway.RememberDirectRedirect(
                                        ticket.UpstreamUri,
                                        directRouteScope,
                                        Request.HttpMethod,
                                        requestUserAgent,
                                        forwardedHeaders,
                                        lease,
                                        behavior);
                                var responseTime = runtime.Clock.UtcNow;
                                var sourceExpiresAtUtc = lease.RedirectCount == 0 &&
                                                         ticket.Purpose == PlaybackTicketPurpose.DirectClient &&
                                                         directRouteScope is not null &&
                                                         options.DirectRedirectCacheSeconds > 0
                                    ? responseTime + TimeSpan.FromSeconds(Math.Min(
                                        options.DirectRedirectCacheSeconds,
                                        (int)GatewayTransport.RedirectLeaseLifetime.TotalSeconds))
                                    : ticket.Purpose == PlaybackTicketPurpose.ServerFfmpeg || lease.RedirectCount == 0
                                        ? lease.RedirectExpiresAtUtc
                                        : responseTime;
                                WriteRedirectResponse(
                                    ticket,
                                    lease.EffectiveUri,
                                    responseTime,
                                    sourceExpiresAtUtc,
                                    options.DirectRedirectCacheSeconds);
                            }))
                        throw Unavailable();
                    logger.Debug("STRM_BRIDGE_GATEWAY_REDIRECT item=" + ShortId(ticket.ItemId));
                    return string.Empty;
                }
                if (plan == GatewayTransportPlan.RelayHls && canHandoff)
                {
                    var headers = CreateResponseHeaders(lease, includeLength: false);
                    foreach (var name in new[]
                             {
                             "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Content-Encoding",
                         })
                        headers.Remove(name);
                    var manifest = await ReadManifestAsync(lease, headerBudget.Token).ConfigureAwait(false);
                    var apiPathBase = GatewayRouteBuilder.GetApiPathBase(Request);
                    var rootTicket = parentTicketValue ?? ticketValue;
                    using var mutation = await runtime.Tickets.AcquireHlsMutationAsync(
                            rootTicket,
                            headerBudget.Token)
                        .ConfigureAwait(false);
                    var retention = HlsPlaylistRewriter.GetResourceRetention(manifest);
                    var childTickets = new List<string>();
                    var referencedTickets = new HashSet<string>(StringComparer.Ordinal);
                    var childRoutes = new Dictionary<string, string>(StringComparer.Ordinal);
                    try
                    {
                        var rewritten = HlsPlaylistRewriter.Rewrite(
                            manifest,
                            lease.EffectiveUri,
                            target =>
                            {
                                headerBudget.Token.ThrowIfCancellationRequested();
                                var validated = runtime.Gateway.ValidateResource(lease.EffectiveUri, target);
                                if (childRoutes.TryGetValue(validated.AbsoluteUri, out var existingRoute))
                                    return existingRoute;
                                var childTicket = runtime.Tickets.IssueHlsResource(
                                    rootTicket,
                                    ticketValue,
                                    ticket,
                                    validated,
                                    out var created);
                                if (created) childTickets.Add(childTicket);
                                referencedTickets.Add(childTicket);
                                var route = GatewayRouteBuilder.CreateHlsRoute(
                                    apiPathBase,
                                    rootTicket,
                                    childTicket,
                                    validated);
                                childRoutes.Add(validated.AbsoluteUri, route);
                                return route;
                            });
                        headerBudget.Token.ThrowIfCancellationRequested();
                        Request.Response.StatusCode = 200;
                        headers["Cache-Control"] = "private, no-store";
                        var result = resultFactory.GetResult(
                            Request,
                            new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(rewritten)),
                            "application/vnd.apple.mpegurl",
                            headers);
                        headerBudget.Token.ThrowIfCancellationRequested();
                        runtime.Tickets.CommitHlsManifest(rootTicket, ticketValue, referencedTickets, retention);
                        logger.Debug("STRM_BRIDGE_GATEWAY_HLS item=" + ShortId(ticket.ItemId));
                        return result;
                    }
                    catch
                    {
                        foreach (var childTicket in childTickets) runtime.Tickets.Revoke(childTicket);
                        throw;
                    }
                }
                break;
            }
            headerBudget.Token.ThrowIfCancellationRequested();
            lease.OwnCancellation(operationCancellation);
            operationCancellation = null;
            return await CreateRelayResultAsync(lease, ticket).ConfigureAwait(false);
        }
        catch (GatewaySourceBackoffException exception)
        {
            Request.Response.StatusCode = exception.StatusCode;
            AddSafeHeaders();
            Request.Response.AddHeader(
                "Retry-After",
                exception.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
            logger.Debug("STRM_BRIDGE_GATEWAY_SOURCE_BACKOFF status=" +
                         exception.StatusCode.ToString(CultureInfo.InvariantCulture));
            return string.Empty;
        }
        catch (Exception exception) when (exception is GatewayCapacityException or TicketCapacityException)
        {
            MarkProbeLocalFailure(probeRoot);
            Request.Response.StatusCode = 503;
            AddSafeHeaders();
            Request.Response.AddHeader("Retry-After", "1");
            logger.Debug("STRM_BRIDGE_GATEWAY_CAPACITY item=" + ShortId(ticket.ItemId));
            return string.Empty;
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
            throw Unavailable();
        }
        catch (OperationCanceledException) when (!Request.CancellationToken.IsCancellationRequested)
        {
            Request.Response.StatusCode = 504;
            AddSafeHeaders();
            logger.Debug("STRM_BRIDGE_GATEWAY_TIMEOUT item=" + ShortId(ticket.ItemId));
            return string.Empty;
        }
        catch (Exception exception) when (
            exception is System.Net.Http.HttpRequestException ||
            exception is RedirectRejectedException ||
            exception is TicketCapacityException ||
            exception is InvalidOperationException ||
            exception is IOException)
        {
            if (exception is TicketCapacityException) MarkProbeLocalFailure(probeRoot);
            var rejection = RedirectPolicy.FindRejection(exception);
            if (isProbe && rejection is not null)
            {
                var root = ticket;
                if (parentTicketValue is not null && runtime.Tickets.TryInspect(parentTicketValue, out var inspectedRoot))
                    root = inspectedRoot!;
                Interlocked.Exchange(ref root.ProbeRejectionReason, (int)rejection.Reason);
            }
            if (exception is System.Net.Http.HttpRequestException)
            {
                Request.Response.StatusCode = rejection is null ? 502 : 403;
                AddSafeHeaders();
                logger.Debug("STRM_BRIDGE_GATEWAY_CONNECTION_FAILED item=" + ShortId(ticket.ItemId) +
                             " reason=" + (rejection?.Reason.ToString().ToLowerInvariant() ?? "transport"));
                return string.Empty;
            }
            logger.Debug("STRM_BRIDGE_GATEWAY_REJECTED item=" + ShortId(ticket.ItemId) +
                         " error=" + exception.GetType().Name);
            throw Unavailable();
        }
        finally
        {
            lease?.Dispose();
            operationCancellation?.Dispose();
            directRouteGate?.Dispose();
        }
    }

    private async Task<object> CreateRelayResultAsync(GatewayTransportLease lease, TicketPayload ticket)
    {
        if (string.Equals(Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            using (lease)
            {
                Request.Response.StatusCode = (int)lease.Response.StatusCode;
                var headers = CreateResponseHeaders(lease, includeLength: true);
                logger.Debug("STRM_BRIDGE_GATEWAY_HEAD item=" + ShortId(ticket.ItemId));
                return resultFactory.GetResult(
                    Request,
                    ReadOnlyMemory<byte>.Empty,
                    lease.Response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream",
                    headers);
            }
        }

        Request.Response.StatusCode = (int)lease.Response.StatusCode;
        var responseHeaders = CreateResponseHeaders(lease, includeLength: true);
        var contentType = lease.Response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        Stream? stream = null;
        try
        {
            stream = await lease.OpenOwnedStreamAsync().ConfigureAwait(false);
            var result = resultFactory.GetResult(Request, stream, contentType, responseHeaders);
            logger.Debug("STRM_BRIDGE_GATEWAY_RELAY item=" + ShortId(ticket.ItemId));
            return result;
        }
        catch
        {
            stream?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    private async Task<string> ReadManifestAsync(GatewayTransportLease lease, CancellationToken cancellationToken)
    {
        var declaredLength = lease.Response.Content.Headers.ContentLength;
        if (declaredLength > HlsPlaylistRewriter.MaximumManifestBytes)
            throw new InvalidOperationException("The HLS manifest is too large.");
        using var stream = await lease.OpenOwnedStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > HlsPlaylistRewriter.MaximumManifestBytes)
                throw new InvalidOperationException("The HLS manifest is too large.");
            buffer.Write(chunk, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }

    private Dictionary<string, string> GetForwardedRequestHeaders()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ForwardedRequestHeaders)
        {
            var value = Request.Headers?[name];
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 4096 &&
                value.IndexOfAny(new[] { '\r', '\n' }) < 0)
                result[name] = value;
        }
        return result;
    }

    private string? GetRequestUserAgent()
    {
        var header = Request.Headers?["User-Agent"];
        return !string.IsNullOrWhiteSpace(header) ? header : Request.UserAgent;
    }

    private static Dictionary<string, string> CreateResponseHeaders(
        GatewayTransportLease lease,
        bool includeLength)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cache-Control"] = "private, no-store",
            ["Pragma"] = "no-cache",
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "no-referrer",
        };
        CopyHeader(lease, headers, "Content-Range");
        CopyHeader(lease, headers, "Accept-Ranges");
        CopyHeader(lease, headers, "ETag");
        CopyHeader(lease, headers, "Last-Modified");
        CopyHeader(lease, headers, "Content-Disposition");
        CopyHeader(lease, headers, "Content-Encoding");
        CopyHeader(lease, headers, "Retry-After");
        if (includeLength && lease.Response.Content.Headers.ContentLength is long length)
            headers["Content-Length"] = length.ToString(CultureInfo.InvariantCulture);
        return headers;
    }

    private static void CopyHeader(
        GatewayTransportLease lease,
        IDictionary<string, string> target,
        string name)
    {
        if (lease.Response.Headers.TryGetValues(name, out var values) ||
            lease.Response.Content.Headers.TryGetValues(name, out values))
        {
            var value = string.Join(", ", values);
            if (value.Length <= 8192 && value.IndexOfAny(new[] { '\r', '\n' }) < 0) target[name] = value;
        }
    }

    private static bool HasNonIdentityContentEncoding(HttpResponseMessage response) =>
        response.Content.Headers.ContentEncoding
            .SelectMany(value => value.Split(','))
            .Select(value => value.Trim())
            .Any(value => value.Length > 0 &&
                          !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase));

    private void AddSafeHeaders()
    {
        Request.Response.AddHeader("Cache-Control", "private, no-store");
        Request.Response.AddHeader("Pragma", "no-cache");
        Request.Response.AddHeader("Referrer-Policy", "no-referrer");
        Request.Response.AddHeader("X-Content-Type-Options", "nosniff");
    }

    private void WriteRedirectResponse(
        TicketPayload ticket,
        Uri effectiveUri,
        DateTimeOffset now,
        DateTimeOffset? sourceExpiresAtUtc = null,
        int directClientCacheSeconds = 0)
    {
        if (now >= ticket.ExpiresAtUtc) throw Unavailable();
        var policy = CreateRedirectResponsePolicy(
            ticket.Purpose,
            now,
            ticket.ExpiresAtUtc,
            sourceExpiresAtUtc,
            directClientCacheSeconds);
        Request.Response.StatusCode = policy.StatusCode;
        AddRedirectResponseHeaders(policy);
        Request.Response.AddHeader("Location", effectiveUri.AbsoluteUri);
    }

    private void WriteSourceRedirectResponse(
        TicketPayload ticket,
        Uri effectiveUri,
        int statusCode,
        DateTimeOffset now,
        int directClientCacheSeconds = 0)
    {
        if (now >= ticket.ExpiresAtUtc || statusCode is not (301 or 302 or 303 or 307 or 308))
            throw Unavailable();
        var policy = CreateRedirectResponsePolicy(
            ticket.Purpose,
            now,
            ticket.ExpiresAtUtc,
            directClientCacheSeconds > 0
                ? now + TimeSpan.FromSeconds(directClientCacheSeconds)
                : null,
            directClientCacheSeconds);
        Request.Response.StatusCode = policy.StatusCode;
        AddRedirectResponseHeaders(policy);
        Request.Response.AddHeader("Location", effectiveUri.AbsoluteUri);
    }

    private void AddRedirectResponseHeaders(GatewayRedirectResponsePolicy policy)
    {
        if (policy.Expires is null)
        {
            AddSafeHeaders();
            return;
        }
        Request.Response.AddHeader("Cache-Control", policy.CacheControl);
        Request.Response.AddHeader("Expires", policy.Expires);
        Request.Response.AddHeader("Vary", "User-Agent, Accept, Accept-Language");
        Request.Response.AddHeader("Referrer-Policy", "no-referrer");
        Request.Response.AddHeader("X-Content-Type-Options", "nosniff");
    }

    internal static GatewayRedirectResponsePolicy CreateRedirectResponsePolicy(
        PlaybackTicketPurpose purpose,
        DateTimeOffset now,
        DateTimeOffset? ticketExpiresAtUtc = null,
        DateTimeOffset? sourceExpiresAtUtc = null,
        int directClientCacheSeconds = 0)
    {
        if (purpose == PlaybackTicketPurpose.DirectClient)
        {
            if (directClientCacheSeconds <= 0)
                return new GatewayRedirectResponsePolicy(302, "private, no-store", null);
            var directExpiry = now + TimeSpan.FromSeconds(directClientCacheSeconds);
            if (ticketExpiresAtUtc.HasValue && ticketExpiresAtUtc.Value < directExpiry)
                directExpiry = ticketExpiresAtUtc.Value;
            if (sourceExpiresAtUtc.HasValue && sourceExpiresAtUtc.Value < directExpiry)
                directExpiry = sourceExpiresAtUtc.Value;
            var directMaxAge = Math.Max(0, (int)Math.Floor((directExpiry - now).TotalSeconds));
            if (directMaxAge == 0)
                return new GatewayRedirectResponsePolicy(302, "private, no-store", null);
            directExpiry = now + TimeSpan.FromSeconds(directMaxAge);
            return new GatewayRedirectResponsePolicy(
                302,
                "max-age=" + directMaxAge.ToString(CultureInfo.InvariantCulture) + ", private, must-revalidate",
                directExpiry.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        }
        if (purpose != PlaybackTicketPurpose.ServerFfmpeg)
            return new GatewayRedirectResponsePolicy(302, "private, no-store", null);
        var expiry = now + ServerFfmpegRedirectLifetime;
        if (ticketExpiresAtUtc.HasValue && ticketExpiresAtUtc.Value < expiry) expiry = ticketExpiresAtUtc.Value;
        if (sourceExpiresAtUtc.HasValue && sourceExpiresAtUtc.Value < expiry) expiry = sourceExpiresAtUtc.Value;
        var maxAge = Math.Max(0, (int)Math.Floor((expiry - now).TotalSeconds));
        if (maxAge == 0) return new GatewayRedirectResponsePolicy(307, "private, no-store", null);
        expiry = now + TimeSpan.FromSeconds(maxAge);
        return new GatewayRedirectResponsePolicy(
            307,
            "max-age=" + maxAge.ToString(CultureInfo.InvariantCulture) + ", private, must-revalidate",
            expiry.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void MarkProbeRequestObserved(TicketPayload? probeRoot)
    {
        if (probeRoot is not null && Interlocked.Exchange(ref probeRoot.ProbeRequestObserved, 1) == 0)
            Interlocked.Exchange(ref probeRoot.ProbeInputObservedCallback, null)?.Invoke();
    }

    private static void MarkProbeLocalFailure(TicketPayload? probeRoot)
    {
        if (probeRoot is not null) Interlocked.Exchange(ref probeRoot.ProbeLocalFailureObserved, 1);
    }

    internal static bool IsLoopbackRequest(IPAddress? remoteIp) =>
        remoteIp is not null && IPAddress.IsLoopback(remoteIp);

    internal static bool RequestsFreshRedirect(IReadOnlyDictionary<string, string> headers)
    {
        var cacheControl = headers.TryGetValue("Cache-Control", out var value) ? value : string.Empty;
        foreach (var rawDirective in cacheControl.Split(','))
        {
            var directive = rawDirective.Trim();
            var separator = directive.IndexOf('=');
            var name = (separator >= 0 ? directive.Substring(0, separator) : directive).Trim();
            if (string.Equals(name, "no-cache", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "no-store", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(name, "max-age", StringComparison.OrdinalIgnoreCase) || separator < 0)
                continue;
            var parameter = directive.Substring(separator + 1).Trim().Trim('"');
            if (long.TryParse(parameter, NumberStyles.None, CultureInfo.InvariantCulture, out var maxAge) && maxAge == 0)
                return true;
        }
        var pragma = headers.TryGetValue("Pragma", out value) ? value : string.Empty;
        return pragma.Split(',')
            .Select(directive => directive.Trim())
            .Any(directive => string.Equals(directive, "no-cache", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsIncludedItem(MediaBrowser.Controller.Entities.BaseItem? item, string[] includedLibraryIds, bool isProbe)
    {
        var supportedType = PlaybackItemPolicy.IsVideo(item) ||
                            isProbe && string.Equals(item?.MediaType, MediaBrowser.Model.Entities.MediaType.Audio,
                                StringComparison.OrdinalIgnoreCase);
        if (!supportedType || string.IsNullOrWhiteSpace(item!.Path) ||
            includedLibraryIds.Length == 0)
            return false;
        var allowed = new HashSet<string>(includedLibraryIds, StringComparer.OrdinalIgnoreCase);
        return libraryManager.GetCollectionFolders(item)
            .Any(folder => allowed.Contains(folder.Id.ToString("N")) || allowed.Contains(folder.Id.ToString()));
    }

    private static bool IsValidFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32 &&
        value.IndexOfAny(new[] { '/', '\\', '?', '#', '\r', '\n' }) < 0 &&
        (value.StartsWith("stream", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("resource", StringComparison.OrdinalIgnoreCase));

    private static bool NeedsCompleteManifest(System.Net.Http.HttpResponseMessage response, string method) =>
        string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) &&
        TransportPlanner.CanHandoffRedirect((int)response.StatusCode) ||
        (int)response.StatusCode == 304 ||
        (int)response.StatusCode == 206 && !IsCompleteRepresentation(response);

    private static bool IsCompleteRepresentation(System.Net.Http.HttpResponseMessage response)
    {
        var range = response.Content.Headers.ContentRange;
        return range is not null && string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) &&
               range.From == 0 && range.Length.HasValue &&
               range.To == range.Length.Value - 1 &&
               response.Content.Headers.ContentLength == range.Length;
    }

    private static bool HasResponseBody(System.Net.Http.HttpResponseMessage response, string method)
    {
        if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)) return false;
        var status = (int)response.StatusCode;
        return status is not 204 and not 304;
    }

    private static ResourceNotFoundException Unavailable() =>
        new("The playback gateway resource is unavailable.");

    private static string ShortId(Guid itemId) => itemId.ToString("N").Substring(0, 8);
}

internal readonly struct GatewayRedirectResponsePolicy
{
    public GatewayRedirectResponsePolicy(int statusCode, string cacheControl, string? expires)
    {
        StatusCode = statusCode;
        CacheControl = cacheControl;
        Expires = expires;
    }

    public int StatusCode { get; }

    public string CacheControl { get; }

    public string? Expires { get; }
}
