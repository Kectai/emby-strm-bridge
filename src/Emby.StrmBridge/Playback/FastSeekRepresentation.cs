using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Emby.StrmBridge.Playback;

// A byte offset is meaningful only within the exact identity-encoded representation sampled.
// This value stays in memory and must never be included in logs or persisted metadata.
internal sealed class FastSeekRepresentation
{
    public FastSeekRepresentation(string strongETag, long totalLength)
    {
        if (!IsStrongETag(strongETag) || totalLength <= 0)
            throw new ArgumentException("A strong media representation validator is required.");
        StrongETag = strongETag;
        TotalLength = totalLength;
    }

    public string StrongETag { get; }
    public long TotalLength { get; }
    public string UserAgent => GatewayTransport.ProbeUserAgent;

    public bool Matches(FastSeekRepresentation? other) => other is not null &&
        TotalLength == other.TotalLength && string.Equals(StrongETag, other.StrongETag, StringComparison.Ordinal);

    public bool Matches(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent &&
        TryCreate(response, out var actual) && Matches(actual);

    public static bool TryCreate(HttpResponseMessage response, out FastSeekRepresentation? representation)
    {
        representation = null;
        var etag = response.Headers.ETag;
        var length = response.StatusCode == HttpStatusCode.PartialContent
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        if (etag is null || etag.IsWeak || !IsStrongETag(etag.Tag) || !length.HasValue || length.Value <= 0 ||
            response.Content.Headers.ContentEncoding.Any(value =>
                !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase))) return false;
        representation = new FastSeekRepresentation(etag.Tag, length.Value);
        return true;
    }

    private static bool IsStrongETag(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 1024 &&
        !value.Any(char.IsControl) && EntityTagHeaderValue.TryParse(value, out var parsed) &&
        !parsed.IsWeak && parsed.Tag != "*";
}
