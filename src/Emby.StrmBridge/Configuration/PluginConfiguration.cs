using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Xml.Serialization;
using Emby.StrmBridge.Localization;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.LocalizationAttributes;

namespace Emby.StrmBridge.Configuration;

public enum PlaybackRoutingMode
{
    Native = 0,
    RedirectOnly = 1,
    Adaptive = 2,
    RelayOnly = 3,
}

public sealed class PluginConfiguration : EditableOptionsBase
{
    internal const int CurrentConfigurationVersion = 2;
    internal const int MaximumAllowedRedirectHosts = 256;
    internal const int MaximumAllowedRedirectHostTextLength = 64 * 1024;

    public override string EditorTitle => PluginStrings.EditorTitle;

    public override string EditorDescription => PluginStrings.EditorDescription;

    public override IEditObjectContainer CreateEditContainer()
    {
        var result = base.CreateEditContainer();
        if (result is not EditObjectContainer container) return result;

        var root = container.EditorRoot;
        root.DisplayName = EditorTitle;
        root.Description = EditorDescription;
        foreach (var editor in root.EditorItems)
        {
            var property = typeof(PluginConfiguration).GetProperty(
                editor.Name,
                BindingFlags.Instance | BindingFlags.Public);
            if (property is null) continue;

            if (property.GetCustomAttribute<DisplayNameLAttribute>() is { } displayName)
                editor.DisplayName = displayName.DisplayName;
            if (property.GetCustomAttribute<DescriptionLAttribute>() is { } description)
                editor.Description = description.Description;
        }

        return container;
    }

    [DisplayNameL(nameof(PluginStrings.Enabled), typeof(PluginStrings))]
    public bool Enabled { get; set; } = true;

    [DisplayNameL(nameof(PluginStrings.PlaybackMode), typeof(PluginStrings))]
    [DescriptionL(nameof(PluginStrings.PlaybackModeDescription), typeof(PluginStrings))]
    public PlaybackRoutingMode PlaybackMode { get; set; } = PlaybackRoutingMode.Adaptive;

    [Browsable(false)]
    public int ConfigurationVersion { get; set; } = CurrentConfigurationVersion;

    [DisplayNameL(nameof(PluginStrings.ExtractAfterLibraryScan), typeof(PluginStrings))]
    public bool ExtractAfterLibraryScan { get; set; }

    [DisplayNameL(nameof(PluginStrings.OnlyMissingMediaInfo), typeof(PluginStrings))]
    [DescriptionL(nameof(PluginStrings.OnlyMissingMediaInfoDescription), typeof(PluginStrings))]
    public bool OnlyMissingMediaInfo { get; set; } = true;

    [DisplayNameL(nameof(PluginStrings.EnablePersistence), typeof(PluginStrings))]
    public bool EnablePersistence { get; set; } = true;

    [DisplayNameL(nameof(PluginStrings.MaximumExtractionConcurrency), typeof(PluginStrings))]
    [MinValue(1)]
    [MaxValue(2)]
    public int MaximumExtractionConcurrency { get; set; } = 1;

    [DisplayNameL(nameof(PluginStrings.ExtractionTimeoutSeconds), typeof(PluginStrings))]
    [MinValue(30)]
    [MaxValue(180)]
    public int ExtractionTimeoutSeconds { get; set; } = 120;

    [DisplayNameL(nameof(PluginStrings.GatewayTimeoutSeconds), typeof(PluginStrings))]
    [MinValue(10)]
    [MaxValue(180)]
    public int GatewayTimeoutSeconds { get; set; } = 120;

    [DisplayNameL(nameof(PluginStrings.RedirectHopLimit), typeof(PluginStrings))]
    [MinValue(1)]
    [MaxValue(8)]
    public int RedirectHopLimit { get; set; } = 5;

    [DisplayNameL(nameof(PluginStrings.RelayConcurrency), typeof(PluginStrings))]
    [MinValue(1)]
    [MaxValue(16)]
    public int RelayConcurrency { get; set; } = 4;

    [Browsable(false)]
    public string[] IncludedLibraryIds { get; set; } = Array.Empty<string>();

    [Browsable(false)]
    [XmlIgnore]
    public EditorSelectOption[] AvailableLibraries { get; set; } = Array.Empty<EditorSelectOption>();

