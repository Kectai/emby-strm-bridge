using System;
using System.IO;
using Emby.StrmBridge.Extraction;
using Emby.StrmBridge.Playback;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Runtime;

public sealed class PluginEntryPoint : IServerEntryPoint, IDisposable
{
    private readonly IServerApplicationHost applicationHost;
    private readonly IServerApplicationPaths applicationPaths;
    private readonly IMediaSourceManager mediaSourceManager;
    private readonly ILibraryManager libraryManager;
    private readonly IItemRepository itemRepository;
    private readonly INotificationManager notificationManager;
    private readonly IActivityManager activityManager;
    private readonly IAuthorizationContext authorizationContext;
    private readonly IHttpResultFactory resultFactory;
    private readonly ILogManager logManager;
    private MaintenanceService? maintenance;
    private bool disposed;

    public PluginEntryPoint(
        IServerApplicationHost applicationHost,
        IServerApplicationPaths applicationPaths,
        IMediaSourceManager mediaSourceManager,
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        INotificationManager notificationManager,
        IActivityManager activityManager,
        IAuthorizationContext authorizationContext,
        IHttpResultFactory resultFactory,
        ILogManager logManager)
    {
        this.applicationHost = applicationHost;
        this.applicationPaths = applicationPaths;
        this.mediaSourceManager = mediaSourceManager;
        this.libraryManager = libraryManager;
        this.itemRepository = itemRepository;
        this.notificationManager = notificationManager;
        this.activityManager = activityManager;
        this.authorizationContext = authorizationContext;
        this.resultFactory = resultFactory;
        this.logManager = logManager;
    }

    public void Run()
    {
        var runtime = Plugin.Runtime ?? throw new InvalidOperationException("STRM Bridge runtime is unavailable.");
        Plugin.Instance?.AttachLibraryManager(libraryManager);
        runtime.Initialize(applicationPaths.ConfigurationDirectoryPath);
        runtime.InitializeFastSeek(logManager.GetLogger(Plugin.Instance?.Name ?? "STRM Bridge"));
        var coordinator = new ExtractionCoordinator(
            runtime,
            libraryManager,
            mediaSourceManager,
            itemRepository,
            logManager,
            notificationManager,
            activityManager,
            applicationHost);
        runtime.Extraction = coordinator;
        try
        {
            var playbackProcessor = new PlaybackInfoProcessor(
                runtime,
                libraryManager,
                mediaSourceManager,
                authorizationContext,
                logManager);
            var nativeStreamProcessor = new NativeVideoStreamProcessor(
                runtime,
                libraryManager,
                mediaSourceManager,
                authorizationContext,
                resultFactory,
                logManager);
            var transcodeInputProcessor = new TranscodeInputProcessor(
                runtime,
                libraryManager,
                mediaSourceManager,
                applicationHost,
                logManager);
            var ffmpegCommandProcessor = new FfmpegCommandProcessor(runtime, logManager);
            var playbackPatch = new HarmonyPatchHost(
                playbackProcessor,
                nativeStreamProcessor,
                transcodeInputProcessor,
                ffmpegCommandProcessor,
                logManager);
            runtime.SetPlaybackHealth(playbackPatch.Install(), playbackPatch.HostAbi);
            runtime.AttachPlaybackPatch(playbackPatch);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException || exception is FileLoadException ||
            exception is InvalidDataException || exception is BadImageFormatException)
        {
            runtime.SetPlaybackHealth(PlaybackPatchStatus.Failed, "dependency-unavailable");
            logManager.GetLogger(Plugin.Instance?.Name ?? "STRM Bridge")
                .Warn("STRM_BRIDGE_PATCH_DEPENDENCY_FAILED error=" + exception.GetType().Name);
        }
        try
        {
            var subtitleLogger = logManager.GetLogger(Plugin.Instance?.Name ?? "STRM Bridge");
            var subtitles = new Emby.StrmBridge.Subtitles.SubtitleCoordinator(runtime, applicationHost, applicationPaths, subtitleLogger);
            runtime.Subtitles = subtitles;
            var subtitleProcessor = new Emby.StrmBridge.Subtitles.SubtitleRequestProcessor(runtime, libraryManager,
                mediaSourceManager, authorizationContext,
                applicationHost.TryResolve<IAuthService>() ?? throw new InvalidOperationException("Subtitle authentication unavailable."),
                resultFactory, subtitles, subtitleLogger);
            runtime.SubtitleRequests = subtitleProcessor;
            var subtitlePatch = new Emby.StrmBridge.Subtitles.SubtitlePatchHost(runtime, resultFactory, subtitleLogger,
                new Emby.StrmBridge.Subtitles.SharedSubtitleNegotiation(runtime, libraryManager, mediaSourceManager));
            runtime.SubtitlePatch = subtitlePatch;
            subtitles.Status = subtitlePatch.Install() ? "Ready" : "NativeOnly";
        }
        catch (Exception exception)
        {
            logManager.GetLogger(Plugin.Instance?.Name ?? "STRM Bridge")
                .Warn("STRM_BRIDGE_SUBTITLE_UNAVAILABLE error=" + exception.GetType().Name);
        }
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
