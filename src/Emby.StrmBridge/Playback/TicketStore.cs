using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Domain;

namespace Emby.StrmBridge.Playback;

public sealed class TicketStore
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan PlaybackReconnectGrace = TimeSpan.FromHours(2);
    public const int MaximumPlaybackTickets = 4096;
    public const int MaximumHlsTickets = 20000;
    private readonly object sync = new();
    private readonly Dictionary<string, TicketPayload> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> hlsTicketsByParent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HlsTicketIndex> hlsTicketIndexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> hlsMutationGates = new(StringComparer.Ordinal);
    private readonly IClock clock;
    private readonly int playbackCapacity;
    private readonly int hlsCapacity;
    private readonly byte[] userBindingSalt = CreateRandomBytes(32);
    private int playbackCount;
    private int hlsCount;

    public TicketStore(
        IClock clock,
        int playbackCapacity = MaximumPlaybackTickets,
        int hlsCapacity = MaximumHlsTickets)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (playbackCapacity < 1 || playbackCapacity > MaximumPlaybackTickets)
            throw new ArgumentOutOfRangeException(nameof(playbackCapacity));
        if (hlsCapacity < 1 || hlsCapacity > MaximumHlsTickets)
            throw new ArgumentOutOfRangeException(nameof(hlsCapacity));
        this.playbackCapacity = playbackCapacity;
        this.hlsCapacity = hlsCapacity;
    }

    public int Count
    {
        get { lock (sync) return playbackCount + hlsCount; }
    }

    public string IssuePlayback(
        Guid itemId,
        string mediaSourceId,
        string? userId,
        SourceIdentity source,
        PlaybackTicketPurpose purpose,
        int runtimeGeneration,
        TimeSpan? playbackLifetime = null)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var lifetime = ValidatePlaybackLifetime(playbackLifetime ?? MaximumLifetime);
        var now = clock.UtcNow;
        var payload = new TicketPayload(
            TicketScope.Playback,
            itemId,
            mediaSourceId,
            HashUserId(userId),
            source,
            source.SourceUri,
            purpose,
            runtimeGeneration,
            hlsDepth: 0,
            now,
            now + PreviewLifetime,
            now + MaximumLifetime,
            lifetime);
        return Add(payload, playbackCapacity);
    }

    public string IssueHlsResource(
        string rootTicket,
        string currentTicket,
        TicketPayload parent,
        Uri upstreamUri,
        out bool created)
    {
        created = false;
        if (!IsWellFormed(rootTicket)) throw new ArgumentException("The root ticket is invalid.", nameof(rootTicket));
        if (!IsWellFormed(currentTicket))
            throw new ArgumentException("The current ticket is invalid.", nameof(currentTicket));
        if (parent is null) throw new ArgumentNullException(nameof(parent));
        if (upstreamUri is null) throw new ArgumentNullException(nameof(upstreamUri));
        if (parent.HlsDepth >= 8) throw new TicketCapacityException();
        if (upstreamUri.AbsoluteUri.Length > 8192)
            throw new ArgumentException("The HLS resource URI is too long.", nameof(upstreamUri));
        lock (sync)
        {
            RemoveExpiredUnsafe();
            if (!entries.TryGetValue(rootTicket, out var root) || root.Scope != TicketScope.Playback ||
                !entries.TryGetValue(currentTicket, out var current) || !ReferenceEquals(current, parent) ||
                parent.Scope == TicketScope.Playback && !string.Equals(rootTicket, currentTicket, StringComparison.Ordinal) ||
                parent.Scope == TicketScope.HlsResource &&
                (!hlsTicketIndexes.TryGetValue(currentTicket, out var currentIndex) ||
                 !string.Equals(currentIndex.ParentTicket, rootTicket, StringComparison.Ordinal)) ||
                root.ItemId != parent.ItemId ||
                !string.Equals(root.MediaSourceId, parent.MediaSourceId, StringComparison.Ordinal))
                throw new InvalidOperationException("The HLS ticket relationship is unavailable.");
            var resourceKey = HashHlsResource(upstreamUri, parent.HlsDepth + 1);
            if (hlsTicketsByParent.TryGetValue(rootTicket, out var indexed) &&
                indexed.TryGetValue(resourceKey, out var existingTicket) &&
                entries.TryGetValue(existingTicket, out var existing) &&
                Uri.Compare(existing.UpstreamUri, upstreamUri, UriComponents.AbsoluteUri,
                    UriFormat.UriEscaped, StringComparison.Ordinal) == 0)
            {
                return existingTicket;
            }

            var now = clock.UtcNow;
            var payload = new TicketPayload(
                TicketScope.HlsResource,
                parent.ItemId,
                parent.MediaSourceId,
                parent.UserBindingHash.ToArray(),
                parent.Source,
                upstreamUri,
                parent.Purpose,
                parent.RuntimeGeneration,
                parent.HlsDepth + 1,
                now,
                parent.ExpiresAtUtc,
                parent.MaximumExpiresAtUtc,
                parent.PlaybackLifetime);
            var ticket = AddUnsafe(payload, hlsCapacity);
            indexed ??= new Dictionary<string, string>(StringComparer.Ordinal);
            indexed[resourceKey] = ticket;
            hlsTicketsByParent[rootTicket] = indexed;
            hlsTicketIndexes[ticket] = new HlsTicketIndex(rootTicket, resourceKey);
            created = true;
            return ticket;
        }
    }

    public bool TryInspect(string ticket, out TicketPayload? payload)
    {
        payload = null;
        if (!IsWellFormed(ticket)) return false;
        lock (sync)
        {
            if (!entries.TryGetValue(ticket, out var found)) return false;
            if (clock.UtcNow >= found.ExpiresAtUtc)
            {
                RemoveUnsafe(ticket);
                return false;
            }
            payload = found;
            return true;
        }
    }

    public bool TryRedeem(string ticket, string? authenticatedUserId, out TicketPayload? payload)
    {
        payload = null;
        if (!IsWellFormed(ticket)) return false;
        lock (sync)
        {
            if (!entries.TryGetValue(ticket, out var found)) return false;
            var now = clock.UtcNow;
            if (now >= found.ExpiresAtUtc)
            {
                RemoveUnsafe(ticket);
                return false;
            }
            var providedBinding = HashUserId(authenticatedUserId);
            if (providedBinding.Length > 0 && found.UserBindingHash.Length > 0 &&
                !FixedTimeEquals(providedBinding, found.UserBindingHash))
                return false;
            var requestedExpiry = now + found.PlaybackLifetime;
            found.ExpiresAtUtc = requestedExpiry < found.MaximumExpiresAtUtc
                ? requestedExpiry
                : found.MaximumExpiresAtUtc;
            payload = found;
            return true;
        }
    }

    public bool IsHlsResourceOfRoot(string rootTicket, string childTicket)
    {
        if (!IsWellFormed(rootTicket) || !IsWellFormed(childTicket)) return false;
        lock (sync)
        {
            return entries.TryGetValue(rootTicket, out var root) && root.Scope == TicketScope.Playback &&
                   entries.TryGetValue(childTicket, out var child) && child.Scope == TicketScope.HlsResource &&
                   clock.UtcNow < root.ExpiresAtUtc && clock.UtcNow < child.ExpiresAtUtc &&
                   hlsTicketIndexes.TryGetValue(childTicket, out var index) &&
                   string.Equals(index.ParentTicket, rootTicket, StringComparison.Ordinal);
        }
    }

    public async Task<IDisposable> AcquireHlsMutationAsync(
        string rootTicket,
        CancellationToken cancellationToken)
    {
        if (!IsWellFormed(rootTicket)) throw new ArgumentException("The root ticket is invalid.", nameof(rootTicket));
        SemaphoreSlim gate;
        lock (sync)
        {
            if (!entries.TryGetValue(rootTicket, out var root) || root.Scope != TicketScope.Playback ||
                clock.UtcNow >= root.ExpiresAtUtc)
                throw new InvalidOperationException("The root ticket is unavailable.");
            if (!hlsMutationGates.TryGetValue(rootTicket, out gate!))
            {
                gate = new SemaphoreSlim(1, 1);
                hlsMutationGates.Add(rootTicket, gate);
            }
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            if (entries.TryGetValue(rootTicket, out var root) && root.Scope == TicketScope.Playback &&
                clock.UtcNow < root.ExpiresAtUtc)
                return new HlsMutationLease(gate);
        }
        gate.Release();
        throw new InvalidOperationException("The root ticket is unavailable.");
    }

    public void Revoke(string ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return;
        lock (sync) RemoveUnsafe(ticket);
    }

    public int RemoveExpired()
    {
        lock (sync)
        {
            var expired = entries.Where(entry => clock.UtcNow >= entry.Value.ExpiresAtUtc)
                .Select(entry => entry.Key)
                .ToArray();
            foreach (var ticket in expired) RemoveUnsafe(ticket);
            return expired.Length;
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            entries.Clear();
            hlsTicketsByParent.Clear();
            hlsTicketIndexes.Clear();
            hlsMutationGates.Clear();
            playbackCount = 0;
            hlsCount = 0;
        }
    }

    public static TimeSpan ComputePlaybackLifetime(long? runTimeTicks)
    {
        if (!runTimeTicks.HasValue || runTimeTicks.Value <= 0) return MaximumLifetime;
        var maximumMediaTicks = MaximumLifetime.Ticks - PlaybackReconnectGrace.Ticks;
        var mediaTicks = Math.Min(runTimeTicks.Value, maximumMediaTicks);
        return TimeSpan.FromTicks(mediaTicks) + PlaybackReconnectGrace;
    }

    private string Add(TicketPayload payload, int scopeCapacity)
    {
        lock (sync)
        {
            RemoveExpiredUnsafe();
            return AddUnsafe(payload, scopeCapacity);
        }
    }

    private string AddUnsafe(TicketPayload payload, int scopeCapacity)
    {
        var currentScopeCount = payload.Scope == TicketScope.Playback ? playbackCount : hlsCount;
        if (currentScopeCount >= scopeCapacity) throw new TicketCapacityException();
        string ticket;
        do { ticket = CreateTicket(); } while (entries.ContainsKey(ticket));
        entries.Add(ticket, payload);
        if (payload.Scope == TicketScope.Playback) playbackCount++;
        else hlsCount++;
        return ticket;
    }

    private void RemoveExpiredUnsafe()
    {
        var now = clock.UtcNow;
        foreach (var ticket in entries.Where(entry => now >= entry.Value.ExpiresAtUtc)
                     .Select(entry => entry.Key).ToArray())
            RemoveUnsafe(ticket);
    }

    private void RemoveUnsafe(string ticket)
    {
        if (!entries.TryGetValue(ticket, out var payload) || !entries.Remove(ticket)) return;
        if (payload.Scope == TicketScope.Playback)
        {
            playbackCount--;
            hlsMutationGates.Remove(ticket);
            if (hlsTicketsByParent.TryGetValue(ticket, out var children))
            {
                foreach (var childTicket in children.Values.ToArray()) RemoveUnsafe(childTicket);
                hlsTicketsByParent.Remove(ticket);
            }
        }
        else
        {
            hlsCount--;
            if (hlsTicketIndexes.TryGetValue(ticket, out var index))
            {
                hlsTicketIndexes.Remove(ticket);
                if (hlsTicketsByParent.TryGetValue(index.ParentTicket, out var children))
                {
                    children.Remove(index.ResourceKey);
                    if (children.Count == 0) hlsTicketsByParent.Remove(index.ParentTicket);
                }
            }
        }
    }

    private static TimeSpan ValidatePlaybackLifetime(TimeSpan lifetime)
    {
        if (lifetime < PreviewLifetime || lifetime > MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        return lifetime;
    }

    private static string CreateTicket()
    {
        var bytes = CreateRandomBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashHlsResource(Uri resource, int depth)
    {
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(
            depth.ToString(CultureInfo.InvariantCulture) + "\n" + resource.AbsoluteUri)));
    }

    private byte[] HashUserId(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace("-", string.Empty).ToLowerInvariant();
        if (normalized.Length == 0) return Array.Empty<byte>();
        using var hmac = new HMACSHA256(userBindingSalt);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        var difference = 0;
        for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
        return difference == 0;
    }

    private static byte[] CreateRandomBytes(int length)
    {
        var bytes = new byte[length];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return bytes;
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

    private sealed class HlsTicketIndex
    {
        public HlsTicketIndex(string parentTicket, string resourceKey)
        {
            ParentTicket = parentTicket;
            ResourceKey = resourceKey;
        }

        public string ParentTicket { get; }

        public string ResourceKey { get; }
    }

    private sealed class HlsMutationLease : IDisposable
    {
        private SemaphoreSlim? gate;

        public HlsMutationLease(SemaphoreSlim gate) => this.gate = gate;

        public void Dispose() => Interlocked.Exchange(ref gate, null)?.Release();
    }
}

public sealed class TicketCapacityException : Exception
{
    public TicketCapacityException() : base("The playback-ticket capacity has been reached.") { }
}
