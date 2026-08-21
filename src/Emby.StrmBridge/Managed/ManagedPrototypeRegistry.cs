using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.StrmBridge.Domain;
using Emby.StrmBridge.Persistence;
using Emby.StrmBridge.Playback;

namespace Emby.StrmBridge.Managed;

internal sealed class ManagedPrototypeRegistry : IDisposable
{
    public const int MaximumEntries = 8;
    public const int MaximumSourceLength = 4096;
    public static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(1);
    private const int TokenByteLength = 32;
    private const int TokenTextLength = 43;
    private const string RoutePrefix = "/StrmBridge/Managed/v1/";
    private static readonly HashSet<string> AllowedContainerHints = new(
        new[] { "avi", "flv", "iso", "m2ts", "m4v", "mkv", "mov", "mp4", "mpeg", "mpg", "ogv", "ts", "webm", "wmv" },
        StringComparer.Ordinal);

    private readonly object sync = new();
    private readonly Dictionary<string, PrototypeEntry> entries = new(StringComparer.Ordinal);
    private readonly byte[] fingerprintKey = new byte[32];
    private readonly IClock clock;
    private bool disposed;

    public ManagedPrototypeRegistry(IClock clock)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(fingerprintKey);
    }

    public int Count
    {
        get
        {
            lock (sync)
            {
                ThrowIfDisposed();
                RemoveExpiredUnsafe(clock.UtcNow);
                return entries.Count;
            }
        }
    }

    internal ManagedPrototypeRegistration Create(string sourceUrl, string? containerHint)
    {
        var source = ParseSource(sourceUrl);
        var normalizedHint = NormalizeContainerHint(containerHint);
        var now = clock.UtcNow;
        lock (sync)
        {
            ThrowIfDisposed();
            RemoveExpiredUnsafe(now);
            if (entries.Count >= MaximumEntries)
                throw new ManagedPrototypeRegistrationException(ManagedPrototypeRegistrationFailure.CapacityExceeded);

            string token;
            do { token = CreateToken(); }
            while (entries.ContainsKey(token));

            var entry = new PrototypeEntry(
                token,
                CreateSourceIdentity(token, source, now),
                now,
                now + EntryLifetime);
            entries.Add(token, entry);
            return new ManagedPrototypeRegistration(
                RoutePrefix + token + (normalizedHint is null ? string.Empty : "." + normalizedHint),
                entry.ExpiresAtUtc);
        }
    }

    internal async Task<RedirectLease> ResolveAsync(
        string routeValue,
        string? userAgent,
        RedirectResolver resolver,
        CancellationToken cancellationToken)
    {
        if (resolver is null) throw new ArgumentNullException(nameof(resolver));
        var entry = GetActiveEntry(routeValue);
        var lease = await resolver.ResolveAsync(entry.Source, userAgent, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ThrowIfDisposed();
            RemoveExpiredUnsafe(clock.UtcNow);
            if (!entries.TryGetValue(entry.Token, out var current) ||
                !ReferenceEquals(current, entry) || !lease.IsValidAt(clock.UtcNow))
                throw new ManagedPrototypeUnavailableException();
        }
        return lease;
    }

    internal bool IsActive(string routeValue)
    {
        if (!TryParseRouteValue(routeValue, out var token, out _)) return false;
        lock (sync)
        {
            if (disposed) return false;
            RemoveExpiredUnsafe(clock.UtcNow);
            return entries.ContainsKey(token);
        }
    }

    public int Clear()
    {
        lock (sync)
        {
            if (disposed) return 0;
            var count = entries.Count;
            entries.Clear();
            return count;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            entries.Clear();
            Array.Clear(fingerprintKey, 0, fingerprintKey.Length);
        }
    }

    internal static bool TryParseRouteValue(string? routeValue, out string token, out string? containerHint)
    {
        token = string.Empty;
        containerHint = null;
        if (string.IsNullOrEmpty(routeValue) || routeValue.Length < TokenTextLength || routeValue.Any(char.IsControl))
            return false;
        token = routeValue.Substring(0, TokenTextLength);
        if (!IsBase64UrlToken(token))
        {
            token = string.Empty;
            return false;
        }
        if (routeValue.Length == TokenTextLength) return true;
        if (routeValue[TokenTextLength] != '.')
        {
            token = string.Empty;
            return false;
        }
        var suffix = routeValue.Substring(TokenTextLength + 1);
        if (!AllowedContainerHints.Contains(suffix))
        {
            token = string.Empty;
            return false;
        }
        containerHint = suffix;
        return true;
    }

    private PrototypeEntry GetActiveEntry(string routeValue)
    {
        if (!TryParseRouteValue(routeValue, out var token, out _))
            throw new ManagedPrototypeUnavailableException();
        lock (sync)
        {
            ThrowIfDisposed();
            RemoveExpiredUnsafe(clock.UtcNow);
            if (!entries.TryGetValue(token, out var entry)) throw new ManagedPrototypeUnavailableException();
            return entry;
        }
    }

    private SourceIdentity CreateSourceIdentity(string token, Uri source, DateTimeOffset createdAtUtc) => new(
        ComputeFingerprint("storage:" + token),
        ComputeFingerprint("source:" + source.AbsoluteUri),
        source,
        string.Empty,
        0,
        createdAtUtc);

    private string ComputeFingerprint(string value)
    {
        using var hmac = new HMACSHA256(fingerprintKey);
        return HmacIdentityProvider.ToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static Uri ParseSource(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl) || sourceUrl.Length > MaximumSourceLength ||
            sourceUrl.Any(char.IsControl) || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) ||
            (!string.Equals(source.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(source.UserInfo) || !string.IsNullOrEmpty(source.Fragment))
            throw new ManagedPrototypeRegistrationException(ManagedPrototypeRegistrationFailure.InvalidSource);
        return source;
    }

    private static string? NormalizeContainerHint(string? containerHint)
    {
        if (string.IsNullOrWhiteSpace(containerHint)) return null;
        var normalized = containerHint.Trim().TrimStart('.').ToLowerInvariant();
        if (!AllowedContainerHints.Contains(normalized))
            throw new ManagedPrototypeRegistrationException(
                ManagedPrototypeRegistrationFailure.InvalidContainerHint);
        return normalized;
    }

    private static bool IsBase64UrlToken(string value) => value.Length == TokenTextLength && value.All(character =>
        character >= 'A' && character <= 'Z' ||
        character >= 'a' && character <= 'z' ||
        character >= '0' && character <= '9' || character == '-' || character == '_');

    private static string CreateToken()
    {
        var bytes = new byte[TokenByteLength];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void RemoveExpiredUnsafe(DateTimeOffset now)
    {
        foreach (var token in entries.Where(pair => now >= pair.Value.ExpiresAtUtc).Select(pair => pair.Key).ToArray())
            entries.Remove(token);
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(ManagedPrototypeRegistry));
    }

    private sealed class PrototypeEntry
    {
        public PrototypeEntry(
            string token,
            SourceIdentity source,
            DateTimeOffset createdAtUtc,
            DateTimeOffset expiresAtUtc)
        {
            Token = token;
            Source = source;
            CreatedAtUtc = createdAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string Token { get; }
        public SourceIdentity Source { get; }
        public DateTimeOffset CreatedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
    }
}

internal readonly struct ManagedPrototypeRegistration
{
    public ManagedPrototypeRegistration(string relativePath, DateTimeOffset expiresAtUtc)
    {
        RelativePath = relativePath;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string RelativePath { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
}

internal enum ManagedPrototypeRegistrationFailure
{
    InvalidSource,
    InvalidContainerHint,
    CapacityExceeded,
}

internal sealed class ManagedPrototypeRegistrationException : Exception
{
    public ManagedPrototypeRegistrationException(ManagedPrototypeRegistrationFailure reason)
        : base("The managed STRM prototype registration is invalid.") => Reason = reason;

    public ManagedPrototypeRegistrationFailure Reason { get; }
}

internal sealed class ManagedPrototypeUnavailableException : Exception
{
    public ManagedPrototypeUnavailableException()
        : base("The managed STRM prototype is unavailable.") { }
}
