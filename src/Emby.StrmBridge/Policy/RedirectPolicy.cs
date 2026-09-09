using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Emby.StrmBridge.Configuration;

namespace Emby.StrmBridge.Policy;

public sealed class RedirectPolicy
{
    private readonly Func<string[]> allowedHostsProvider;
    private readonly Action<string>? untrustedHostObserver;

    public RedirectPolicy(
        Func<string[]>? allowedHostsProvider = null,
        Action<string>? untrustedHostObserver = null)
    {
        this.allowedHostsProvider = allowedHostsProvider ?? (() => Array.Empty<string>());
        this.untrustedHostObserver = untrustedHostObserver;
    }

    public Uri Validate(Uri source, string? location)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (string.IsNullOrWhiteSpace(location) || location.Any(char.IsControl))
        {
            throw new RedirectRejectedException(RedirectRejectionReason.InvalidLocation);
        }
        if (!Uri.TryCreate(source, location, out var target) ||
            (!string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Fragment))
        {
            throw new RedirectRejectedException(RedirectRejectionReason.UnsafeLocation);
        }
        if (string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new RedirectRejectedException(RedirectRejectionReason.InsecureDowngrade);
        if (!IsTrustedTargetHost(source, target))
        {
            untrustedHostObserver?.Invoke(PluginConfiguration.NormalizeHost(target.IdnHost));
            throw new RedirectRejectedException(RedirectRejectionReason.UntrustedTargetHost);
        }
        return target;
    }

    private bool IsTrustedTargetHost(Uri source, Uri target)
    {
        var sourceHost = PluginConfiguration.NormalizeHost(source.IdnHost);
        var targetHost = PluginConfiguration.NormalizeHost(target.IdnHost);
        if (string.Equals(sourceHost, targetHost, StringComparison.OrdinalIgnoreCase)) return true;
        return PluginConfiguration.IsHostAllowed(targetHost, allowedHostsProvider());
    }

    internal string GetTransportTrustKey() => string.Join("\n",
        (allowedHostsProvider() ?? Array.Empty<string>())
            .Select(PluginConfiguration.NormalizeHost)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));

    internal void ValidateAddress(Uri target, IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IsPublicAddress(address)) return;
        if (IPAddress.TryParse(target.IdnHost.Trim('[', ']'), out var literal) &&
            address.Equals(literal.IsIPv4MappedToIPv6 ? literal.MapToIPv4() : literal)) return;
        if (PluginConfiguration.IsHostAllowed(address.ToString(), allowedHostsProvider())) return;
        untrustedHostObserver?.Invoke(address.ToString());
        throw new RedirectRejectedException(RedirectRejectionReason.UntrustedTargetHost);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
                   !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                   !(bytes[0] == 169 && bytes[1] == 254) &&
                   !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                   !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2)) &&
                   !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)) &&
                   !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        // Only global unicast; translated, link-local, unique-local and multicast addresses require approval.
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
               (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
               !(bytes[0] == 0x20 && bytes[1] == 0x01 &&
                 (bytes[2] == 0 && bytes[3] == 0 || bytes[2] == 0x0d && bytes[3] == 0xb8));
    }

    internal static RedirectRejectedException? FindRejection(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is RedirectRejectedException rejection) return rejection;
        return null;
    }

}

public enum RedirectRejectionReason
{
    InvalidLocation,
    UnsafeLocation,
    InsecureDowngrade,
    UntrustedTargetHost,
    InvalidUserAgent,
    UnexpectedStatus,
    PermanentFailure,
    TooManyRedirects,
}

public sealed class RedirectRejectedException : Exception
{
    public RedirectRejectedException(RedirectRejectionReason reason)
        : base("The remote redirect did not satisfy the configured safety policy.")
    {
        Reason = reason;
    }

    public RedirectRejectionReason Reason { get; }
}
