using System;
using System.Linq;
using System.Globalization;
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

    // Removed live segments remain readable for at least the longest playlist
    // duration plus the longest segment duration (RFC 8216 section 6.2.2).
    internal static TimeSpan? GetResourceRetention(string manifest)
    {
        var lines = manifest.Replace("\r", string.Empty).Split('\n');
        if (lines.Any(line => line.Trim() is "#EXT-X-ENDLIST" or "#EXT-X-PLAYLIST-TYPE:VOD"))
            return null; // Static media remains seekable for the root ticket lifetime.
        double duration = 0, longestSegment = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var segment = line.StartsWith("#EXTINF:", StringComparison.Ordinal);
            var target = line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal);
            if (!segment && !target) continue;
            var value = line.Substring(line.IndexOf(':') + 1).Split(',')[0];
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                throw new InvalidOperationException("The HLS duration is invalid.");
            longestSegment = Math.Max(longestSegment, seconds);
            if (segment) duration = Math.Min(TicketStore.MaximumLifetime.TotalSeconds, duration + seconds);
        }
        return TimeSpan.FromSeconds(Math.Min(TicketStore.MaximumLifetime.TotalSeconds,
            Math.Max(120, duration + longestSegment)));
    }

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
