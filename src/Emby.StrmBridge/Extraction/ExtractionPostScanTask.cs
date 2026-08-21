using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace Emby.StrmBridge.Extraction;

public sealed class ExtractionPostScanTask : ILibraryPostScanTask
{
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = Plugin.Runtime;
        if (runtime?.GetOptionsSnapshot().ExtractAfterLibraryScan == true)
            runtime.Extraction?.QueuePostScan();
        progress?.Report(100);
        return Task.CompletedTask;
    }
}
