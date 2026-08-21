using System;
using System.Globalization;
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
    public static string EditorDescription => Get(nameof(EditorDescription));
    public static string Enabled => Get(nameof(Enabled));
    public static string EnablePlaybackSource => Get(nameof(EnablePlaybackSource));
    public static string EnablePlaybackSourceDescription => Get(nameof(EnablePlaybackSourceDescription));
    public static string ExtractAfterLibraryScan => Get(nameof(ExtractAfterLibraryScan));
    public static string OnlyMissingMediaInfo => Get(nameof(OnlyMissingMediaInfo));
    public static string OnlyMissingMediaInfoDescription => Get(nameof(OnlyMissingMediaInfoDescription));
    public static string EnablePersistence => Get(nameof(EnablePersistence));
    public static string MaximumExtractionConcurrency => Get(nameof(MaximumExtractionConcurrency));
    public static string ExtractionTimeoutSeconds => Get(nameof(ExtractionTimeoutSeconds));
    public static string IncludedLibraryIds => Get(nameof(IncludedLibraryIds));
    public static string IncludedLibraryIdsDescription => Get(nameof(IncludedLibraryIdsDescription));
    public static string DetectedRedirectHosts => Get(nameof(DetectedRedirectHosts));
    public static string DetectedRedirectHostsDescription => Get(nameof(DetectedRedirectHostsDescription));
    public static string TrustedHostSuffix => Get(nameof(TrustedHostSuffix));
    public static string AllowedRedirectHosts => Get(nameof(AllowedRedirectHosts));
    public static string AllowedRedirectHostsDescription => Get(nameof(AllowedRedirectHostsDescription));
    public static string AwaitingApprovalNotificationTitle => Get(nameof(AwaitingApprovalNotificationTitle));
    public static string AwaitingApprovalNotificationDescription => Get(nameof(AwaitingApprovalNotificationDescription));
    public static string RetryCompletedNotificationTitle => Get(nameof(RetryCompletedNotificationTitle));
    public static string RetryCompletedNotificationDescription => Get(nameof(RetryCompletedNotificationDescription));
    public static string ConcurrencyValidation => Get(nameof(ConcurrencyValidation));
    public static string TimeoutValidation => Get(nameof(TimeoutValidation));
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
        var resources = SelectResources(CultureInfo.CurrentUICulture);
        return resources.GetString(key, CultureInfo.InvariantCulture) ??
               EnglishResources.GetString(key, CultureInfo.InvariantCulture) ??
               throw new InvalidOperationException("A required plugin localization resource is missing.");
    }

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
