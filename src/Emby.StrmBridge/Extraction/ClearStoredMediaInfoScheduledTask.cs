using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Localization;
using MediaBrowser.Model.Tasks;

namespace Emby.StrmBridge.Extraction;

public sealed class ClearStoredMediaInfoScheduledTask : IScheduledTask
{
    public string Name => PluginStrings.ClearStoredMediaInfoTaskName;

    public string Key => "StrmBridgeClearStoredMediaInfo";

    public string Description => PluginStrings.ClearStoredMediaInfoTaskDescription;

    public string Category => PluginStrings.ScheduledTaskCategory;

    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var coordinator = Plugin.Runtime?.Extraction
            ?? throw new InvalidOperationException(PluginStrings.TaskInitializationError);
        return coordinator.ClearStoredMediaInfoAsync(progress, cancellationToken);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
