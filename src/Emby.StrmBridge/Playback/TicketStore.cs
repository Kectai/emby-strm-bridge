using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Emby.StrmBridge.Domain;

namespace Emby.StrmBridge.Playback;

public sealed class TicketStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan BoundPlaybackLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan PlaybackReconnectGrace = TimeSpan.FromHours(2);
    public const int MaximumCapacity = 2048;
    private readonly object sync = new();
    private readonly Dictionary<string, TicketPayload> entries = new(StringComparer.Ordinal);
    private readonly IClock clock;
    private readonly int capacity;

    public TicketStore(IClock clock, int capacity = 1024)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (capacity < 1 || capacity > MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public string Issue(
        TicketScope scope,
        Guid itemId,
        string mediaSourceId,
        SourceIdentity source,
        TimeSpan? lifetime = null,
        TimeSpan? boundLifetime = null)
    {
        var actualLifetime = lifetime ?? DefaultLifetime;
        if (actualLifetime <= TimeSpan.Zero || actualLifetime > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
        var actualBoundLifetime = boundLifetime ?? BoundPlaybackLifetime;
        if (actualBoundLifetime < DefaultLifetime || actualBoundLifetime > BoundPlaybackLifetime)
            throw new ArgumentOutOfRangeException(nameof(boundLifetime));
        var now = clock.UtcNow;
        lock (sync)
        {
            RemoveExpiredUnsafe(now);
            if (entries.Count >= capacity) throw new TicketCapacityException();
            string ticket;
            do { ticket = CreateTicket(); } while (entries.ContainsKey(ticket));
            entries.Add(ticket, new TicketPayload(
                scope,
                itemId,
                mediaSourceId,
                source,
                now,
                now + actualLifetime,
                actualBoundLifetime));
            return ticket;
        }
    }

    public bool TryRedeem(string ticket, TicketScope expectedScope, long userId, out TicketPayload? payload)
    {
        payload = null;
        if (!IsWellFormed(ticket) || userId <= 0) return false;
        lock (sync)
        {
            if (!entries.TryGetValue(ticket, out var found)) return false;
            if (clock.UtcNow >= found.ExpiresAtUtc || found.Scope != expectedScope)
            {
                entries.Remove(ticket);
                return false;
            }
            if (found.BoundUserId == 0)
            {
                found.BoundUserId = userId;
                ExtendLifetimeOnce(found, clock.UtcNow);
            }
            else if (found.BoundUserId != userId) return false;
            payload = found;
            return true;
        }
    }

    public bool TryRedeemLocalServer(
        string ticket,
        TicketScope expectedScope,
        out TicketPayload? payload)
    {
        payload = null;
        if (!IsWellFormed(ticket)) return false;
        lock (sync)
        {
            if (!entries.TryGetValue(ticket, out var found)) return false;
            if (clock.UtcNow >= found.ExpiresAtUtc || found.Scope != expectedScope)
            {
                entries.Remove(ticket);
                return false;
            }
            ExtendLifetimeOnce(found, clock.UtcNow);
            payload = found;
            return true;
        }
    }

    public bool TryInspect(string ticket, TicketScope expectedScope, out TicketPayload? payload)
    {
        payload = null;
        if (!IsWellFormed(ticket)) return false;
        lock (sync)
        {
            if (!entries.TryGetValue(ticket, out var found)) return false;
            if (clock.UtcNow >= found.ExpiresAtUtc || found.Scope != expectedScope)
            {
                entries.Remove(ticket);
                return false;
            }
            payload = found;
            return true;
        }
    }

    public void Revoke(string ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return;
        lock (sync) entries.Remove(ticket);
    }

    public int RemoveExpired()
    {
        lock (sync) return RemoveExpiredUnsafe(clock.UtcNow);
    }

    public void Clear()
    {
        lock (sync) entries.Clear();
    }

    public static TimeSpan ComputeBoundPlaybackLifetime(long? runTimeTicks)
    {
        if (!runTimeTicks.HasValue || runTimeTicks.Value <= 0) return BoundPlaybackLifetime;
        var maximumMediaTicks = BoundPlaybackLifetime.Ticks - PlaybackReconnectGrace.Ticks;
        var mediaTicks = Math.Min(runTimeTicks.Value, maximumMediaTicks);
        return TimeSpan.FromTicks(mediaTicks) + PlaybackReconnectGrace;
    }

    private int RemoveExpiredUnsafe(DateTimeOffset now)
    {
        var expired = new List<string>();
        foreach (var entry in entries)
        {
            if (now >= entry.Value.ExpiresAtUtc) expired.Add(entry.Key);
        }
        foreach (var key in expired) entries.Remove(key);
        return expired.Count;
    }

    private static void ExtendLifetimeOnce(TicketPayload payload, DateTimeOffset now)
    {
        if (payload.LifetimeExtended) return;
        payload.ExpiresAtUtc = now + payload.BoundLifetime;
        payload.LifetimeExtended = true;
    }

    private static string CreateTicket()
    {
        var bytes = new byte[32];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool IsWellFormed(string? ticket)
    {
        if (ticket is null || ticket.Length != 43) return false;
        foreach (var character in ticket)
        {
            if (!(character >= 'a' && character <= 'z') && !(character >= 'A' && character <= 'Z') &&
                !(character >= '0' && character <= '9') && character != '-' && character != '_') return false;
        }
        return true;
    }
}

public sealed class TicketCapacityException : Exception
{
    public TicketCapacityException() : base("The playback-ticket capacity has been reached.") { }
}
