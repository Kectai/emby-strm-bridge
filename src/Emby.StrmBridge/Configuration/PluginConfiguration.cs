using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Xml.Serialization;
using Emby.StrmBridge.Localization;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;

namespace Emby.StrmBridge.Configuration;

public sealed class PluginConfiguration : EditableOptionsBase
{
    internal const int CurrentConfigurationVersion = 1;
    internal const int MaximumAllowedRedirectHosts = 256;
    internal const int MaximumAllowedRedirectHostTextLength = 64 * 1024;

    public override string EditorTitle => PluginStrings.EditorTitle;

    public override string EditorDescription => PluginStrings.EditorDescription;

    [DisplayNameL(nameof(PluginStrings.Enabled), typeof(PluginStrings))]
    public bool Enabled { get; set; } = true;

    [DisplayNameL(nameof(PluginStrings.EnablePlaybackSource), typeof(PluginStrings))]
    [DescriptionL(nameof(PluginStrings.EnablePlaybackSourceDescription), typeof(PluginStrings))]
    public bool EnablePlaybackSource { get; set; } = true;

    [Browsable(false)]
    public int ConfigurationVersion { get; set; }

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
    public string? DetectedRedirectHostsAvailable { get; set; }

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
        if (ConfigurationVersion < CurrentConfigurationVersion)
        {
            EnablePlaybackSource = true;
            ConfigurationVersion = CurrentConfigurationVersion;
            changed = true;
        }
        if (MaximumExtractionConcurrency < 1 || MaximumExtractionConcurrency > 2)
        {
            MaximumExtractionConcurrency = 1;
            changed = true;
        }
        if (ExtractionTimeoutSeconds < 30 || ExtractionTimeoutSeconds > 180)
        {
            ExtractionTimeoutSeconds = 120;
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
        if (MaximumExtractionConcurrency < 1 || MaximumExtractionConcurrency > 2)
            context.AddValidationError(nameof(MaximumExtractionConcurrency), PluginStrings.ConcurrencyValidation);
        if (ExtractionTimeoutSeconds < 30 || ExtractionTimeoutSeconds > 180)
            context.AddValidationError(nameof(ExtractionTimeoutSeconds), PluginStrings.TimeoutValidation);
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
            EnablePlaybackSource = EnablePlaybackSource,
            ConfigurationVersion = ConfigurationVersion,
            ExtractAfterLibraryScan = ExtractAfterLibraryScan,
            OnlyMissingMediaInfo = OnlyMissingMediaInfo,
            EnablePersistence = EnablePersistence,
            MaximumExtractionConcurrency = MaximumExtractionConcurrency,
            ExtractionTimeoutSeconds = ExtractionTimeoutSeconds,
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
        var wildcard = trimmed.StartsWith("*.", StringComparison.Ordinal);
        var host = wildcard ? trimmed.Substring(2) : trimmed;
        if (!wildcard && host[0] == '[' && host[host.Length - 1] == ']')
            host = host.Substring(1, host.Length - 2);
        try
        {
            host = new IdnMapping().GetAscii(host).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            host = host.ToLowerInvariant();
        }
        return wildcard ? "*." + host : host;
    }

    internal static bool IsValidHost(string value)
    {
        var normalized = NormalizeHost(value);
        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = normalized.Substring(2);
            return suffix.Contains('.') &&
                   suffix.Length <= 251 &&
                   suffix.IndexOf('*') < 0 &&
                   Uri.CheckHostName(suffix) == UriHostNameType.Dns;
        }
        return IsValidExactHost(normalized);
    }

    internal static bool IsValidExactHost(string value)
    {
        var normalized = NormalizeHost(value);
        if (normalized.Length == 0 || normalized.Length > 253 ||
            normalized.IndexOf('*') >= 0 ||
            normalized.IndexOfAny(new[] { '/', '\\', '?', '#', '@', ':', ' ', '\t', '\r', '\n' }) >= 0)
            return System.Net.IPAddress.TryParse(normalized, out _);
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
            if (!rule.StartsWith("*.", StringComparison.Ordinal) || !IsValidHost(rule)) continue;
            var suffix = rule.Substring(1);
            if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
