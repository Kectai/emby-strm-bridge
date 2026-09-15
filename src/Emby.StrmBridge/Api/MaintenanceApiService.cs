using System;
using System.Reflection;
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
            PluginVersion = GetPluginVersion(),
            BuildId = GetBuildId(),
            Enabled = options.Enabled,
            SubtitlesEnabled = options.EnableSubtitles,
            SubtitleStatus = runtime.Subtitles?.Status == "Ready" && runtime.SubtitlePatch?.CanServe != true
                ? "Unavailable" : runtime.Subtitles?.Status ?? "Unavailable",
            SubtitleJobs = runtime.Subtitles?.ActiveJobs ?? 0,
            SubtitleWebResourceStatus = runtime.SubtitlePatch?.ResourceStatus ?? "Unavailable",
            PlaybackMode = options.PlaybackMode.ToString(),
            SelectedLibraryCount = options.IncludedLibraryIds.Length,
            TrustedHostRuleCount = options.AllowedRedirectHosts.Length,
            DetectedHostCount = runtime.GetDetectedRedirectHosts().Length,
            PatchStatus = health.PatchStatus.ToString(),
            health.HostAbi,
            health.PrefixDiagnosticsAvailable,
            health.ExternalPrefixCount,
            health.NativePrefixInstalled,
            health.NativePrefixPriority,
            health.HighestExternalPrefixPriority,
            health.NativePrefixUncontended,
            health.NativePrefixPriorityStrictlyHigher,
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
            PluginVersion = GetPluginVersion(),
            BuildId = GetBuildId(),
            Enabled = options.Enabled,
            SubtitlesEnabled = options.EnableSubtitles,
            SubtitleStatus = runtime.Subtitles?.Status == "Ready" && runtime.SubtitlePatch?.CanServe != true
                ? "Unavailable" : runtime.Subtitles?.Status ?? "Unavailable",
            SubtitleJobs = runtime.Subtitles?.ActiveJobs ?? 0,
            SubtitleWebResourceStatus = runtime.SubtitlePatch?.ResourceStatus ?? "Unavailable",
            PlaybackMode = options.PlaybackMode.ToString(),
            PatchStatus = health.PatchStatus.ToString(),
            health.HostAbi,
            health.PrefixDiagnosticsAvailable,
            health.ExternalPrefixCount,
            health.NativePrefixInstalled,
            health.NativePrefixPriority,
            health.HighestExternalPrefixPriority,
            health.NativePrefixUncontended,
            health.NativePrefixPriorityStrictlyHigher,
            health.RuntimeGeneration,
            health.TicketCount,
            health.ActiveRelayCount,
        };
    }

    private static string GetPluginVersion() =>
        typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ??
        typeof(Plugin).Assembly.GetName().Version?.ToString(3) ??
        "unknown";

    private static string GetBuildId() =>
        typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12);

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
