using System;

namespace Emby.StrmBridge.Domain;

public enum TicketScope
{
    PlaybackRedirect = 1,
}

public sealed class TicketPayload
{
    public TicketPayload(
        TicketScope scope,
        Guid itemId,
        string mediaSourceId,
        SourceIdentity source,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        TimeSpan boundLifetime)
    {
        Scope = scope;
        ItemId = itemId;
        MediaSourceId = mediaSourceId ?? throw new ArgumentNullException(nameof(mediaSourceId));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        BoundLifetime = boundLifetime;
    }

    public TicketScope Scope { get; }

    public Guid ItemId { get; }

    public string MediaSourceId { get; }

    public SourceIdentity Source { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; internal set; }

    internal TimeSpan BoundLifetime { get; }

    internal long BoundUserId { get; set; }

    internal bool LifetimeExtended { get; set; }
}
