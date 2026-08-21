using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Localization;
using MediaBrowser.Model.Tasks;

namespace Emby.StrmBridge.Extraction;

public sealed class ExtractionScheduledTask : IScheduledTask
{
    public string Name => PluginStrings.ScheduledTaskName;

    public string Key => "StrmBridgeExtractMissing";

    public string Description => PluginStrings.ScheduledTaskDescription;

    public string Category => PluginStrings.ScheduledTaskCategory;

    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var coordinator = Plugin.Runtime?.Extraction
            ?? throw new InvalidOperationException(PluginStrings.TaskInitializationError);
        return coordinator.ExtractAsync(force: false, progress, cancellationToken);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = "IntervalTrigger",
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
        };
    }
}
