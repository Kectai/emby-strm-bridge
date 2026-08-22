using System;

namespace Emby.StrmBridge.Domain;

public enum TicketScope
{
    Playback = 1,
    HlsResource = 2,
}

public sealed class TicketPayload
{
    internal TicketPayload(
        TicketScope scope,
        Guid itemId,
        string mediaSourceId,
        byte[] userBindingHash,
        SourceIdentity source,
        Uri upstreamUri,
        int runtimeGeneration,
        int hlsDepth,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset maximumExpiresAtUtc,
        TimeSpan playbackLifetime)
    {
        Scope = scope;
        ItemId = itemId;
        MediaSourceId = mediaSourceId ?? throw new ArgumentNullException(nameof(mediaSourceId));
        UserBindingHash = userBindingHash ?? throw new ArgumentNullException(nameof(userBindingHash));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        UpstreamUri = upstreamUri ?? throw new ArgumentNullException(nameof(upstreamUri));
        RuntimeGeneration = runtimeGeneration;
        HlsDepth = hlsDepth;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        MaximumExpiresAtUtc = maximumExpiresAtUtc;
        PlaybackLifetime = playbackLifetime;
    }

    public TicketScope Scope { get; }

    public Guid ItemId { get; }

    public string MediaSourceId { get; }

    internal byte[] UserBindingHash { get; }

    public SourceIdentity Source { get; }

    internal Uri UpstreamUri { get; }

    public int RuntimeGeneration { get; }

    public int HlsDepth { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; internal set; }

    internal DateTimeOffset MaximumExpiresAtUtc { get; }

    internal TimeSpan PlaybackLifetime { get; }

}
