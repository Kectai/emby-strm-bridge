using System;
using System.IO;
using System.Linq;
using System.Threading;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Localization;
using Emby.StrmBridge.Runtime;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Common;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge;

public sealed class Plugin : BasePluginSimpleUI<PluginConfiguration>
{
    public static readonly Guid PluginId = new("08a67570-4963-4768-964f-cdde9f53a066");
    private readonly ILogger logger;
    private readonly object detectedRedirectHostCatalogSync = new();
    private string[] detectedRedirectHostCatalog = Array.Empty<string>();
    private ILibraryManager? libraryManager;

    public Plugin(IApplicationHost applicationHost, ILogManager logManager)
        : base(applicationHost)
    {
        Instance = this;
        Runtime = new PluginRuntime();
        logger = logManager.GetLogger(Name);
        var options = GetOptions();
        if (options.Normalize()) SaveOptions(options);
        SetDetectedRedirectHostCatalog(options.DetectedRedirectHostCatalog);
        Runtime.UpdateOptions(options, invalidateSensitiveState: false);
        logger.Info("STRM Bridge plugin is loading.");
    }

    public static Plugin? Instance { get; private set; }

    public static PluginRuntime? Runtime { get; private set; }

    public override string Name => "STRM Bridge";

    public override string Description => PluginStrings.PluginDescription;

    public override Guid Id => PluginId;

    public PluginConfiguration Options => GetOptions();

    internal void AttachLibraryManager(ILibraryManager? manager) => Volatile.Write(ref libraryManager, manager);

    internal EditorSelectOption[]? GetLibraryOptions()
    {
        var manager = Volatile.Read(ref libraryManager);
        if (manager is null) return null;
        try
        {
            var collectionFolders = manager.RootFolder?.CollectionFolders
                ?? Array.Empty<MediaBrowser.Controller.Entities.Folder>();
            var options = CreateLibraryOptions(collectionFolders);
            if (options.Length > 0) return options;
        }
        catch (Exception exception)
        {
            logger.Warn("STRM Bridge could not enumerate root media libraries for configuration. error=" +
                        exception.GetType().Name);
        }

        try
        {
            return CreateLibraryOptions(manager.GetVirtualFolders());
        }
        catch (Exception exception)
        {
            logger.Warn("STRM Bridge could not enumerate virtual media libraries for configuration. error=" +
                        exception.GetType().Name);
            return null;
        }
    }

    internal static EditorSelectOption[] CreateLibraryOptions(
        System.Collections.Generic.IEnumerable<MediaBrowser.Controller.Entities.Folder>? folders)
    {
        return (folders ?? Array.Empty<MediaBrowser.Controller.Entities.Folder>())
                .Where(folder => folder.Id != Guid.Empty && !string.IsNullOrWhiteSpace(folder.Name))
                .GroupBy(folder => folder.Id)
                .Select(group => group.First())
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .Select(folder => new EditorSelectOption
                {
                    Value = folder.Id.ToString("N"),
                    Name = folder.Name,
                    IsEnabled = true,
                })
                .ToArray();
    }

    internal static EditorSelectOption[] CreateLibraryOptions(System.Collections.Generic.IEnumerable<VirtualFolderInfo>? folders)
    {
        return (folders ?? Array.Empty<VirtualFolderInfo>())
            .Select(folder => new
            {
                Folder = folder,
                Id = ParseVirtualFolderId(folder),
            })
            .Where(value => value.Id != Guid.Empty && !string.IsNullOrWhiteSpace(value.Folder.Name))
            .GroupBy(value => value.Id)
            .Select(group => group.First())
            .OrderBy(value => value.Folder.Name, StringComparer.OrdinalIgnoreCase)
            .Select(value => new EditorSelectOption
            {
                Value = value.Id.ToString("N"),
                Name = value.Folder.Name,
                IsEnabled = true,
            })
            .ToArray();
    }

    internal static EditorSelectOption[] CreateDetectedRedirectHostOptions(
        System.Collections.Generic.IEnumerable<string>? detectedHosts,
        System.Collections.Generic.IEnumerable<string>? allowedHosts)
    {
        var allowed = (allowedHosts ?? Array.Empty<string>()).ToArray();
        return (detectedHosts ?? Array.Empty<string>())
            .Select(host => new
            {
                Host = host,
                IsTrusted = PluginConfiguration.IsHostAllowed(host, allowed),
            })
            .Select(value => new EditorSelectOption
            {
                Value = value.Host,
                Name = value.IsTrusted ? value.Host + PluginStrings.TrustedHostSuffix : value.Host,
                IsEnabled = !value.IsTrusted,
            })
            .ToArray();
    }

    internal static string[] MergeDetectedRedirectHostCatalog(
        System.Collections.Generic.IEnumerable<string>? currentHosts,
        System.Collections.Generic.IEnumerable<EditorSelectOption>? rememberedOptions)
    {
        return (currentHosts ?? Array.Empty<string>())
            .Concat((rememberedOptions ?? Array.Empty<EditorSelectOption>())
                .Select(option => option.Value))
            .Select(PluginConfiguration.NormalizeHost)
            .Where(PluginConfiguration.IsValidExactHost)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(PluginRuntime.MaximumDetectedRedirectHosts)
            .ToArray();
    }

