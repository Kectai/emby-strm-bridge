using System;

namespace Emby.StrmBridge.Domain;

internal static class TechnicalMediaInfo
{
    internal static readonly long MinimumRuntimeTicks = TimeSpan.FromSeconds(1).Ticks;

    public static bool IsComplete(string? container, long? runTimeTicks, bool hasInternalVideo) =>
        hasInternalVideo &&
        runTimeTicks.HasValue &&
        runTimeTicks.Value >= MinimumRuntimeTicks &&
        !string.IsNullOrWhiteSpace(container) &&
        !string.Equals(container, "strm", StringComparison.OrdinalIgnoreCase);
}
