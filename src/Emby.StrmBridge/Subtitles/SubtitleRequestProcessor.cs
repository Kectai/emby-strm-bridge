using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Playback;
using Emby.StrmBridge.Policy;
using Emby.StrmBridge.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Subtitles;

internal sealed class SubtitleRequestProcessor
{
    private readonly PluginRuntime runtime;
    private readonly ILibraryManager library;
    private readonly IMediaSourceManager sources;
    private readonly IAuthorizationContext authorization;
    private readonly IAuthService authService;
    private readonly IHttpResultFactory results;
    private readonly SubtitleCoordinator coordinator;
    private readonly ILogger logger;

    internal SubtitleRequestProcessor(PluginRuntime runtime, ILibraryManager library, IMediaSourceManager sources,
        IAuthorizationContext authorization, IAuthService authService, IHttpResultFactory results, SubtitleCoordinator coordinator, ILogger logger)
    {
        this.runtime = runtime; this.library = library; this.sources = sources;
        this.authService = authService;
        this.authorization = authorization; this.results = results; this.coordinator = coordinator; this.logger = logger;
    }

    internal SubtitleRequestContext? Resolve(IRequest http, object request, bool externalClock = false)
    {
        var operation = runtime.BeginOperation();
        var options = runtime.GetOptionsSnapshot();
        if (!options.Enabled || !options.EnableSubtitles || options.PlaybackMode == PlaybackRoutingMode.Native ||
            runtime.SourcePolicy is null || (bool?)Property(request, "NativeHlsClock") != false) return null;
        var user = authorization.GetAuthorizationInfo(http).User;
        if (user is null) return null;
        if (!user.Policy.EnableMediaPlayback || user.Policy.IsDisabled) throw new UnauthorizedAccessException();
        var id = Text(request, "Id");
        var item = ResolveItem(id);
        if (item is null || !PlaybackItemPolicy.IsVideo(item) ||
            !string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase)) return null;
        if (!library.GetCollectionFolders(item).Any(folder => options.IncludedLibraryIds.Any(included =>
            Guid.TryParse(included, out var selected) && selected == folder.Id))) return null;
        authService.Authenticate(http, new AuthenticatedAttribute());
        if (!item.IsVisible(user)) throw new UnauthorizedAccessException();
        var mediaId = Text(request, "MediaSourceId");
        if (string.IsNullOrWhiteSpace(mediaId)) return null;
        var media = sources.GetStaticMediaSources(item, false, false, null)
            .FirstOrDefault(source => string.Equals(source.Id, mediaId, StringComparison.OrdinalIgnoreCase));
        if (media is null) throw new ArgumentException("Subtitle media source unavailable.");
        if (!string.Equals(media.Container, "mkv", StringComparison.OrdinalIgnoreCase) ||
            media.RequiredHttpHeaders?.Count > 0) return null;
        var index = (int?)Property(request, "Index") ?? -1;
        var subtitle = media.MediaStreams?.FirstOrDefault(stream => stream.Type == MediaStreamType.Subtitle && stream.Index == index);
        if (subtitle is null) throw new ArgumentException("Subtitle stream unavailable.");
        if (subtitle.IsExternal != externalClock || (externalClock && !SharedSubtitleOutput.IsExternalAssTrack(subtitle))) return null;
        var codec = subtitle.Codec?.ToLowerInvariant() ?? string.Empty;
        if (codec is not ("ass" or "ssa" or "srt" or "subrip")) return null;
        var sourceItem = string.IsNullOrWhiteSpace(media.ItemId) ? item : ResolveItem(media.ItemId);
        if (sourceItem is null || !sourceItem.IsVisible(user) || !PlaybackItemPolicy.IsVideo(sourceItem) ||
            !library.GetCollectionFolders(sourceItem).Any(folder => options.IncludedLibraryIds.Any(included =>
                Guid.TryParse(included, out var selected) && selected == folder.Id)))
            throw new UnauthorizedAccessException();
        var sourceIdentity = runtime.SourcePolicy.Read(sourceItem.Path);
        if (!StaticMediaSourcePolicy.Matches(sources, sourceItem, sourceIdentity) ||
            !StaticMediaSourcePolicy.MatchesPlaybackSource(media, sourceIdentity))
            throw new ArgumentException("Subtitle source changed.");
        var start = (long?)Property(request, "StartPositionTicks") ?? 0;
        var end = (long?)Property(request, "EndPositionTicks") ?? 0;
        if (start < 0 || end < 0 || start > TimeSpan.FromDays(7).Ticks || end > TimeSpan.FromDays(7).Ticks)
            throw new ArgumentException("Subtitle time range invalid.");
        return new SubtitleRequestContext
        {
            Generation = operation.Generation,
            PlaySessionId = Text(request, "PlaySessionId") ?? string.Empty,
            VideoStartTicks = (long?)Property(request, "VideoStartTicks") ?? 0,
            NativeHlsClock = (bool?)Property(request, "NativeHlsClock") ?? true,
            ItemId = sourceItem.Id,
            RequestedItemId = item.Id,
            UserId = user.Id,
            MediaSourceId = media.Id,
            Source = sourceIdentity,
            Index = index,
            StreamFingerprint = SubtitleDigest.Streams(media.MediaStreams!),
            Codec = codec,
            IsExternal = subtitle.IsExternal,
            Start = start,
            End = end,
        };
    }

    internal async Task<object> RespondAsync(IRequest http, SubtitleRequestContext context, CancellationToken token)
    {
        if (context.End <= context.Start || context.End - context.Start > TimeSpan.FromSeconds(90).Ticks)
            throw new ArgumentException("Subtitle window invalid.");
        var stream = await coordinator.OpenAsync(context, http.CancellationToken, token).ConfigureAwait(false);
        try
        {
            http.Response.StatusCode = 200;
            return results.GetResult(http, stream, "text/x-ssa; charset=utf-8",
                new Dictionary<string, string> { ["Cache-Control"] = "private, no-store", ["X-Content-Type-Options"] = "nosniff", ["X-Accel-Buffering"] = "no", ["X-StrmBridge-Subtitle-Window"] = "partial" });
        }
        catch { stream.Dispose(); throw; }
    }

    internal static int ProblemStatus(string reason) => reason switch
    {
        "source-changed" or "cache-invalidated" => 409,
        "outside-scope" or "source-not-cacheable" => 422,
        "authorization-changed" => 403,
        "idempotency-conflict" => 409,
        "upstream-forbidden" or "range-invalid" or "upstream-rejected" or "parse-invalid" or "extraction-diagnostics" => 502,
        _ => 503,
    };

    private BaseItem? ResolveItem(string? id)
    {
        if (Guid.TryParse(id, out var guid)) return library.GetItemById(guid);
        return long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var internalId)
            ? library.GetItemById(internalId) : null;
    }
    private static object? Property(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
    private static string? Text(object value, string name) => Property(value, name) as string;
}