    private static Guid ParseVirtualFolderId(VirtualFolderInfo folder)
    {
        if (Guid.TryParse(folder.Guid, out var guid)) return guid;
        if (Guid.TryParse(folder.ItemId, out guid)) return guid;
        return Guid.TryParse(folder.Id, out guid) ? guid : Guid.Empty;
    }

    internal System.Collections.Generic.HashSet<Guid>? GetLibraryIds()
    {
        var options = GetLibraryOptions();
        return options is null
            ? null
            : new System.Collections.Generic.HashSet<Guid>(
                options.Where(option => Guid.TryParse(option.Value, out _))
                    .Select(option => Guid.Parse(option.Value)));
    }

    protected override bool OnOptionsSaving(PluginConfiguration options)
    {
        var detectedHosts = MergeDetectedRedirectHostCatalog(
            Runtime?.GetDetectedRedirectHosts(),
            GetDetectedRedirectHostCatalog().Select(host => new EditorSelectOption { Value = host }));
        options.DetectedRedirectHostCatalog = detectedHosts;
        options.MergeDetectedRedirectHostsToTrust(detectedHosts);
        options.Normalize();
        PopulateUiPresentationData(options);
        return base.OnOptionsSaving(options);
    }

    protected override PluginConfiguration OnBeforeShowUI(PluginConfiguration options)
    {
        return PopulateUiPresentationData(base.OnBeforeShowUI(options));
    }

    private PluginConfiguration PopulateUiPresentationData(PluginConfiguration prepared)
    {
        prepared.Normalize();
        var libraries = GetLibraryOptions() ?? Array.Empty<EditorSelectOption>();
        prepared.AvailableLibraries = libraries;
        prepared.IncludedLibraries = string.Join(",", prepared.IncludedLibraryIds);

        var detectedHosts = prepared.Enabled
            ? MergeDetectedRedirectHostCatalog(
                Runtime?.GetDetectedRedirectHosts(),
                GetDetectedRedirectHostCatalog().Select(host => new EditorSelectOption { Value = host }))
            : Array.Empty<string>();
        prepared.DetectedRedirectHostCatalog = detectedHosts;
        prepared.AvailableDetectedRedirectHosts = CreateDetectedRedirectHostOptions(
            detectedHosts,
            prepared.AllowedRedirectHosts);
        prepared.DetectedRedirectHostsToTrust = string.Empty;
        return prepared;
    }

    protected override void OnOptionsSaved(PluginConfiguration options)
    {
        SetDetectedRedirectHostCatalog(options.DetectedRedirectHostCatalog);
        var runtime = Runtime;
        var previousHosts = runtime?.GetOptionsSnapshot().AllowedRedirectHosts ?? Array.Empty<string>();
        runtime?.UpdateOptions(options, invalidateSensitiveState: true);
        var addedRedirectTrust = (options.AllowedRedirectHosts ?? Array.Empty<string>())
            .Except(previousHosts, StringComparer.OrdinalIgnoreCase)
            .Any();
        var redirectTrustChanged = !previousHosts.SequenceEqual(
            options.AllowedRedirectHosts ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        if (runtime?.ExtractionState is not null && redirectTrustChanged)
        {
            try
            {
                runtime.ExtractionState.ClearFailures();
                runtime.ExtractionState.Flush();
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException ||
                exception is InvalidDataException || exception is System.Runtime.Serialization.SerializationException)
            {
                logger.Warn("STRM Bridge could not clear extraction backoff after redirect trust changed. error=" +
                            exception.GetType().Name);
            }
        }
        if (addedRedirectTrust && options.Enabled && options.IncludedLibraryIds.Length > 0)
            runtime?.Extraction?.QueueTrustRetry();
        logger.Info("STRM Bridge options were updated.");
        PopulateUiPresentationData(options);
    }

    private string[] GetDetectedRedirectHostCatalog()
    {
        lock (detectedRedirectHostCatalogSync) return detectedRedirectHostCatalog.ToArray();
    }

    private void SetDetectedRedirectHostCatalog(
        System.Collections.Generic.IEnumerable<string>? detectedHosts)
    {
        var normalized = MergeDetectedRedirectHostCatalog(
            detectedHosts,
            Array.Empty<EditorSelectOption>());
        lock (detectedRedirectHostCatalogSync) detectedRedirectHostCatalog = normalized;
    }

    public override void OnUninstalling()
    {
        var dataDirectory = Runtime?.DataDirectory;
        Runtime?.Dispose();
        if (!string.IsNullOrEmpty(dataDirectory) &&
            string.Equals(Path.GetFileName(dataDirectory), "Emby.StrmBridge", StringComparison.Ordinal) &&
            Directory.Exists(dataDirectory))
        {
            var attributes = File.GetAttributes(dataDirectory);
            Directory.Delete(dataDirectory, recursive: (attributes & FileAttributes.ReparsePoint) == 0);
        }
        base.OnUninstalling();
    }
}
