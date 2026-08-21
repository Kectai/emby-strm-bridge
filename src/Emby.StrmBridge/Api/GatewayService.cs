using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly ILibraryManager libraryManager;
    private readonly IAuthorizationContext authorizationContext;
    private readonly ILogger logger;

    public GatewayService(
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext,
        ILogManager logManager)
    {
        this.libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        this.authorizationContext = authorizationContext ?? throw new ArgumentNullException(nameof(authorizationContext));
        logger = logManager.GetLogger(Plugin.Instance?.Name ?? "STRM Bridge");
    }

    public IRequest Request { get; set; } = null!;

    public Task<object> Get(GetStrmBridgeRedirect request) => ResolveAsync(request);

    public Task<object> Head(GetStrmBridgeRedirect request) => ResolveAsync(request);

    private async Task<object> ResolveAsync(GetStrmBridgeRedirect request)
    {
        var plugin = Plugin.Instance;
        var runtime = Plugin.Runtime;
        var options = runtime?.GetOptionsSnapshot();
        var isLocalServerRequest = IsDirectLoopbackRequest(Request);
        AuthorizationInfo? authorization = null;
        MediaBrowser.Controller.Entities.User? user = null;
        if (!isLocalServerRequest)
        {
            authorization = authorizationContext.GetAuthorizationInfo(Request);
            user = authorization.User;
        }
        if (plugin is null || runtime?.SourcePolicy is null || runtime.Redirects is null ||
            options is null || !options.Enabled || !options.EnablePlaybackSource ||
            (!isLocalServerRequest && (user is null || !user.Policy.EnableMediaPlayback)) ||
            !runtime.Tickets.TryInspect(
                request.Ticket,
                TicketScope.PlaybackRedirect,
                out var ticket))
        {
            throw new ResourceNotFoundException("The playback redirect is unavailable.");
        }

        var item = libraryManager.GetItemById(ticket!.ItemId);
        if (!IsIncludedItem(item, options.IncludedLibraryIds) ||
            (!isLocalServerRequest && !item!.IsVisible(user)))
        {
            throw new ResourceNotFoundException("The playback redirect is unavailable.");
        }
        var redeemed = isLocalServerRequest
            ? runtime.Tickets.TryRedeemLocalServer(
                request.Ticket,
                TicketScope.PlaybackRedirect,
                out ticket)
            : runtime.Tickets.TryRedeem(
                request.Ticket,
                TicketScope.PlaybackRedirect,
                authorization!.UserId,
                out ticket);
        if (!redeemed)
            throw new ResourceNotFoundException("The playback redirect is unavailable.");
        var redeemedTicket = ticket!;

        SourceIdentity? currentSource = null;
        try
        {
            var operation = runtime.BeginOperation();
            currentSource = runtime.SourcePolicy.Read(item.Path);
            if (!redeemedTicket.Source.HasSameFileVersion(currentSource))
            {
                runtime.Tickets.Revoke(request.Ticket);
                throw new ResourceNotFoundException("The playback redirect is unavailable.");
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                Request.CancellationToken,
                operation.CancellationToken);
            var lease = await runtime.Redirects.ResolveAsync(currentSource, Request.UserAgent, linked.Token)
                .ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();

            var committed = runtime.TryCommit(
                operation.Generation,
                () =>
                {
                    var latestOptions = runtime.GetOptionsSnapshot();
                    var latestItem = libraryManager.GetItemById(redeemedTicket.ItemId);
                    if (!latestOptions.Enabled || !latestOptions.EnablePlaybackSource ||
                        !IsIncludedItem(latestItem, latestOptions.IncludedLibraryIds))
                        return false;
                    TicketPayload? latestTicket;
                    if (isLocalServerRequest)
                    {
                        if (!IsDirectLoopbackRequest(Request) ||
                            !runtime.Tickets.TryRedeemLocalServer(
                                request.Ticket,
                                TicketScope.PlaybackRedirect,
                                out latestTicket))
                            return false;
                    }
                    else
                    {
                        var latestAuthorization = authorizationContext.GetAuthorizationInfo(Request);
                        var latestUser = latestAuthorization.User;
                        if (latestUser is null || !latestUser.Policy.EnableMediaPlayback ||
                            !latestItem!.IsVisible(latestUser) ||
                            !runtime.Tickets.TryRedeem(
                                request.Ticket,
                                TicketScope.PlaybackRedirect,
                                latestAuthorization.UserId,
                                out latestTicket))
                            return false;
                    }
                    var latestSource = runtime.SourcePolicy.Read(latestItem!.Path);
                    return latestTicket!.Source.HasSameFileVersion(latestSource) &&
                           lease.IsValidAt(runtime.Clock.UtcNow);
                },
                () =>
                {
                    Request.Response.StatusCode = 302;
                    AddSafeHeaders();
                    Request.Response.AddHeader("Location", lease.GetLocation());
                });
            if (!committed)
            {
                runtime.Tickets.Revoke(request.Ticket);
                throw new ResourceNotFoundException("The playback redirect is unavailable.");
            }
            logger.Debug("STRM_BRIDGE_REDIRECT_RESOLVED item=" + ShortId(redeemedTicket.ItemId));
            return string.Empty;
        }
        catch (RedirectThrottledException exception)
        {
            Request.Response.StatusCode = 429;
            AddSafeHeaders();
            AddRetryAfter(exception.RetryAfterSeconds);
            logger.Debug("STRM_BRIDGE_SOURCE_THROTTLED item=" + ShortId(redeemedTicket.ItemId));
            return string.Empty;
        }
        catch (RedirectSourceUnavailableException exception)
        {
            Request.Response.StatusCode = 503;
            AddSafeHeaders();
            AddRetryAfter(exception.RetryAfterSeconds);
            logger.Debug("STRM_BRIDGE_SOURCE_UNAVAILABLE item=" + ShortId(redeemedTicket.ItemId));
            return string.Empty;
        }
        catch (OperationCanceledException) when (!Request.CancellationToken.IsCancellationRequested)
        {
            runtime.Tickets.Revoke(request.Ticket);
            throw new ResourceNotFoundException("The playback redirect is unavailable.");
        }
        catch (RedirectRejectedException exception)
        {
            if (exception.Reason == RedirectRejectionReason.UnexpectedStatus && currentSource is not null)
            {
                try
                {
                    runtime.ExtractionState?.RecordRedirectBridgeRequirement(
                        currentSource.StorageKey,
                        currentSource.SourceFingerprint,
                        requiresRedirectBridge: false,
                        runtime.Clock.UtcNow);
                    runtime.ExtractionState?.Flush();
                }
                catch (Exception persistenceException) when (
                    persistenceException is System.IO.IOException ||
                    persistenceException is UnauthorizedAccessException ||
                    persistenceException is System.IO.InvalidDataException ||
                    persistenceException is System.Runtime.Serialization.SerializationException)
                {
                    logger.Debug("STRM_BRIDGE_STATE_SAVE_FAILED");
                }
            }
            runtime.Tickets.Revoke(request.Ticket);
            logger.Debug("STRM_BRIDGE_REDIRECT_REJECTED item=" + ShortId(redeemedTicket.ItemId));
            throw new ResourceNotFoundException("The playback redirect is unavailable.");
        }
        catch (Exception exception) when (
            exception is SourcePolicyException ||
            exception is InvalidOperationException || exception is System.Net.Http.HttpRequestException)
        {
            runtime.Tickets.Revoke(request.Ticket);
            logger.Debug("STRM_BRIDGE_REDIRECT_REJECTED item=" + ShortId(redeemedTicket.ItemId));
            throw new ResourceNotFoundException("The playback redirect is unavailable.");
        }
    }

    private void AddRetryAfter(int seconds) => Request.Response.AddHeader(
        "Retry-After",
        Math.Max(1, Math.Min(60, seconds)).ToString(CultureInfo.InvariantCulture));

    private void AddSafeHeaders()
    {
        Request.Response.AddHeader("Cache-Control", "private, no-store");
        Request.Response.AddHeader("Pragma", "no-cache");
        Request.Response.AddHeader("Referrer-Policy", "no-referrer");
        Request.Response.AddHeader("X-Content-Type-Options", "nosniff");
    }

    private static string ShortId(Guid itemId) => itemId.ToString("N").Substring(0, 8);

    internal static bool IsDirectLoopbackRequest(IRequest request)
    {
        if (request is null ||
            !string.IsNullOrWhiteSpace(request.XForwardedFor) ||
            !string.IsNullOrWhiteSpace(request.XRealIp) ||
            !string.IsNullOrWhiteSpace(request.Headers?["Forwarded"]) ||
            request.RemoteIp is null)
            return false;
        var address = request.RemoteIp;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    private bool IsIncludedItem(
        MediaBrowser.Controller.Entities.BaseItem? item,
        string[] includedLibraryIds)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path)) return false;
        if (includedLibraryIds.Length == 0) return false;
        var allowed = new System.Collections.Generic.HashSet<string>(
            includedLibraryIds,
            StringComparer.OrdinalIgnoreCase);
        return libraryManager.GetCollectionFolders(item)
            .Any(folder => allowed.Contains(folder.Id.ToString("N")) || allowed.Contains(folder.Id.ToString()));
    }
}
