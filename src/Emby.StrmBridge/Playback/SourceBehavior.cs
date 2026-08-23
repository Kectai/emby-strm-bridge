using System;
using System.Net.Http;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Domain;

namespace Emby.StrmBridge.Playback;

public enum SourceTransportBehavior
{
    FileBody = 0,
    HlsManifest = 1,
}

public enum GatewayTransportPlan
{
    Redirect = 0,
    RelayFile = 1,
    RelayHls = 2,
}

public static class SourceBehaviorClassifier
{
    private static readonly byte[] HlsSignature =
        { (byte)'#', (byte)'E', (byte)'X', (byte)'T', (byte)'M', (byte)'3', (byte)'U' };

    public static SourceTransportBehavior Classify(HttpResponseMessage response, Uri effectiveUri)
        => Classify(response, effectiveUri, ReadOnlyMemory<byte>.Empty);

    public static SourceTransportBehavior Classify(
        HttpResponseMessage response,
        Uri effectiveUri,
        ReadOnlyMemory<byte> contentPrefix)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));
        if (effectiveUri is null) throw new ArgumentNullException(nameof(effectiveUri));
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0 ||
            effectiveUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            HasHlsSignature(contentPrefix))
            return SourceTransportBehavior.HlsManifest;
        return SourceTransportBehavior.FileBody;
    }

    private static bool HasHlsSignature(ReadOnlyMemory<byte> prefix)
    {
        var value = prefix.Span;
        var offset = value.Length >= 3 && value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf ? 3 : 0;
        if (value.Length - offset < HlsSignature.Length) return false;
        for (var index = 0; index < HlsSignature.Length; index++)
            if (value[offset + index] != HlsSignature[index]) return false;
        return true;
    }
}

public static class TransportPlanner
{
    public static GatewayTransportPlan Create(
        PlaybackRoutingMode mode,
        SourceTransportBehavior behavior,
        PlaybackTicketPurpose purpose)
    {
        if (mode == PlaybackRoutingMode.RedirectOnly) return GatewayTransportPlan.Redirect;
        if (behavior == SourceTransportBehavior.HlsManifest) return GatewayTransportPlan.RelayHls;
        if (mode == PlaybackRoutingMode.Adaptive && purpose == PlaybackTicketPurpose.DirectClient)
            return GatewayTransportPlan.Redirect;
        return GatewayTransportPlan.RelayFile;
    }
}
