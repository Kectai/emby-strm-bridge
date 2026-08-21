using System;
using System.Collections.Generic;
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
        if (!Uri.TryCreate(location, UriKind.Absolute, out var target) ||
            (!string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Fragment))
        {
            throw new RedirectRejectedException(RedirectRejectionReason.UnsafeLocation);
        }
        if (CopiesCredentialValue(source, target))
        {
            throw new RedirectRejectedException(RedirectRejectionReason.CredentialPropagation);
        }
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

    private static bool CopiesCredentialValue(Uri source, Uri target)
    {
        var sourceValues = ParseQueryValues(source.Query)
            .Where(value => value.Length >= 12)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sourceValues.Length == 0) return false;

        string targetMaterial;
        try { targetMaterial = Uri.UnescapeDataString(target.Host + target.AbsolutePath + target.Query); }
        catch (UriFormatException)
        {
            throw new RedirectRejectedException(RedirectRejectionReason.InvalidLocation);
        }
        return sourceValues.Any(value => targetMaterial.IndexOf(value, StringComparison.Ordinal) >= 0);
    }

    private static IEnumerable<string> ParseQueryValues(string query)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0 || separator == pair.Length - 1) continue;
            string decoded;
            try { decoded = Uri.UnescapeDataString(pair.Substring(separator + 1)); }
            catch (UriFormatException) { continue; }
            yield return decoded;
        }
    }
}

public enum RedirectRejectionReason
{
    InvalidLocation,
    UnsafeLocation,
    CredentialPropagation,
    UntrustedTargetHost,
    InvalidUserAgent,
    UnexpectedStatus,
    PermanentFailure,
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
