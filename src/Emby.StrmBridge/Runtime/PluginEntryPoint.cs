using System;
using Emby.StrmBridge.Extraction;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Runtime;

public sealed class PluginEntryPoint : IServerEntryPoint, IDisposable
{
    private readonly IServerApplicationPaths applicationPaths;
    private readonly IMediaSourceManager mediaSourceManager;
    private readonly ILibraryManager libraryManager;
    private readonly IItemRepository itemRepository;
    private readonly INotificationManager notificationManager;
    private readonly IActivityManager activityManager;
    private readonly ILogManager logManager;
    private MaintenanceService? maintenance;
    private bool disposed;

    public PluginEntryPoint(
        IServerApplicationPaths applicationPaths,
        IMediaSourceManager mediaSourceManager,
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        INotificationManager notificationManager,
        IActivityManager activityManager,
        ILogManager logManager)
    {
        this.applicationPaths = applicationPaths;
        this.mediaSourceManager = mediaSourceManager;
        this.libraryManager = libraryManager;
        this.itemRepository = itemRepository;
        this.notificationManager = notificationManager;
        this.activityManager = activityManager;
        this.logManager = logManager;
    }

    public void Run()
    {
        var runtime = Plugin.Runtime ?? throw new InvalidOperationException("STRM Bridge runtime is unavailable.");
        Plugin.Instance?.AttachLibraryManager(libraryManager);
        runtime.Initialize(applicationPaths.ConfigurationDirectoryPath);
        var coordinator = new ExtractionCoordinator(
            runtime,
            libraryManager,
            mediaSourceManager,
            itemRepository,
            logManager,
            notificationManager,
            activityManager);
        runtime.Extraction = coordinator;
        maintenance = new MaintenanceService(runtime, logManager);
        runtime.Maintenance = maintenance;
        maintenance.Start();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        maintenance?.Dispose();
        maintenance = null;
        Plugin.Instance?.AttachLibraryManager(null);
        Plugin.Runtime?.Dispose();
    }
}
