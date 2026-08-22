using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Api;

public sealed class GatewayService : IService, IRequiresRequest
{
    private static readonly string[] ForwardedRequestHeaders =
    {
        "Range", "If-Range", "If-None-Match", "If-Modified-Since", "Accept", "Accept-Language", "Cache-Control",
    };
    private readonly ILibraryManager libraryManager;
    private readonly IAuthorizationContext authorizationContext;
    private readonly IHttpResultFactory resultFactory;
    private readonly ILogger logger;

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
        var runtime = Plugin.Runtime;
        var options = runtime?.GetOptionsSnapshot();
        if (runtime?.SourcePolicy is null || runtime.Gateway is null || options is null ||
            !options.Enabled || options.PlaybackMode == PlaybackRoutingMode.Native ||
            !runtime.Tickets.TryInspect(ticketValue, out var inspected))
            throw Unavailable();

        if (parentTicketValue is not null &&
            (!runtime.Tickets.TryInspect(parentTicketValue, out var parent) ||
             parent!.Scope != TicketScope.Playback || inspected!.Scope != TicketScope.HlsResource ||
             parent.ItemId != inspected.ItemId ||
             !string.Equals(parent.MediaSourceId, inspected.MediaSourceId, StringComparison.Ordinal)))
            throw Unavailable();
        if (parentTicketValue is null && inspected!.Scope != TicketScope.Playback) throw Unavailable();

        AuthorizationInfo? authorization = null;
        try { authorization = authorizationContext.GetAuthorizationInfo(Request); }
        catch (UnauthorizedAccessException) { }
        catch (Exception exception)
        {
            logger.Debug("STRM_BRIDGE_GATEWAY_AUTH_FAILED error=" + exception.GetType().Name);
            throw Unavailable();
        }
        var user = authorization?.User;
        if (user is not null && !user.Policy.EnableMediaPlayback) throw Unavailable();
        var authenticatedUserId = user?.Id.ToString("N");
        if (!runtime.Tickets.TryRedeem(ticketValue, authenticatedUserId, out var ticket)) throw Unavailable();
        if (ticket!.RuntimeGeneration != runtime.Generation)
        {
            runtime.Tickets.Revoke(ticketValue);
            throw Unavailable();
        }

        var item = libraryManager.GetItemById(ticket.ItemId);
        if (!IsIncludedItem(item, options.IncludedLibraryIds) || (user is not null && !item!.IsVisible(user)))
            throw Unavailable();
        SourceIdentity currentSource;
        try { currentSource = runtime.SourcePolicy.Read(item!.Path); }
        catch (SourcePolicyException) { throw Unavailable(); }
        if (!ticket.Source.HasSameFileVersion(currentSource))
        {
            runtime.Tickets.Revoke(ticketValue);
            throw Unavailable();
        }

