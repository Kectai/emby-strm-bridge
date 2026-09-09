using System;
using System.Globalization;
using System.Net;
using System.Resources;

namespace Emby.StrmBridge.Localization;

public static class PluginStrings
{
    private static readonly ResourceManager EnglishResources = new(
        "Emby.StrmBridge.Localization.PluginStrings",
        typeof(PluginStrings).Assembly);
    private static readonly ResourceManager SimplifiedChineseResources = new(
        "Emby.StrmBridge.Localization.PluginStringsZhHans",
        typeof(PluginStrings).Assembly);
    private static readonly ResourceManager TraditionalChineseResources = new(
        "Emby.StrmBridge.Localization.PluginStringsZhHant",
        typeof(PluginStrings).Assembly);

    public static string PluginDescription => Get(nameof(PluginDescription));
    public static string EditorTitle => Get(nameof(EditorTitle));
    public static string EditorDescription => GetSelectableDescription(nameof(EditorDescription));
    public static string Enabled => Get(nameof(Enabled));
    public static string PlaybackMode => Get(nameof(PlaybackMode));
    public static string PlaybackModeDescription => GetSelectableDescription(nameof(PlaybackModeDescription));
    public static string EnableFastSeek => Get(nameof(EnableFastSeek));
    public static string EnableFastSeekDescription => GetSelectableDescription(nameof(EnableFastSeekDescription));
    public static string ExtractAfterLibraryScan => Get(nameof(ExtractAfterLibraryScan));
    public static string OnlyMissingMediaInfo => Get(nameof(OnlyMissingMediaInfo));
    public static string OnlyMissingMediaInfoDescription => GetSelectableDescription(nameof(OnlyMissingMediaInfoDescription));
    public static string EnablePersistence => Get(nameof(EnablePersistence));
    public static string EnablePersistenceDescription =>
        GetSelectableDescription(nameof(EnablePersistenceDescription));
    public static string MaximumExtractionConcurrency => Get(nameof(MaximumExtractionConcurrency));
    public static string ExtractionTimeoutSeconds => Get(nameof(ExtractionTimeoutSeconds));
    public static string GatewayTimeoutSeconds => Get(nameof(GatewayTimeoutSeconds));
    public static string DirectRedirectCacheSeconds => Get(nameof(DirectRedirectCacheSeconds));
    public static string DirectRedirectCacheSecondsDescription =>
        GetSelectableDescription(nameof(DirectRedirectCacheSecondsDescription));
    public static string RedirectHopLimit => Get(nameof(RedirectHopLimit));
    public static string RelayConcurrency => Get(nameof(RelayConcurrency));
    public static string IncludedLibraryIds => Get(nameof(IncludedLibraryIds));
    public static string IncludedLibraryIdsDescription => GetSelectableDescription(nameof(IncludedLibraryIdsDescription));
    public static string DetectedRedirectHosts => Get(nameof(DetectedRedirectHosts));
    public static string DetectedRedirectHostsDescription => GetSelectableDescription(nameof(DetectedRedirectHostsDescription));
    public static string TrustedHostSuffix => Get(nameof(TrustedHostSuffix));
    public static string AllowedRedirectHosts => Get(nameof(AllowedRedirectHosts));
    public static string AllowedRedirectHostsDescription => GetSelectableDescription(nameof(AllowedRedirectHostsDescription));
    public static string AwaitingApprovalNotificationTitle => Get(nameof(AwaitingApprovalNotificationTitle));
    public static string AwaitingApprovalNotificationDescription => Get(nameof(AwaitingApprovalNotificationDescription));
    public static string RetryCompletedNotificationTitle => Get(nameof(RetryCompletedNotificationTitle));
    public static string RetryCompletedNotificationDescription => Get(nameof(RetryCompletedNotificationDescription));
    public static string ConcurrencyValidation => Get(nameof(ConcurrencyValidation));
    public static string TimeoutValidation => Get(nameof(TimeoutValidation));
    public static string GatewayValidation => Get(nameof(GatewayValidation));
    public static string DirectRedirectCacheValidation => Get(nameof(DirectRedirectCacheValidation));
    public static string LibraryValidation => Get(nameof(LibraryValidation));
    public static string RedirectHostValidation => Get(nameof(RedirectHostValidation));
    public static string RedirectHostCapacityValidation => Get(nameof(RedirectHostCapacityValidation));
    public static string ScheduledTaskName => Get(nameof(ScheduledTaskName));
    public static string ScheduledTaskDescription => Get(nameof(ScheduledTaskDescription));
    public static string ClearStoredMediaInfoTaskName => Get(nameof(ClearStoredMediaInfoTaskName));
    public static string ClearStoredMediaInfoTaskDescription => Get(nameof(ClearStoredMediaInfoTaskDescription));
    public static string ScheduledTaskCategory => Get(nameof(ScheduledTaskCategory));
    public static string TaskInitializationError => Get(nameof(TaskInitializationError));
    public static string InvalidItemIdError => Get(nameof(InvalidItemIdError));
    public static string InvalidLibraryIdError => Get(nameof(InvalidLibraryIdError));
    public static string AuthenticatedUserRequiredError => Get(nameof(AuthenticatedUserRequiredError));
    public static string AdministratorRequiredError => Get(nameof(AdministratorRequiredError));

    private static string Get(string key)
    {
        return Get(SelectResources(CultureInfo.CurrentUICulture), key);
    }

    internal static string GetText(string key, CultureInfo culture) =>
        Get(SelectResources(culture), key);

    private static string Get(ResourceManager resources, string key) =>
        resources.GetString(key, CultureInfo.InvariantCulture) ??
        EnglishResources.GetString(key, CultureInfo.InvariantCulture) ??
        throw new InvalidOperationException("A required plugin localization resource is missing.");

    private static string GetSelectableDescription(string key) =>
        WrapSelectableDescription(Get(key));

    private static string WrapSelectableDescription(string text) =>
        "<span style=\"-webkit-user-select:text;user-select:text\">" +
        WebUtility.HtmlEncode(text) +
        "</span>";

    private static ResourceManager SelectResources(CultureInfo culture)
    {
        if (!string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
            return EnglishResources;
        var name = culture.Name;
        if (name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase))
            return TraditionalChineseResources;
        return SimplifiedChineseResources;
    }

}
