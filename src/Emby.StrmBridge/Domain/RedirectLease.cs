using System;

namespace Emby.StrmBridge.Domain;

public sealed class RedirectLease
{
    public RedirectLease(
        Uri target,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        bool isDirectSource = false)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        IsDirectSource = isDirectSource;
    }

    internal Uri Target { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    internal bool IsDirectSource { get; }

    public bool IsValidAt(DateTimeOffset now) => now < ExpiresAtUtc;

    public string GetLocation() => Target.AbsoluteUri;
}
