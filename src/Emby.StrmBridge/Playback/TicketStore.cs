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
    public const int MaximumHlsTicketsPerRoot = 12000;
    private readonly object sync = new();
    private readonly Dictionary<string, TicketPayload> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> nativeSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> hlsTicketsByParent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HlsManifestWindow> hlsManifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HlsTicketIndex> hlsTicketIndexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> hlsMutationGates = new(StringComparer.Ordinal);
    private readonly IClock clock;
    private readonly int playbackCapacity;
    private readonly int hlsCapacity;
    private readonly byte[] userBindingSalt = CreateRandomBytes(32);
    private readonly byte[] deviceBindingSalt = CreateRandomBytes(32);
    private const int MaximumHlsReferences = 40000;
    private const int MaximumHlsReferencesPerRoot = 24000;
    private readonly Dictionary<string, int> hlsReferenceCounts = new(StringComparer.Ordinal);
    private int hlsReferenceCount;
    private readonly int hlsRootCapacity;
    private int playbackCount;
    private int hlsCount;
    private DateTimeOffset nextExpirationSweep;
    internal long ExpirationSweepCount { get; private set; }

    public TicketStore(
        IClock clock,
        int playbackCapacity = MaximumPlaybackTickets,
        int hlsCapacity = MaximumHlsTickets,
        int hlsRootCapacity = MaximumHlsTicketsPerRoot)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (playbackCapacity < 1 || playbackCapacity > MaximumPlaybackTickets)
            throw new ArgumentOutOfRangeException(nameof(playbackCapacity));
        if (hlsCapacity < 1 || hlsCapacity > MaximumHlsTickets)
            throw new ArgumentOutOfRangeException(nameof(hlsCapacity));
        if (hlsRootCapacity < 1 || hlsRootCapacity > MaximumHlsTicketsPerRoot)
            throw new ArgumentOutOfRangeException(nameof(hlsRootCapacity));
        this.hlsRootCapacity = Math.Min(hlsRootCapacity, hlsCapacity);
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
        TimeSpan? playbackLifetime = null,
        string? deviceId = null,
        bool sourceRedirectHandoffAllowed = false)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var isProbe = purpose == PlaybackTicketPurpose.ExtractionProbe;
        var lifetime = playbackLifetime ?? MaximumLifetime;
        if (isProbe)
        {
            if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(3))
                throw new ArgumentOutOfRangeException(nameof(playbackLifetime));
        }
        else lifetime = ValidatePlaybackLifetime(lifetime);
        var now = clock.UtcNow;
        var payload = new TicketPayload(
            TicketScope.Playback,
            itemId,
            mediaSourceId,
            HashUserId(userId),
            HashDeviceId(deviceId),
            source,
            source.SourceUri,
            purpose,
            runtimeGeneration,
            hlsDepth: 0,
            sourceRedirectHandoffAllowed: purpose == PlaybackTicketPurpose.DirectClient &&
                                          sourceRedirectHandoffAllowed,
            now,
            now + (isProbe ? lifetime : PreviewLifetime),
            now + (isProbe ? lifetime : MaximumLifetime),
            lifetime);
        return Add(payload, playbackCapacity);
    }

    internal void RegisterNativePlayback(string ticket, string? playSessionId)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(ticket, out var payload)) return;
            var key = NativeSessionKey(payload, playSessionId);
            if (key.Length > 0) nativeSessions[key] = ticket;
            var fallback = NativeSessionKey(payload, null);
            if (fallback.Length > 0) nativeSessions[fallback] = ticket;
        }
    }

    internal string GetOrIssueNativePlayback(
        Guid itemId, string mediaSourceId, string? userId, string? deviceId,
        string? playSessionId, SourceIdentity source, int runtimeGeneration,
        TimeSpan lifetime, out bool requestScoped, bool sourceRedirectHandoffAllowed = false)
    {
        lock (sync)
        {
            var key = NativeSessionKey(itemId, mediaSourceId, source.SourceFingerprint,
                HashUserId(userId), HashDeviceId(deviceId), sourceRedirectHandoffAllowed, playSessionId);
            if (key.Length > 0 && nativeSessions.TryGetValue(key, out var current) &&
                entries.TryGetValue(current, out var payload) &&
                clock.UtcNow < payload.ExpiresAtUtc &&
                payload.RuntimeGeneration == runtimeGeneration && payload.Source.HasSameFileVersion(source))
            {
                requestScoped = false;
                return current;
            }
            var ticket = IssuePlayback(itemId, mediaSourceId, userId, source,
                PlaybackTicketPurpose.DirectClient, runtimeGeneration, lifetime, deviceId,
                sourceRedirectHandoffAllowed);
            requestScoped = string.IsNullOrWhiteSpace(playSessionId) || key.Length == 0;
            if (!requestScoped) nativeSessions[key] = ticket;
            return ticket;
        }
    }

    internal void ReleaseRequestTicket(string ticket)
    {
        lock (sync)
            if (!hlsTicketsByParent.ContainsKey(ticket)) RemoveUnsafe(ticket);
    }

    private static string NativeSessionKey(TicketPayload payload, string? session) =>
        NativeSessionKey(payload.ItemId, payload.MediaSourceId, payload.Source.SourceFingerprint,
            payload.UserBindingHash, payload.DeviceBindingHash,
            payload.SourceRedirectHandoffAllowed, session);

    private static string NativeSessionKey(Guid itemId, string mediaSourceId, string fingerprint,
        byte[] user, byte[] device, bool sourceRedirectHandoffAllowed, string? session)
    {
        if (user.Length == 0 || string.IsNullOrWhiteSpace(session) && device.Length == 0 || session?.Length > 256)
            return string.Empty;
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(
            itemId.ToString("N") + "\n" + mediaSourceId + "\n" + fingerprint + "\n" +
            Convert.ToBase64String(user) + "\n" + Convert.ToBase64String(device) + "\n" +
            (sourceRedirectHandoffAllowed ? "file" : "unknown") + "\n" + session)));
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
            SweepExpiredIfDueUnsafe();
            if (!entries.TryGetValue(rootTicket, out var root) || root.Scope != TicketScope.Playback ||
                IsExpiredUnsafe(rootTicket, root, clock.UtcNow) ||
                !entries.TryGetValue(currentTicket, out var current) || !ReferenceEquals(current, parent) ||
                IsExpiredUnsafe(currentTicket, current, clock.UtcNow) ||
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
                if (!IsExpiredUnsafe(existingTicket, existing, clock.UtcNow)) return existingTicket;
                RemoveUnsafe(existingTicket);
                hlsTicketsByParent.TryGetValue(rootTicket, out indexed);
            }

            if (indexed?.Count >= hlsRootCapacity || hlsCount >= hlsCapacity)
            {
                // Capacity pressure must reclaim expired entries even inside the sweep interval.
                SweepExpiredIfDueUnsafe(force: true);
                if (!entries.ContainsKey(rootTicket) || !entries.ContainsKey(currentTicket) ||
                    IsExpiredUnsafe(rootTicket, root, clock.UtcNow) ||
                    IsExpiredUnsafe(currentTicket, current, clock.UtcNow))
                    throw new InvalidOperationException("The HLS ticket relationship is unavailable.");
                hlsTicketsByParent.TryGetValue(rootTicket, out indexed);
            }
            if (indexed?.Count >= hlsRootCapacity)
                throw new TicketCapacityException();
            var now = clock.UtcNow;
            var payload = new TicketPayload(
                TicketScope.HlsResource,
                parent.ItemId,
                parent.MediaSourceId,
                parent.UserBindingHash.ToArray(),
                parent.DeviceBindingHash.ToArray(),
                parent.Source,
                upstreamUri,
                parent.Purpose,
                parent.RuntimeGeneration,
                parent.HlsDepth + 1,
                sourceRedirectHandoffAllowed: false,
                now,
                parent.ExpiresAtUtc,
                parent.MaximumExpiresAtUtc,
                parent.PlaybackLifetime);
            var ticket = AddUnsafe(payload, hlsCapacity);
            payload.ProbeCancellation = parent.ProbeCancellation;
            indexed ??= new Dictionary<string, string>(StringComparer.Ordinal);
            indexed[resourceKey] = ticket;
            hlsTicketsByParent[rootTicket] = indexed;
            hlsTicketIndexes[ticket] = new HlsTicketIndex(rootTicket, resourceKey);
            created = true;
            return ticket;
        }
    }

    internal void CommitHlsManifest(string rootTicket, string manifestTicket,
        IEnumerable<string> resourceTickets, TimeSpan? retention)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(rootTicket, out var root) || root.Scope != TicketScope.Playback ||
                clock.UtcNow >= root.ExpiresAtUtc ||
                manifestTicket != rootTicket && !IsHlsResourceOfRoot(rootTicket, manifestTicket))
                throw new InvalidOperationException("The HLS manifest is unavailable.");
            var resources = new HashSet<string>(resourceTickets, StringComparer.Ordinal);
            foreach (var resource in resources)
                if (!hlsTicketIndexes.TryGetValue(resource, out var index) || index.ParentTicket != rootTicket)
                    throw new InvalidOperationException("The HLS resource relationship is unavailable.");
            var addedReferences = resources.Count(resource =>
                !hlsTicketIndexes[resource].References.ContainsKey(manifestTicket));
            hlsReferenceCounts.TryGetValue(rootTicket, out var rootReferences);
            if (hlsReferenceCount + addedReferences > MaximumHlsReferences ||
                rootReferences + addedReferences > MaximumHlsReferencesPerRoot)
                throw new TicketCapacityException();
            if (hlsManifests.TryGetValue(manifestTicket, out var previous))
            {
                // Keep the longest observed window: a shrinking live playlist must not
                // shorten the promised lifetime of resources in an older response.
                retention = previous.Retention is null || retention is null ? null :
                    previous.Retention > retention ? previous.Retention : retention;
                foreach (var removed in previous.Resources.Except(resources))
                    RetireReferenceUnsafe(removed, manifestTicket, retention);
            }
            foreach (var resource in resources)
                hlsTicketIndexes[resource].References[manifestTicket] = null;
            hlsReferenceCount += addedReferences;
            hlsReferenceCounts[rootTicket] = rootReferences + addedReferences;
            hlsManifests[manifestTicket] = new HlsManifestWindow(resources, retention);
        }
    }

    private void RetireReferenceUnsafe(string resource, string manifest, TimeSpan? retention)
    {
        if (hlsTicketIndexes.TryGetValue(resource, out var index) && index.References.ContainsKey(manifest))
            index.References[manifest] = retention.HasValue ? clock.UtcNow + retention.Value : null;
    }

    private bool IsExpiredUnsafe(string ticket, TicketPayload payload, DateTimeOffset now) =>
        now >= payload.ExpiresAtUtc ||
        hlsTicketIndexes.TryGetValue(ticket, out var index) && index.References.Count > 0 &&
        index.References.Values.All(deadline => deadline.HasValue && now >= deadline.Value);

    public bool TryInspect(string ticket, out TicketPayload? payload)
    {
        payload = null;
        if (!IsWellFormed(ticket)) return false;
        lock (sync)
        {
            if (!entries.TryGetValue(ticket, out var found)) return false;
            if (IsExpiredUnsafe(ticket, found, clock.UtcNow))
            {
                RemoveUnsafe(ticket);
                return false;
            }
            payload = found;
            return true;
        }
    }

    internal bool MatchesTranscodeInput(string ticket, Guid itemId, string mediaSourceId,
        string? userId, SourceIdentity source, int runtimeGeneration)
    {
        lock (sync)
        {
            return TryInspect(ticket, out var payload) && payload is not null &&
                   payload.Scope == TicketScope.Playback && payload.Purpose == PlaybackTicketPurpose.ServerFfmpeg &&
                   payload.ItemId == itemId && payload.MediaSourceId == mediaSourceId &&
                   payload.RuntimeGeneration == runtimeGeneration && payload.Source.HasSameFileVersion(source) &&
                   FixedTimeEquals(payload.UserBindingHash, HashUserId(userId));
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
            if (IsExpiredUnsafe(ticket, found, now))
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
                   clock.UtcNow < root.ExpiresAtUtc && !IsExpiredUnsafe(childTicket, child, clock.UtcNow) &&
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
            // One batch sweep before rewriting, outside the per-URI path.
            SweepExpiredIfDueUnsafe(force: true);
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
            var previousCount = entries.Count;
            SweepExpiredIfDueUnsafe(force: true);
            return previousCount - entries.Count;
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            entries.Clear();
            nativeSessions.Clear();
            hlsTicketsByParent.Clear();
            hlsTicketIndexes.Clear();
            hlsManifests.Clear();
            hlsReferenceCounts.Clear();
            hlsReferenceCount = 0;
            hlsMutationGates.Clear();
            playbackCount = 0;
            hlsCount = 0;
            nextExpirationSweep = default;
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
            SweepExpiredIfDueUnsafe(force: playbackCount >= scopeCapacity);
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

    private void SweepExpiredIfDueUnsafe(bool force = false)
    {
        var now = clock.UtcNow;
        if (!force && now < nextExpirationSweep) return;
        nextExpirationSweep = now + TimeSpan.FromSeconds(1);
        ExpirationSweepCount++;
        foreach (var ticket in entries.Where(entry => IsExpiredUnsafe(entry.Key, entry.Value, now))
                     .Select(entry => entry.Key).ToArray())
            RemoveUnsafe(ticket);
        foreach (var index in hlsTicketIndexes.Values)
        {
            var expired = index.References.Where(pair => pair.Value.HasValue && now >= pair.Value.Value)
                .Select(pair => pair.Key).ToArray();
            foreach (var manifest in expired) index.References.Remove(manifest);
            if (expired.Length == 0) continue;
            hlsReferenceCount -= expired.Length;
            hlsReferenceCounts[index.ParentTicket] -= expired.Length;
        }
    }

    private void RemoveUnsafe(string ticket)
    {
        if (!entries.TryGetValue(ticket, out var payload) || !entries.Remove(ticket)) return;
        if (hlsManifests.TryGetValue(ticket, out var window))
        {
            hlsManifests.Remove(ticket);
            foreach (var resource in window.Resources)
                RetireReferenceUnsafe(resource, ticket, window.Retention);
        }
        if (payload.Scope == TicketScope.Playback)
        {
            foreach (var key in nativeSessions.Where(pair => pair.Value == ticket).Select(pair => pair.Key).ToArray())
                nativeSessions.Remove(key);
            playbackCount--;
            hlsMutationGates.Remove(ticket);
            if (hlsTicketsByParent.TryGetValue(ticket, out var children))
            {
                foreach (var childTicket in children.Values.ToArray()) RemoveUnsafe(childTicket);
                hlsTicketsByParent.Remove(ticket);
            }
            hlsReferenceCounts.Remove(ticket);
        }
        else
        {
            hlsCount--;
            if (hlsTicketIndexes.TryGetValue(ticket, out var index))
            {
                hlsTicketIndexes.Remove(ticket);
                hlsReferenceCount -= index.References.Count;
                if (hlsReferenceCounts.TryGetValue(index.ParentTicket, out var referenceCount))
                {
                    if (referenceCount == index.References.Count) hlsReferenceCounts.Remove(index.ParentTicket);
                    else hlsReferenceCounts[index.ParentTicket] = referenceCount - index.References.Count;
                }
                foreach (var manifest in index.References.Keys)
                    if (hlsManifests.TryGetValue(manifest, out var owner)) owner.Resources.Remove(ticket);
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
        return HashBinding(normalized, userBindingSalt);
    }

    private byte[] HashDeviceId(string? value) =>
        HashBinding((value ?? string.Empty).Trim(), deviceBindingSalt);

    private static byte[] HashBinding(string value, byte[] salt)
    {
        if (value.Length == 0) return Array.Empty<byte>();
        using var hmac = new HMACSHA256(salt);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
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

        public Dictionary<string, DateTimeOffset?> References { get; } = new(StringComparer.Ordinal);
    }

    private sealed class HlsManifestWindow
    {
        public HlsManifestWindow(HashSet<string> resources, TimeSpan? retention)
        {
            Resources = resources;
            Retention = retention;
        }

        public HashSet<string> Resources { get; }
        public TimeSpan? Retention { get; }
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
