using System;
using System.Threading;
using Emby.StrmBridge.Playback;
using MediaBrowser.Model.Logging;

namespace Emby.StrmBridge.Runtime;

public sealed class MaintenanceService : IDisposable
{
    private readonly PluginRuntime runtime;
    private readonly ILogger logger;
    private Timer? timer;
    private int running;
    private int stopping;

    public MaintenanceService(PluginRuntime runtime, ILogManager logManager)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        logger = logManager.GetLogger(Plugin.Instance?.Name ?? "STRM Bridge");
    }

    public void Start() => timer ??= new Timer(_ => Run(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    private void Run()
    {
        if (Volatile.Read(ref stopping) != 0) return;
        if (Interlocked.Exchange(ref running, 1) != 0) return;
        try
        {
            if (Volatile.Read(ref stopping) != 0) return;
            runtime.Tickets.RemoveExpired();
            runtime.Redirects?.RemoveExpired();
            runtime.MediaInfoStore?.RemoveTemporaryFiles();
        }
        catch (Exception)
        {
            logger.Debug("STRM_BRIDGE_MAINTENANCE_FAILED");
        }
        finally { Interlocked.Exchange(ref running, 0); }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref stopping, 1);
        var current = Interlocked.Exchange(ref timer, null);
        if (current is null) return;
        using var drained = new ManualResetEvent(false);
        if (current.Dispose(drained)) drained.WaitOne();
    }
}