    [DisplayNameL(nameof(PluginStrings.IncludedLibraryIds), typeof(PluginStrings))]
    [DescriptionL(nameof(PluginStrings.IncludedLibraryIdsDescription), typeof(PluginStrings))]
    [EditMultilSelect]
    [SelectItemsSource(nameof(AvailableLibraries))]
    [XmlIgnore]
    public string? IncludedLibraries { get; set; }

    [Browsable(false)]
    public string[] AllowedRedirectHosts { get; set; } = Array.Empty<string>();

    [Browsable(false)]
    [XmlIgnore]
    public EditorSelectOption[] AvailableDetectedRedirectHosts { get; set; } = Array.Empty<EditorSelectOption>();

    [Browsable(false)]
    public string[] DetectedRedirectHostCatalog { get; set; } = Array.Empty<string>();

    [DisplayNameL(nameof(PluginStrings.DetectedRedirectHosts), typeof(PluginStrings))]
    [DescriptionL(nameof(PluginStrings.DetectedRedirectHostsDescription), typeof(PluginStrings))]
    [EditMultilSelect]
    [SelectItemsSource(nameof(AvailableDetectedRedirectHosts))]
    [XmlIgnore]
    public string? DetectedRedirectHostsToTrust { get; set; }

    [DisplayNameL(nameof(PluginStrings.AllowedRedirectHosts), typeof(PluginStrings))]
    [DescriptionL(nameof(PluginStrings.AllowedRedirectHostsDescription), typeof(PluginStrings))]
    [EditMultiline(3)]
    public string? AllowedRedirectHostsText { get; set; }

    public bool Normalize()
    {
        var changed = false;
        if (ConfigurationVersion != CurrentConfigurationVersion)
        {
            ConfigurationVersion = CurrentConfigurationVersion;
            changed = true;
        }
        if (!Enum.IsDefined(typeof(PlaybackRoutingMode), PlaybackMode))
        {
            PlaybackMode = PlaybackRoutingMode.Adaptive;
            changed = true;
        }
        var maximumExtractionConcurrency = NormalizeRange(MaximumExtractionConcurrency, 1, 2, 1);
        var extractionTimeoutSeconds = NormalizeRange(ExtractionTimeoutSeconds, 30, 180, 120);
        var gatewayTimeoutSeconds = NormalizeRange(GatewayTimeoutSeconds, 10, 180, 120);
        var redirectHopLimit = NormalizeRange(RedirectHopLimit, 1, 8, 5);
        var relayConcurrency = NormalizeRange(RelayConcurrency, 1, 16, 4);
        if (MaximumExtractionConcurrency != maximumExtractionConcurrency)
        {
            MaximumExtractionConcurrency = maximumExtractionConcurrency;
            changed = true;
        }
        if (ExtractionTimeoutSeconds != extractionTimeoutSeconds)
        {
            ExtractionTimeoutSeconds = extractionTimeoutSeconds;
            changed = true;
        }
        if (GatewayTimeoutSeconds != gatewayTimeoutSeconds)
        {
            GatewayTimeoutSeconds = gatewayTimeoutSeconds;
            changed = true;
        }
        if (RedirectHopLimit != redirectHopLimit)
        {
            RedirectHopLimit = redirectHopLimit;
            changed = true;
        }
        if (RelayConcurrency != relayConcurrency)
        {
            RelayConcurrency = relayConcurrency;
            changed = true;
        }

        var configuredIds = IncludedLibraries is null
            ? IncludedLibraryIds ?? Array.Empty<string>()
            : IncludedLibraries.Split(',');
        var normalizedIds = configuredIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Guid.TryParse(value.Trim(), out var id) ? id.ToString("N") : value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!(IncludedLibraryIds ?? Array.Empty<string>()).SequenceEqual(normalizedIds, StringComparer.Ordinal))
        {
            IncludedLibraryIds = normalizedIds;
            changed = true;
        }
        var normalizedLibrarySelection = string.Join(",", normalizedIds);
        if (!string.Equals(IncludedLibraries, normalizedLibrarySelection, StringComparison.Ordinal))
        {
            IncludedLibraries = normalizedLibrarySelection;
            changed = true;
        }

