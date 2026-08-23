using System;

namespace Emby.StrmBridge.Domain;

public enum TicketScope
{
    Playback = 1,
    HlsResource = 2,
}

public enum PlaybackTicketPurpose
{
    DirectClient = 1,
    ServerFfmpeg = 2,
}

public sealed class TicketPayload
{
    internal TicketPayload(
        TicketScope scope,
        Guid itemId,
        string mediaSourceId,
        byte[] userBindingHash,
        byte[] deviceBindingHash,
        SourceIdentity source,
        Uri upstreamUri,
        PlaybackTicketPurpose purpose,
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
        DeviceBindingHash = deviceBindingHash ?? throw new ArgumentNullException(nameof(deviceBindingHash));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        UpstreamUri = upstreamUri ?? throw new ArgumentNullException(nameof(upstreamUri));
        Purpose = purpose;
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

    internal byte[] DeviceBindingHash { get; }

    public SourceIdentity Source { get; }

    internal Uri UpstreamUri { get; }

    public PlaybackTicketPurpose Purpose { get; }

    public int RuntimeGeneration { get; }

    public int HlsDepth { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; internal set; }

    internal DateTimeOffset MaximumExpiresAtUtc { get; }

    internal TimeSpan PlaybackLifetime { get; }

}
