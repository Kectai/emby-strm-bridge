using System;
using Emby.StrmBridge.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

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
                .Exists(candidate => Matches(candidate, source));
        }
        catch
        {
            return false;
        }
    }

    public static bool Matches(MediaSourceInfo mediaSource, SourceIdentity source)
    {
        if (mediaSource is null) throw new ArgumentNullException(nameof(mediaSource));
        if (source is null) throw new ArgumentNullException(nameof(source));
        return HasStaticTransportShape(mediaSource) &&
               MatchesUri(mediaSource.Path, source.SourceUri);
    }

    public static bool MatchesPlaybackSource(MediaSourceInfo mediaSource, SourceIdentity source)
    {
        if (mediaSource is null) throw new ArgumentNullException(nameof(mediaSource));
        if (source is null) throw new ArgumentNullException(nameof(source));
        return HasStaticTransportShape(mediaSource) &&
               (MatchesUri(mediaSource.Path, source.SourceUri) ||
                MatchesUri(mediaSource.ProbePath, source.SourceUri) ||
                MatchesLocalPath(mediaSource.Path, source.LocalPath) ||
                MatchesLocalPath(mediaSource.ProbePath, source.LocalPath));
    }

    private static bool HasStaticTransportShape(MediaSourceInfo mediaSource) =>
        !mediaSource.RequiresOpening &&
        !mediaSource.RequiresClosing &&
        string.IsNullOrEmpty(mediaSource.OpenToken) &&
        (mediaSource.RequiredHttpHeaders is null || mediaSource.RequiredHttpHeaders.Count == 0);

    private static bool MatchesUri(string? value, Uri source)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
               Uri.Compare(candidate, source, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped,
                   StringComparison.Ordinal) == 0;
    }

    private static bool MatchesLocalPath(string? value, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            return string.Equals(
                System.IO.Path.GetFullPath(value),
                System.IO.Path.GetFullPath(sourcePath),
                System.IO.Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
