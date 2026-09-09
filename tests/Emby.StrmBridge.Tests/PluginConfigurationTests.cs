using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Runtime;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Entities;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class PluginConfigurationTests
{
    [TestMethod]
    public void NativeEditor_ContainsAllAdministratorSettings()
    {
        var configuration = new PluginConfiguration
        {
            AvailableLibraries = new[]
            {
                new EditorSelectOption { Value = Guid.Empty.ToString("N"), Name = "Library", IsEnabled = true },
            },
            AvailableDetectedRedirectHosts = new[]
            {
                new EditorSelectOption { Value = "detected.invalid", Name = "detected.invalid", IsEnabled = true },
            },
        };
        var container = (EditObjectContainer)configuration.CreateEditContainer();
        var ids = container.EditorRoot.EditorItems.Select(item => item.Id).ToArray();

        CollectionAssert.IsSubsetOf(new[]
        {
            nameof(PluginConfiguration.Enabled),
            nameof(PluginConfiguration.PlaybackMode),
            nameof(PluginConfiguration.EnableFastSeek),
            nameof(PluginConfiguration.ExtractAfterLibraryScan),
            nameof(PluginConfiguration.OnlyMissingMediaInfo),
            nameof(PluginConfiguration.EnablePersistence),
            nameof(PluginConfiguration.MaximumExtractionConcurrency),
            nameof(PluginConfiguration.ExtractionTimeoutSeconds),
            nameof(PluginConfiguration.GatewayTimeoutSeconds),
            nameof(PluginConfiguration.DirectRedirectCacheSeconds),
            nameof(PluginConfiguration.RedirectHopLimit),
            nameof(PluginConfiguration.RelayConcurrency),
            nameof(PluginConfiguration.IncludedLibraries),
            nameof(PluginConfiguration.DetectedRedirectHostsToTrust),
            nameof(PluginConfiguration.AllowedRedirectHostsText),
        }, ids);
        Assert.DoesNotContain(nameof(PluginConfiguration.AllowedRedirectHosts), ids);
        var libraryEditor = (EditorSelectMultiple)container.EditorRoot.EditorItems.Single(item =>
            item.Id == nameof(PluginConfiguration.IncludedLibraries));
        var detectedEditor = (EditorSelectMultiple)container.EditorRoot.EditorItems.Single(item =>
            item.Id == nameof(PluginConfiguration.DetectedRedirectHostsToTrust));
        Assert.AreEqual(nameof(PluginConfiguration.AvailableLibraries), libraryEditor.ItemsSourceId);
        Assert.AreEqual(nameof(PluginConfiguration.AvailableDetectedRedirectHosts), detectedEditor.ItemsSourceId);
        var editObject = (PluginConfiguration)container.Object;
        Assert.AreEqual("Library", editObject.AvailableLibraries.Single().Name);
        Assert.AreEqual("detected.invalid", editObject.AvailableDetectedRedirectHosts.Single().Name);
        var hostsEditor = (EditorText)container.EditorRoot.EditorItems.Single(item =>
            item.Id == nameof(PluginConfiguration.AllowedRedirectHostsText));
        Assert.IsTrue(hostsEditor.MultiLine);
        Assert.AreEqual(3, hostsEditor.LineCount);
    }

    [TestMethod]
    public void NativeEditor_ShowsDetectedHostControlWithoutPuttingPromptInOptions()
    {
        var configuration = new PluginConfiguration();
        var container = (EditObjectContainer)configuration.CreateEditContainer();

        Assert.IsInstanceOfType<EditorSelectMultiple>(container.EditorRoot.EditorItems.Single(item =>
            item.Id == nameof(PluginConfiguration.DetectedRedirectHostsToTrust)));
        Assert.IsEmpty(((PluginConfiguration)container.Object).AvailableDetectedRedirectHosts);
    }

    [TestMethod]
    public void PlaybackGateway_DefaultsToAdaptiveAndNormalizesInvalidValues()
    {
        var fresh = new PluginConfiguration();
        Assert.AreEqual(PlaybackRoutingMode.Adaptive, fresh.PlaybackMode);
        Assert.IsTrue(fresh.EnableFastSeek);
        Assert.AreEqual(20, fresh.DirectRedirectCacheSeconds);
        Assert.IsTrue(fresh.Normalize());
        Assert.IsFalse(fresh.Normalize());
        Assert.AreEqual(PluginConfiguration.CurrentConfigurationVersion, fresh.ConfigurationVersion);

        var invalid = new PluginConfiguration
        {
            ConfigurationVersion = 0,
            PlaybackMode = (PlaybackRoutingMode)99,
            GatewayTimeoutSeconds = int.MaxValue,
            DirectRedirectCacheSeconds = int.MaxValue,
            RedirectHopLimit = int.MaxValue,
            RelayConcurrency = int.MaxValue,
        };
        Assert.IsTrue(invalid.Normalize());
        Assert.AreEqual(PluginConfiguration.CurrentConfigurationVersion, invalid.ConfigurationVersion);
        Assert.AreEqual(PlaybackRoutingMode.Adaptive, invalid.PlaybackMode);
        Assert.AreEqual(120, invalid.GatewayTimeoutSeconds);
        Assert.AreEqual(20, invalid.DirectRedirectCacheSeconds);
        Assert.AreEqual(5, invalid.RedirectHopLimit);
        Assert.AreEqual(4, invalid.RelayConcurrency);
    }

    [TestMethod]
    public void DetectedHostOptions_KeepTrustedDetectionsVisibleButUnselected()
    {
        var detected = new[] { "edge.cdn.example.invalid", "pending.invalid" };
        var allowed = new[] { "*.CDN.EXAMPLE.INVALID" };

        var options = Plugin.CreateDetectedRedirectHostOptions(detected, allowed);
        Assert.AreEqual(2, options.Length);
        Assert.IsFalse(options[0].IsEnabled);
        Assert.IsTrue(options[0].Name.StartsWith("edge.cdn.example.invalid", StringComparison.Ordinal));
        Assert.IsTrue(options[1].IsEnabled);
        Assert.AreEqual("pending.invalid", options[1].Name);
    }

    [TestMethod]
    public void DetectedHostCatalog_KeepsRememberedTrustedHostsAndRejectsInvalidValues()
    {
        var remembered = new[]
        {
            new EditorSelectOption { Value = "Remembered.Example.Invalid.", Name = "ignored", IsEnabled = false },
            new EditorSelectOption { Value = "https://not-a-host.invalid/path", Name = "ignored", IsEnabled = true },
            new EditorSelectOption { Value = "duplicate.invalid", Name = "ignored", IsEnabled = true },
        };

        var catalog = Plugin.MergeDetectedRedirectHostCatalog(
            new[] { "duplicate.invalid", "current.invalid" },
            remembered);

        CollectionAssert.AreEqual(
            new[] { "duplicate.invalid", "current.invalid", "remembered.example.invalid" },
            catalog);
    }

    [TestMethod]
    public void DetectedHostCatalog_EnforcesHardCapacity()
    {
        var remembered = Enumerable.Range(0, PluginRuntime.MaximumDetectedRedirectHosts + 5)
            .Select(index => new EditorSelectOption
            {
                Value = $"host-{index}.example.invalid",
                Name = "ignored",
                IsEnabled = true,
            });

        var catalog = Plugin.MergeDetectedRedirectHostCatalog(Array.Empty<string>(), remembered);

        Assert.AreEqual(PluginRuntime.MaximumDetectedRedirectHosts, catalog.Length);
    }

    [TestMethod]
    public void ValidationAndNormalization_RejectTamperedRangesAndLibraryIds()
    {
        Assert.ThrowsExactly<ValidationException>(() =>
            new PluginConfiguration { MaximumExtractionConcurrency = 3 }.ValidateOrThrow());
        Assert.ThrowsExactly<ValidationException>(() =>
            new PluginConfiguration { ExtractionTimeoutSeconds = 5 }.ValidateOrThrow());
        Assert.ThrowsExactly<ValidationException>(() =>
            new PluginConfiguration { DirectRedirectCacheSeconds = -1 }.ValidateOrThrow());
        Assert.ThrowsExactly<ValidationException>(() =>
            new PluginConfiguration { DirectRedirectCacheSeconds = 61 }.ValidateOrThrow());
        Assert.ThrowsExactly<ValidationException>(() =>
            new PluginConfiguration { IncludedLibraryIds = new[] { "not-a-guid" } }.ValidateOrThrow());

        var options = new PluginConfiguration
        {
            MaximumExtractionConcurrency = int.MaxValue,
            ExtractionTimeoutSeconds = int.MaxValue,
            DirectRedirectCacheSeconds = int.MaxValue,
            IncludedLibraryIds = new[] { " " + Guid.Empty + " ", Guid.Empty.ToString() },
        };
        Assert.IsTrue(options.Normalize());
        Assert.AreEqual(1, options.MaximumExtractionConcurrency);
        Assert.AreEqual(120, options.ExtractionTimeoutSeconds);
        Assert.AreEqual(20, options.DirectRedirectCacheSeconds);
        Assert.AreEqual(1, options.IncludedLibraryIds.Length);
        Assert.AreEqual(Guid.Empty.ToString("N"), options.IncludedLibraryIds[0]);
        Assert.AreEqual(Guid.Empty.ToString("N"), options.IncludedLibraries);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var selected = new PluginConfiguration
        {
            IncludedLibraries = first.ToString("D") + ", " + second.ToString("N"),
        };
        Assert.IsTrue(selected.Normalize());
        CollectionAssert.AreEqual(
            new[] { first.ToString("N"), second.ToString("N") },
            selected.IncludedLibraryIds);
    }

    [TestMethod]
    public void DirectRedirectCache_AcceptsBoundariesAndSurvivesSnapshot()
    {
        new PluginConfiguration { DirectRedirectCacheSeconds = 0 }.ValidateOrThrow();
        new PluginConfiguration { DirectRedirectCacheSeconds = 60 }.ValidateOrThrow();

        var options = new PluginConfiguration { DirectRedirectCacheSeconds = 37 };
        var snapshot = options.Snapshot();

        Assert.AreEqual(37, snapshot.DirectRedirectCacheSeconds);
        Assert.AreEqual(37, options.DirectRedirectCacheSeconds);
    }

    [TestMethod]
    public void RedirectHostValidation_AcceptsExactHostsSubdomainRulesAndCidr()
    {
        foreach (var invalid in new[]
                 {
                     "https://media.invalid",
                     "*media.example.invalid",
                     "*.*.media.example.invalid",
                     "*.invalid",
                     "*.127.0.0.1",
                     "media.invalid:443",
                     "media.invalid/path",
                     "user@media.invalid",
                 })
        {
            Assert.ThrowsExactly<ValidationException>(() =>
                new PluginConfiguration { AllowedRedirectHosts = new[] { invalid } }.ValidateOrThrow());
        }

        var options = new PluginConfiguration
        {
            AllowedRedirectHosts = new[]
            {
                " MEDIA.INVALID. ",
                "media.invalid",
                " *.CDN.EXAMPLE.INVALID. ",
                "[::1]",
                "192.0.2.0/24",
            },
        };
        Assert.IsTrue(options.Normalize());
        CollectionAssert.AreEqual(
            new[] { "media.invalid", "*.cdn.example.invalid", "::1", "192.0.2.0/24" },
            options.AllowedRedirectHosts);
        options.ValidateOrThrow();

        var textOptions = new PluginConfiguration
        {
            AllowedRedirectHostsText = " MEDIA.INVALID.\nmedia.invalid; *.CDN.EXAMPLE.INVALID.; [::1]",
        };
        Assert.IsTrue(textOptions.Normalize());
        CollectionAssert.AreEqual(
            new[] { "media.invalid", "*.cdn.example.invalid", "::1" },
            textOptions.AllowedRedirectHosts);
        Assert.AreEqual(
            "media.invalid" + Environment.NewLine + "*.cdn.example.invalid" + Environment.NewLine + "::1",
            textOptions.AllowedRedirectHostsText);
        textOptions.ValidateOrThrow();

        var detectedOptions = new PluginConfiguration
        {
            AllowedRedirectHostsText = "existing.invalid",
            DetectedRedirectHostsToTrust = "detected.invalid, existing.invalid, forged.invalid, *.example.invalid",
        };
        detectedOptions.MergeDetectedRedirectHostsToTrust(new[] { "detected.invalid", "existing.invalid" });
        CollectionAssert.AreEqual(
            new[] { "existing.invalid", "detected.invalid" },
            detectedOptions.AllowedRedirectHosts);
        Assert.IsNull(detectedOptions.DetectedRedirectHostsToTrust);
    }

    [TestMethod]
    public void RedirectHostRules_EnforceHardCapacityAndTextLimit()
    {
        var tooMany = Enumerable.Range(0, PluginConfiguration.MaximumAllowedRedirectHosts + 1)
            .Select(index => $"host-{index}.example.invalid")
            .ToArray();
        Assert.ThrowsExactly<ValidationException>(() =>
            new PluginConfiguration { AllowedRedirectHosts = tooMany }.ValidateOrThrow());
        Assert.ThrowsExactly<ValidationException>(() =>
            new PluginConfiguration
            {
                AllowedRedirectHostsText = new string('a',
                    PluginConfiguration.MaximumAllowedRedirectHostTextLength + 1),
            }.ValidateOrThrow());

        var normalized = new PluginConfiguration { AllowedRedirectHosts = tooMany };
        Assert.IsTrue(normalized.Normalize());
        Assert.AreEqual(
            PluginConfiguration.MaximumAllowedRedirectHosts,
            normalized.AllowedRedirectHosts.Length);
    }

    [TestMethod]
    public void DetectedHostSelection_DoesNotReAddManuallyRemovedTrust()
    {
        var options = new PluginConfiguration
        {
            AllowedRedirectHostsText = string.Empty,
            DetectedRedirectHostsToTrust = string.Empty,
        };

        options.MergeDetectedRedirectHostsToTrust(new[] { "edge.cdn.example.invalid" });
        options.Normalize();

        Assert.IsEmpty(options.AllowedRedirectHosts);
    }

    [TestMethod]
    public void XmlPersistence_ExcludesUiPresentationFieldsAndLibraryNames()
    {
        var options = new PluginConfiguration
        {
            DirectRedirectCacheSeconds = 37,
            IncludedLibraryIds = new[] { Guid.Empty.ToString("N") },
            IncludedLibraries = Guid.Empty.ToString("N"),
            AvailableLibraries = new[]
            {
                new EditorSelectOption { Value = Guid.Empty.ToString("N"), Name = "Private library name" },
            },
            DetectedRedirectHostCatalog = new[] { "detected.invalid" },
            AvailableDetectedRedirectHosts = new[]
            {
                new EditorSelectOption { Value = "detected.invalid", Name = "detected.invalid (trusted)" },
            },
            DetectedRedirectHostsToTrust = "detected.invalid",
        };
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();

        serializer.Serialize(writer, options);
        var xml = writer.ToString();

        Assert.IsFalse(xml.Contains("Private library name", StringComparison.Ordinal));
        Assert.IsFalse(xml.Contains(nameof(PluginConfiguration.AvailableLibraries), StringComparison.Ordinal));
        Assert.IsFalse(xml.Contains(
            nameof(PluginConfiguration.AvailableDetectedRedirectHosts),
            StringComparison.Ordinal));
        Assert.IsFalse(xml.Contains(
            nameof(PluginConfiguration.DetectedRedirectHostsToTrust),
            StringComparison.Ordinal));
        StringAssert.Contains(xml, "<DirectRedirectCacheSeconds>37</DirectRedirectCacheSeconds>");
        StringAssert.Contains(xml, "detected.invalid");
    }

    [TestMethod]
    public void LibraryOptions_UseVirtualFolderItemIdsAndStableOrdering()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var options = Plugin.CreateLibraryOptions(new[]
        {
            new VirtualFolderInfo { Guid = second.ToString("D"), ItemId = "42", Name = "Series" },
            new VirtualFolderInfo { ItemId = first.ToString("N"), Name = "Movies" },
            new VirtualFolderInfo { ItemId = second.ToString("N"), Name = "Duplicate" },
            new VirtualFolderInfo { ItemId = "not-an-item-id", Name = "Invalid" },
        });

        Assert.AreEqual(2, options.Length);
        Assert.AreEqual("Movies", options[0].Name);
        Assert.AreEqual(first.ToString("N"), options[0].Value);
        Assert.AreEqual("Series", options[1].Name);
        Assert.AreEqual(second.ToString("N"), options[1].Value);
        Assert.IsTrue(options.All(option => option.IsEnabled));
    }

    [TestMethod]
    public void LibraryOptions_UseRootCollectionFolderIdsAndStableOrdering()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var options = Plugin.CreateLibraryOptions(new MediaBrowser.Controller.Entities.Folder[]
        {
            new() { Id = second, Name = "Series" },
            new() { Id = first, Name = "Movies" },
            new() { Id = second, Name = "Duplicate" },
            new() { Id = Guid.Empty, Name = "Invalid" },
        });

        Assert.AreEqual(2, options.Length);
        Assert.AreEqual("Movies", options[0].Name);
        Assert.AreEqual(first.ToString("N"), options[0].Value);
        Assert.AreEqual("Series", options[1].Name);
        Assert.AreEqual(second.ToString("N"), options[1].Value);
        Assert.IsTrue(options.All(option => option.IsEnabled));
    }
}
