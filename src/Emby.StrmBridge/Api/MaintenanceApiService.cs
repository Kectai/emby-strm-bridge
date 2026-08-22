using System;
using System.Threading.Tasks;
using Emby.StrmBridge.Localization;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Api;

public sealed class MaintenanceApiService : IService, IRequiresRequest
{
    private readonly IAuthorizationContext authorizationContext;

    public MaintenanceApiService(IAuthorizationContext authorizationContext) =>
        this.authorizationContext = authorizationContext ?? throw new ArgumentNullException(nameof(authorizationContext));

    public IRequest Request { get; set; } = null!;

    public async Task<object> Post(RunStrmBridgeExtraction request)
    {
        RequireAdministrator();
        var coordinator = GetCoordinator();
        Guid? itemId = null;
        if (!string.IsNullOrWhiteSpace(request.ItemId))
        {
            if (!Guid.TryParse(request.ItemId, out var parsedItemId))
                throw new ArgumentException(PluginStrings.InvalidItemIdError, nameof(request.ItemId));
            itemId = parsedItemId;
        }
        if (!string.IsNullOrWhiteSpace(request.LibraryId) && !Guid.TryParse(request.LibraryId, out _))
            throw new ArgumentException(PluginStrings.InvalidLibraryIdError, nameof(request.LibraryId));
        return await coordinator.ExtractAsync(
                request.Force,
                null,
                Request.CancellationToken,
                itemId,
                request.LibraryId)
            .ConfigureAwait(false);
    }

    public async Task<object> Post(RunStrmBridgeRestore request)
    {
        RequireAdministrator();
        return await GetCoordinator().RestoreAsync(null, Request.CancellationToken).ConfigureAwait(false);
    }

    public async Task<object> Post(RunStrmBridgeCleanup request)
    {
        RequireAdministrator();
        var removed = await GetCoordinator().CleanupAsync(Request.CancellationToken).ConfigureAwait(false);
        return new { Removed = removed };
    }

    public async Task<object> Post(RunStrmBridgeClear request)
    {
        RequireAdministrator();
        return await GetCoordinator()
            .ClearStoredMediaInfoAsync(null, Request.CancellationToken)
            .ConfigureAwait(false);
    }

    public object Get(GetStrmBridgeHealth request)
    {
        RequireAdministrator();
        return CreateHealth();
    }

    public object Get(GetStrmBridgeDiagnostics request)
    {
        RequireAdministrator();
        var runtime = Plugin.Runtime ?? throw new InvalidOperationException(PluginStrings.TaskInitializationError);
        var options = runtime.GetOptionsSnapshot();
        var health = runtime.GetPlaybackHealth();
        return new
        {
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            Enabled = options.Enabled,
            PlaybackMode = options.PlaybackMode.ToString(),
            SelectedLibraryCount = options.IncludedLibraryIds.Length,
            TrustedHostRuleCount = options.AllowedRedirectHosts.Length,
            DetectedHostCount = runtime.GetDetectedRedirectHosts().Length,
            PatchStatus = health.PatchStatus.ToString(),
            health.HostAbi,
            health.RuntimeGeneration,
            health.TicketCount,
            health.ActiveRelayCount,
        };
    }

    private static object CreateHealth()
    {
        var runtime = Plugin.Runtime ?? throw new InvalidOperationException(PluginStrings.TaskInitializationError);
        var options = runtime.GetOptionsSnapshot();
        var health = runtime.GetPlaybackHealth();
        return new
        {
            Enabled = options.Enabled,
            PlaybackMode = options.PlaybackMode.ToString(),
            PatchStatus = health.PatchStatus.ToString(),
            health.HostAbi,
            health.RuntimeGeneration,
            health.TicketCount,
            health.ActiveRelayCount,
        };
    }

    private Extraction.ExtractionCoordinator GetCoordinator() =>
        Plugin.Runtime?.Extraction ?? throw new InvalidOperationException(PluginStrings.TaskInitializationError);

    private void RequireAdministrator()
    {
        var user = authorizationContext.GetAuthorizationInfo(Request).User
            ?? throw new UnauthorizedAccessException(PluginStrings.AuthenticatedUserRequiredError);
        if (!user.Policy.IsAdministrator)
            throw new UnauthorizedAccessException(PluginStrings.AdministratorRequiredError);
    }
}
