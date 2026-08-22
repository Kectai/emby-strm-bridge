using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emby.StrmBridge.Playback;

public static class HlsPlaylistRewriter
{
    public const int MaximumManifestBytes = 2 * 1024 * 1024;
    public const int MaximumLines = 20000;
    public const int MaximumUris = 10000;
    private static readonly Regex UriAttribute = new(
        "URI=(?:\"(?<quoted>[^\"]+)\"|(?<plain>[^,\\s]+))",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Rewrite(string manifest, Uri manifestUri, Func<Uri, string> routeFactory)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        if (manifestUri is null) throw new ArgumentNullException(nameof(manifestUri));
        if (routeFactory is null) throw new ArgumentNullException(nameof(routeFactory));
        var lines = manifest.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > MaximumLines || !lines.Any(line =>
                string.Equals(line.Trim(), "#EXTM3U", StringComparison.Ordinal)))
            throw new InvalidOperationException("The HLS manifest is outside the supported bounds.");
        var uriCount = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("#", StringComparison.Ordinal))
            {
                lines[index] = CreateRoute(line.Trim(), manifestUri, routeFactory, ref uriCount);
                continue;
            }
            lines[index] = UriAttribute.Replace(line, match =>
            {
                var value = match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["plain"].Value;
                var route = CreateRoute(value, manifestUri, routeFactory, ref uriCount);
                return "URI=\"" + route + "\"";
            });
        }
        return string.Join("\n", lines);
    }

    private static string CreateRoute(
        string value,
        Uri manifestUri,
        Func<Uri, string> routeFactory,
        ref int uriCount)
    {
        if (++uriCount > MaximumUris) throw new InvalidOperationException("The HLS URI limit has been reached.");
        if (!Uri.TryCreate(manifestUri, value, out var target) ||
            !(string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Fragment))
            throw new InvalidOperationException("The HLS resource URI is invalid.");
        return routeFactory(target);
    }
}
