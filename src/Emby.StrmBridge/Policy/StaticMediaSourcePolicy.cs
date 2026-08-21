using System;
using Emby.StrmBridge.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Emby.StrmBridge.Policy;

internal static class StaticMediaSourcePolicy
{
    public static bool Matches(IMediaSourceManager mediaSourceManager, BaseItem item, SourceIdentity source)
    {
        if (mediaSourceManager is null) throw new ArgumentNullException(nameof(mediaSourceManager));
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (source is null) throw new ArgumentNullException(nameof(source));
        try
        {
            return mediaSourceManager.GetStaticMediaSources(
                    item,
                    enablePathSubstitution: false,
                    fillChapters: false,
                    deviceProfile: null,
                    user: null)
                .Exists(candidate =>
                    !candidate.RequiresOpening &&
                    string.IsNullOrEmpty(candidate.OpenToken) &&
                    (candidate.RequiredHttpHeaders is null || candidate.RequiredHttpHeaders.Count == 0) &&
                    Uri.TryCreate(candidate.Path, UriKind.Absolute, out var uri) &&
                    Uri.Compare(
                        uri,
                        source.SourceUri,
                        UriComponents.AbsoluteUri,
                        UriFormat.SafeUnescaped,
                        StringComparison.Ordinal) == 0);
        }
        catch
        {
            return false;
        }
    }
}
