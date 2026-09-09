using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Emby.StrmBridge.Policy;

internal static class PlaybackItemPolicy
{
    public static bool IsVideo(BaseItem? item) =>
        item is not null &&
        string.Equals(item.MediaType, MediaType.Video, StringComparison.OrdinalIgnoreCase);
}
