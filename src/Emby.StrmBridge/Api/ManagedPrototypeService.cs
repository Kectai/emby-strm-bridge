using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Localization;
using Emby.StrmBridge.Managed;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Api;

public sealed class ManagedPrototypeService : IService, IRequiresRequest
{
    private readonly IAuthorizationContext authorizationContext;
    private readonly ILogger logger;

    public ManagedPrototypeService(IAuthorizationContext authorizationContext, ILogManager logManager)
    {
        this.authorizationContext = authorizationContext ?? throw new ArgumentNullException(nameof(authorizationContext));
        logger = logManager.GetLogger(Plugin.Instance?.Name ?? "STRM Bridge");
    }

    public IRequest Request { get; set; } = null!;

    public object Post(CreateManagedStrmPrototype request)
    {
        RequireAdministrator();
        if (!request.ConfirmExperimental)
            throw new ArgumentException(PluginStrings.ManagedPrototypeConfirmationRequiredError);
        var runtime = GetRuntime();
        ManagedPrototypeRegistration registration;
        try
        {
            registration = runtime.CreateManagedPrototype(request.SourceUrl, request.ContainerHint);
        }
        catch (ManagedPrototypeUnavailableException)
        {
            throw new InvalidOperationException(PluginStrings.ManagedPrototypeUnavailableError);
        }
        catch (ManagedPrototypeRegistrationException exception)
        {
            throw new ArgumentException(exception.Reason switch
            {
                ManagedPrototypeRegistrationFailure.InvalidContainerHint =>
                    PluginStrings.ManagedPrototypeInvalidContainerError,
                ManagedPrototypeRegistrationFailure.CapacityExceeded =>
                    PluginStrings.ManagedPrototypeCapacityError,
                _ => PluginStrings.ManagedPrototypeInvalidSourceError,
            });
        }
        logger.Info("STRM_BRIDGE_MANAGED_PROTOTYPE_CREATED");
        return new ManagedStrmPrototypeResponse
        {
            RelativePath = registration.RelativePath,
            ExpiresAtUtc = registration.ExpiresAtUtc,
        };
    }

    public object Delete(ClearManagedStrmPrototypes request)
    {
        RequireAdministrator();
        var cleared = GetRuntime().ClearManagedPrototypes();
        logger.Info("STRM_BRIDGE_MANAGED_PROTOTYPES_CLEARED count=" +
                    cleared.ToString(CultureInfo.InvariantCulture));
        return new { Cleared = cleared };
    }

    public Task<object> Get(GetManagedStrmPrototype request) => ResolveAsync(request);

    public Task<object> Head(GetManagedStrmPrototype request) => ResolveAsync(request);

    private async Task<object> ResolveAsync(GetManagedStrmPrototype request)
    {
        var runtime = Plugin.Runtime;
        if (runtime?.IsInitialized != true)
            throw new ResourceNotFoundException(PluginStrings.ManagedPrototypeUnavailableError);
        var options = runtime.GetOptionsSnapshot();
        var registry = runtime.ManagedPrototypes;
        var resolver = runtime.Redirects;
        if (!options.Enabled || registry is null || resolver is null)
            throw new ResourceNotFoundException(PluginStrings.ManagedPrototypeUnavailableError);

        try
        {
            var operation = runtime.BeginOperation();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                Request.CancellationToken,
                operation.CancellationToken);
            var lease = await registry.ResolveAsync(
                    request.ManagedId,
                    Request.UserAgent,
                    resolver,
                    linked.Token)
                .ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var committed = runtime.TryCommit(
                operation.Generation,
                () => runtime.GetOptionsSnapshot().Enabled &&
                      registry.IsActive(request.ManagedId) &&
                      lease.IsValidAt(runtime.Clock.UtcNow),
                () =>
                {
                    Request.Response.StatusCode = 302;
                    AddSafeHeaders();
                    Request.Response.AddHeader("Location", lease.GetLocation());
                });
            if (!committed) throw new ManagedPrototypeUnavailableException();
            logger.Debug("STRM_BRIDGE_MANAGED_PROTOTYPE_RESOLVED");
            return string.Empty;
        }
        catch (RedirectThrottledException exception)
        {
            Request.Response.StatusCode = 429;
            AddSafeHeaders();
            AddRetryAfter(exception.RetryAfterSeconds);
            logger.Debug("STRM_BRIDGE_MANAGED_PROTOTYPE_THROTTLED");
            return string.Empty;
        }
        catch (RedirectSourceUnavailableException exception)
        {
            Request.Response.StatusCode = 503;
            AddSafeHeaders();
            AddRetryAfter(exception.RetryAfterSeconds);
            logger.Debug("STRM_BRIDGE_MANAGED_PROTOTYPE_SOURCE_UNAVAILABLE");
            return string.Empty;
        }
        catch (OperationCanceledException) when (!Request.CancellationToken.IsCancellationRequested)
        {
            throw new ResourceNotFoundException(PluginStrings.ManagedPrototypeUnavailableError);
        }
        catch (Exception exception) when (
            exception is ManagedPrototypeUnavailableException ||
            exception is RedirectRejectedException ||
            exception is InvalidOperationException ||
            exception is System.Net.Http.HttpRequestException)
        {
            logger.Debug("STRM_BRIDGE_MANAGED_PROTOTYPE_REJECTED");
            throw new ResourceNotFoundException(PluginStrings.ManagedPrototypeUnavailableError);
        }
    }

    private Runtime.PluginRuntime GetRuntime()
    {
        var runtime = Plugin.Runtime;
        if (runtime?.IsInitialized != true)
            throw new InvalidOperationException(PluginStrings.TaskInitializationError);
        return runtime;
    }

    private void RequireAdministrator()
    {
        var authorization = authorizationContext.GetAuthorizationInfo(Request);
        var user = authorization.User;
        if (user is not null && !user.Policy.IsAdministrator)
            throw new UnauthorizedAccessException(PluginStrings.AdministratorRequiredError);
        if (user is null && !HasAuthenticatedServerToken(authorization, Request))
            throw new UnauthorizedAccessException(PluginStrings.AuthenticatedUserRequiredError);
    }

    internal static bool HasAuthenticatedServerToken(AuthorizationInfo authorization, IRequest request)
    {
        if (authorization is null || request is null) return false;
        if (!string.IsNullOrWhiteSpace(authorization.Token)) return true;

        // Emby 4.9.5 validates server API keys in the route filter but leaves
        // AuthorizationInfo.Token empty for that credential type. These DTOs
        // deliberately remain authenticated routes, so only the standard
        // header is accepted here; query-string credentials are not supported.
        return !string.IsNullOrWhiteSpace(request.Headers.Get("X-Emby-Token"));
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
}
