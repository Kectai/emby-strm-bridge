using System;
using System.Linq;
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