        var configuredHosts = AllowedRedirectHostsText is null ||
                              AllowedRedirectHostsText.Length > MaximumAllowedRedirectHostTextLength
            ? AllowedRedirectHosts ?? Array.Empty<string>()
            : AllowedRedirectHostsText.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var normalizedHosts = configuredHosts
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeHost)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumAllowedRedirectHosts)
            .ToArray();
        if (!(AllowedRedirectHosts ?? Array.Empty<string>()).SequenceEqual(normalizedHosts, StringComparer.Ordinal))
        {
            AllowedRedirectHosts = normalizedHosts;
            changed = true;
        }
        var normalizedHostText = string.Join(Environment.NewLine, normalizedHosts);
        if (!string.Equals(AllowedRedirectHostsText, normalizedHostText, StringComparison.Ordinal))
        {
            AllowedRedirectHostsText = normalizedHostText;
            changed = true;
        }
        return changed;
    }

    internal void MergeDetectedRedirectHostsToTrust(IEnumerable<string> detectedHostCatalog)
    {
        var catalog = new HashSet<string>(
            (detectedHostCatalog ?? Array.Empty<string>())
                .Select(NormalizeHost)
                .Where(IsValidExactHost),
            StringComparer.OrdinalIgnoreCase);
        var selected = (DetectedRedirectHostsToTrust ?? string.Empty)
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeHost)
            .Where(host => IsValidExactHost(host) && catalog.Contains(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selected.Length == 0) return;
        var configured = AllowedRedirectHostsText is null
            ? AllowedRedirectHosts ?? Array.Empty<string>()
            : AllowedRedirectHostsText.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        AllowedRedirectHostsText = string.Join(Environment.NewLine, configured.Concat(selected));
        DetectedRedirectHostsToTrust = null;
        Normalize();
    }

    protected override void Validate(ValidationContext context)
    {
        if (MaximumExtractionConcurrency is < 1 or > 2)
            context.AddValidationError(nameof(MaximumExtractionConcurrency), PluginStrings.ConcurrencyValidation);
        if (ExtractionTimeoutSeconds is < 30 or > 180 || GatewayTimeoutSeconds is < 10 or > 180)
            context.AddValidationError(nameof(ExtractionTimeoutSeconds), PluginStrings.TimeoutValidation);
        if (RedirectHopLimit is < 1 or > 8 || RelayConcurrency is < 1 or > 16)
            context.AddValidationError(nameof(RedirectHopLimit), PluginStrings.GatewayValidation);
        if ((IncludedLibraryIds ?? Array.Empty<string>()).Any(value => !Guid.TryParse(value, out _)))
            context.AddValidationError(nameof(IncludedLibraries), PluginStrings.LibraryValidation);
        var availableLibraryIds = Plugin.Instance?.GetLibraryIds();
        if (availableLibraryIds is not null &&
            (IncludedLibraryIds ?? Array.Empty<string>()).Any(value =>
                Guid.TryParse(value, out var id) && !availableLibraryIds.Contains(id)))
            context.AddValidationError(nameof(IncludedLibraries), PluginStrings.LibraryValidation);
        if ((AllowedRedirectHosts ?? Array.Empty<string>()).Any(value => !IsValidHost(value)))
            context.AddValidationError(nameof(AllowedRedirectHostsText), PluginStrings.RedirectHostValidation);
        if ((AllowedRedirectHosts ?? Array.Empty<string>()).Length > MaximumAllowedRedirectHosts ||
            (AllowedRedirectHostsText?.Length ?? 0) > MaximumAllowedRedirectHostTextLength)
            context.AddValidationError(nameof(AllowedRedirectHostsText), PluginStrings.RedirectHostCapacityValidation);
        if ((DetectedRedirectHostsToTrust ?? string.Empty)
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(value => !IsValidExactHost(value)))
            context.AddValidationError(nameof(DetectedRedirectHostsToTrust), PluginStrings.RedirectHostValidation);
    }

    internal PluginConfiguration Snapshot()
    {
        var snapshot = new PluginConfiguration
        {
            Enabled = Enabled,
            PlaybackMode = PlaybackMode,
            ConfigurationVersion = ConfigurationVersion,
            ExtractAfterLibraryScan = ExtractAfterLibraryScan,
            OnlyMissingMediaInfo = OnlyMissingMediaInfo,
            EnablePersistence = EnablePersistence,
            MaximumExtractionConcurrency = MaximumExtractionConcurrency,
            ExtractionTimeoutSeconds = ExtractionTimeoutSeconds,
            GatewayTimeoutSeconds = GatewayTimeoutSeconds,
            RedirectHopLimit = RedirectHopLimit,
            RelayConcurrency = RelayConcurrency,
            IncludedLibraryIds = (IncludedLibraryIds ?? Array.Empty<string>()).ToArray(),
            IncludedLibraries = IncludedLibraries,
            AllowedRedirectHosts = (AllowedRedirectHosts ?? Array.Empty<string>()).ToArray(),
            AllowedRedirectHostsText = AllowedRedirectHostsText,
            DetectedRedirectHostCatalog = (DetectedRedirectHostCatalog ?? Array.Empty<string>()).ToArray(),
        };
        snapshot.Normalize();
        return snapshot;
    }

    internal static string NormalizeHost(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimEnd('.');
        if (trimmed.Length == 0) return string.Empty;
        if (TryParseCidr(trimmed, out var network, out var prefix))
            return network + "/" + prefix.ToString(CultureInfo.InvariantCulture);
        var wildcard = trimmed.StartsWith("*.", StringComparison.Ordinal) ||
                       trimmed.StartsWith(".", StringComparison.Ordinal);
        var host = trimmed.StartsWith("*.", StringComparison.Ordinal)
            ? trimmed.Substring(2)
            : trimmed.StartsWith(".", StringComparison.Ordinal) ? trimmed.Substring(1) : trimmed;
        if (!wildcard && host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']')
            host = host.Substring(1, host.Length - 2);
        try { host = new IdnMapping().GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { host = host.ToLowerInvariant(); }
        return wildcard ? "*." + host : host;
    }

    internal static bool IsValidHost(string value)
    {
        var normalized = NormalizeHost(value);
        if (TryParseCidr(normalized, out _, out _)) return true;
        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = normalized.Substring(2);
            return suffix.Contains('.') && suffix.Length <= 251 && suffix.IndexOf('*') < 0 &&
                   Uri.CheckHostName(suffix) == UriHostNameType.Dns;
        }
        return IsValidExactHost(normalized);
    }

    internal static bool IsValidExactHost(string value)
    {
        var normalized = NormalizeHost(value);
        if (normalized.Length == 0 || normalized.Length > 253 || normalized.Contains('/') ||
            normalized.IndexOf('*') >= 0 ||
            normalized.IndexOfAny(new[] { '\\', '?', '#', '@', ':', ' ', '\t', '\r', '\n' }) >= 0)
            return IPAddress.TryParse(normalized, out _);
        return Uri.CheckHostName(normalized) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    internal static bool IsHostAllowed(string value, IEnumerable<string>? rules)
    {
        var host = NormalizeHost(value);
        if (!IsValidExactHost(host)) return false;
        foreach (var configured in rules ?? Array.Empty<string>())
        {
            var rule = NormalizeHost(configured);
            if (string.Equals(rule, host, StringComparison.OrdinalIgnoreCase)) return true;
            if (rule.StartsWith("*.", StringComparison.Ordinal) && IsValidHost(rule))
            {
                var suffix = rule.Substring(1);
                if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (IPAddress.TryParse(host, out var address) && TryParseCidr(rule, out var network, out var prefix) &&
                IsInNetwork(address, network, prefix)) return true;
        }
        return false;
    }

    private static int NormalizeRange(int value, int minimum, int maximum, int fallback) =>
        value >= minimum && value <= maximum ? value : fallback;

    private static bool TryParseCidr(string value, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1 ||
            !IPAddress.TryParse(value.Substring(0, separator), out network!) ||
            !int.TryParse(value.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out prefix))
            return false;
        var bits = network.GetAddressBytes().Length * 8;
        return prefix >= 0 && prefix <= bits;
    }

    private static bool IsInNetwork(IPAddress address, IPAddress network, int prefix)
    {
        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length) return false;
        var wholeBytes = prefix / 8;
        var remainingBits = prefix % 8;
        for (var index = 0; index < wholeBytes; index++)
            if (addressBytes[index] != networkBytes[index]) return false;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (addressBytes[wholeBytes] & mask) == (networkBytes[wholeBytes] & mask);
    }
}
