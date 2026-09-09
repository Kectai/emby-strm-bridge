using System;
using System.Linq;
using System.Net;
using MediaBrowser.Model.Services;

namespace Emby.StrmBridge.Playback;

internal static class GatewayRouteBuilder
{
    private const string RouteMarker = "/StrmBridge/Playback/";

    public static string CreatePlaybackRoute(string apiPathBase, string ticket, string? container)
    {
        return NormalizeApiPathBase(apiPathBase) + "/StrmBridge/Playback/v3/" + ticket + "/" +
               CreatePlaybackFileName(container);
    }

    public static string? CreateInternalPlaybackRoute(
        string? localApiUrl,
        string apiPathBase,
        string ticket,
        string? container)
    {
        if (!Uri.TryCreate(localApiUrl, UriKind.Absolute, out var local) ||
            !(string.Equals(local.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(local.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(local.UserInfo) ||
            !string.IsNullOrEmpty(local.Query) ||
            !string.IsNullOrEmpty(local.Fragment) ||
            !IPAddress.TryParse(local.Host, out var address) ||
            !IPAddress.IsLoopback(address))
            return null;

        var origin = new UriBuilder(local.Scheme, local.Host, local.IsDefaultPort ? -1 : local.Port).Uri;
        var effectiveApiPathBase = NormalizeApiPathBase(apiPathBase);
        if (effectiveApiPathBase.Length == 0)
            effectiveApiPathBase = NormalizeApiPathBase(local.AbsolutePath);
        return new Uri(origin, CreatePlaybackRoute(effectiveApiPathBase, ticket, container)).AbsoluteUri;
    }

    public static string CreatePlaybackFileName(string? container) => "stream" + NormalizeExtension(container);

    public static string CreateHlsRoute(string apiPathBase, string parentTicket, string ticket, Uri source)
    {
        var extension = NormalizeExtension(System.IO.Path.GetExtension(source.AbsolutePath).TrimStart('.'));
        return NormalizeApiPathBase(apiPathBase) + "/StrmBridge/Playback/v3/" + parentTicket +
               "/hls/" + ticket + "/resource" + extension;
    }

    public static string GetApiPathBase(IRequest? request)
    {
        if (request is null) return string.Empty;
        var path = GetPath(request.RawUrl);
        if (path.Length == 0) path = GetPath(request.AbsoluteUri);
        if (path.Length == 0) return string.Empty;
        var markerIndex = path.IndexOf(RouteMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0) return NormalizeApiPathBase(path.Substring(0, markerIndex));
        foreach (var marker in new[] { "/Items/", "/PlaybackInfo", "/Videos/", "/Audio/" })
        {
            markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0) return NormalizeApiPathBase(path.Substring(0, markerIndex));
        }
        return string.Empty;
    }

    internal static string NormalizeApiPathBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "/") return string.Empty;
        var path = GetPath(value);
        if (path.Length == 0 || path == "/" || path.Contains("..", StringComparison.Ordinal) ||
            path.Contains('\\') || path.StartsWith("//", StringComparison.Ordinal))
            return string.Empty;
        return "/" + path.Trim('/');
    }

    private static string GetPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)) return absolute.AbsolutePath;
        var query = value.IndexOfAny(new[] { '?', '#' });
        return (query >= 0 ? value.Substring(0, query) : value).Trim();
    }

    private static string NormalizeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = value.Trim().TrimStart('.').ToLowerInvariant();
        return cleaned.Length is > 0 and <= 12 && cleaned.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9')
            ? "." + cleaned
            : string.Empty;
    }
}