        try
        {
            var lease = await runtime.Gateway.OpenAsync(
                    ticket.UpstreamUri,
                    ticketValue,
                    Request.HttpMethod,
                    Request.UserAgent,
                    GetForwardedRequestHeaders(),
                    options,
                    Request.CancellationToken)
                .ConfigureAwait(false);
            if (lease.UsedCachedRedirect)
                logger.Debug("STRM_BRIDGE_GATEWAY_REDIRECT_LEASE_HIT item=" + ShortId(ticket.ItemId));
            if (lease.RetriedRejectedRedirect)
                logger.Debug("STRM_BRIDGE_GATEWAY_REDIRECT_RETRIED item=" + ShortId(ticket.ItemId));
            var behavior = SourceBehaviorClassifier.Classify(lease.Response, lease.EffectiveUri, lease.RedirectCount);
            if (behavior != SourceTransportBehavior.HlsManifest &&
                HasResponseBody(lease.Response, Request.HttpMethod) &&
                (int)lease.Response.StatusCode is 200 or 206)
            {
                var prefix = await lease.PeekPrefixAsync(10, Request.CancellationToken).ConfigureAwait(false);
                behavior = SourceBehaviorClassifier.Classify(
                    lease.Response,
                    lease.EffectiveUri,
                    lease.RedirectCount,
                    prefix);
            }
            var plan = TransportPlanner.Create(options.PlaybackMode, behavior);
            if (plan == GatewayTransportPlan.Redirect)
            {
                using (lease)
                {
                    Request.Response.StatusCode = 302;
                    AddSafeHeaders();
                    Request.Response.AddHeader("Location", lease.EffectiveUri.AbsoluteUri);
                }
                logger.Debug("STRM_BRIDGE_GATEWAY_REDIRECT item=" + ShortId(ticket.ItemId));
                return string.Empty;
            }
            if (plan == GatewayTransportPlan.RelayHls && HasResponseBody(lease.Response, Request.HttpMethod))
            {
                using (lease)
                {
                    var statusCode = (int)lease.Response.StatusCode;
                    var headers = CreateResponseHeaders(lease, includeLength: false);
                    var manifest = await ReadManifestAsync(lease, Request.CancellationToken).ConfigureAwait(false);
                    var apiPathBase = GatewayRouteBuilder.GetApiPathBase(Request);
                    var childTickets = new List<string>();
                    var childRoutes = new Dictionary<string, string>(StringComparer.Ordinal);
                    try
                    {
                        var rewritten = HlsPlaylistRewriter.Rewrite(
                            manifest,
                            lease.EffectiveUri,
                            target =>
                            {
                                var validated = runtime.Gateway.ValidateResource(lease.EffectiveUri, target);
                                if (childRoutes.TryGetValue(validated.AbsoluteUri, out var existingRoute))
                                    return existingRoute;
                                var childTicket = runtime.Tickets.IssueHlsResource(
                                    parentTicketValue ?? ticketValue,
                                    ticket,
                                    validated,
                                    out var created);
                                if (created) childTickets.Add(childTicket);
                                var route = GatewayRouteBuilder.CreateHlsRoute(
                                    apiPathBase,
                                    parentTicketValue ?? ticketValue,
                                    childTicket,
                                    validated);
                                childRoutes.Add(validated.AbsoluteUri, route);
                                return route;
                            });
                        Request.Response.StatusCode = statusCode;
                        headers["Cache-Control"] = "private, no-store";
                        var result = resultFactory.GetResult(
                            Request,
                            new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(rewritten)),
                            "application/vnd.apple.mpegurl",
                            headers);
                        logger.Debug("STRM_BRIDGE_GATEWAY_HLS item=" + ShortId(ticket.ItemId));
                        return result;
                    }
                    catch
                    {
                        foreach (var childTicket in childTickets) runtime.Tickets.Revoke(childTicket);
                        throw;
                    }
                }
            }
            return await CreateRelayResultAsync(lease, ticket).ConfigureAwait(false);
        }
        catch (GatewayCapacityException)
        {
            Request.Response.StatusCode = 503;
            AddSafeHeaders();
            Request.Response.AddHeader("Retry-After", "1");
            logger.Debug("STRM_BRIDGE_GATEWAY_CAPACITY item=" + ShortId(ticket.ItemId));
            return string.Empty;
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
            logger.Debug("STRM_BRIDGE_GATEWAY_REJECTED item=" + ShortId(ticket.ItemId) +
                         " error=" + exception.GetType().Name);
            throw Unavailable();
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

    private void AddSafeHeaders()
    {
        Request.Response.AddHeader("Cache-Control", "private, no-store");
        Request.Response.AddHeader("Pragma", "no-cache");
        Request.Response.AddHeader("Referrer-Policy", "no-referrer");
        Request.Response.AddHeader("X-Content-Type-Options", "nosniff");
    }

    private bool IsIncludedItem(MediaBrowser.Controller.Entities.BaseItem? item, string[] includedLibraryIds)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || includedLibraryIds.Length == 0) return false;
        var allowed = new HashSet<string>(includedLibraryIds, StringComparer.OrdinalIgnoreCase);
        return libraryManager.GetCollectionFolders(item)
            .Any(folder => allowed.Contains(folder.Id.ToString("N")) || allowed.Contains(folder.Id.ToString()));
    }

    private static bool IsValidFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 32 &&
        value.IndexOfAny(new[] { '/', '\\', '?', '#', '\r', '\n' }) < 0 &&
        (value.StartsWith("stream", StringComparison.OrdinalIgnoreCase) ||
         value.StartsWith("resource", StringComparison.OrdinalIgnoreCase));

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
