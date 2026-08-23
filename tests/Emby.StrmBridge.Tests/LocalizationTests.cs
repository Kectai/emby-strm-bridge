using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Extraction;
using Emby.StrmBridge.Localization;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.LocalizationAttributes;

namespace Emby.StrmBridge.Tests;

[TestClass]
public sealed class LocalizationTests
{
    [TestMethod]
    public void AdministratorText_UsesRequestedChineseVariantAndFallsBackToEnglish()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Assert.AreEqual("STRM 桥接", new PluginConfiguration().EditorTitle);
            Assert.IsTrue(PluginStrings.DetectedRedirectHostsDescription.Contains("自动重试", StringComparison.Ordinal));
            Assert.IsTrue(PluginStrings.PlaybackModeDescription.Contains("普通文件直放", StringComparison.Ordinal));
            Assert.AreEqual("媒体库", new ExtractionScheduledTask().Category);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-TW");
            Assert.AreEqual("STRM 橋接", new PluginConfiguration().EditorTitle);
            Assert.IsTrue(PluginStrings.DetectedRedirectHostsDescription.Contains("自動重試", StringComparison.Ordinal));
            Assert.IsTrue(PluginStrings.PlaybackModeDescription.Contains("一般檔案直放", StringComparison.Ordinal));
            Assert.AreEqual("媒體庫", new ClearStoredMediaInfoScheduledTask().Category);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.AreEqual("STRM Bridge", new PluginConfiguration().EditorTitle);
            Assert.IsTrue(PluginStrings.DetectedRedirectHostsDescription.Contains("automatically retries", StringComparison.Ordinal));
            Assert.IsTrue(PluginStrings.PlaybackModeDescription.Contains("Adaptive (recommended)", StringComparison.Ordinal));
            Assert.AreEqual("Library", new ExtractionScheduledTask().Category);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [TestMethod]
    public void EveryPublicLocalizationPropertyExistsInEverySupportedResourceSet()
    {
        var propertyNames = typeof(PluginStrings).GetProperties(
                BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        foreach (var resourceBaseName in new[]
                 {
                     "Emby.StrmBridge.Localization.PluginStrings",
                     "Emby.StrmBridge.Localization.PluginStringsZhHans",
                     "Emby.StrmBridge.Localization.PluginStringsZhHant",
                 })
        {
            var manager = new ResourceManager(resourceBaseName, typeof(PluginStrings).Assembly);
            var resourceSet = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false);
            Assert.IsNotNull(resourceSet, $"Missing embedded resource set {resourceBaseName}.");
            var values = resourceSet.Cast<DictionaryEntry>()
                .ToDictionary(
                    entry => (string)entry.Key,
                    entry => entry.Value as string,
                    StringComparer.Ordinal);
            CollectionAssert.AreEquivalent(
                propertyNames,
                values.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                $"Resource keys differ in {resourceBaseName}.");
            foreach (var propertyName in propertyNames)
                Assert.IsFalse(string.IsNullOrWhiteSpace(values[propertyName]),
                    $"Missing {propertyName} in {resourceBaseName}.");
        }
    }

    [TestMethod]
    public void AdministratorDescriptions_RemainSelectableAcrossNativeEditorTypes()
    {
        var descriptions = new[]
        {
            PluginStrings.EditorDescription,
            PluginStrings.PlaybackModeDescription,
            PluginStrings.OnlyMissingMediaInfoDescription,
            PluginStrings.IncludedLibraryIdsDescription,
            PluginStrings.DetectedRedirectHostsDescription,
            PluginStrings.AllowedRedirectHostsDescription,
        };

        foreach (var description in descriptions)
        {
            Assert.IsTrue(description.StartsWith("<span style=\"", StringComparison.Ordinal));
            Assert.IsTrue(description.Contains("-webkit-user-select:text", StringComparison.Ordinal));
            Assert.IsTrue(description.Contains("user-select:text", StringComparison.Ordinal));
            Assert.IsTrue(description.EndsWith("</span>", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void NativeGenericUiMetadata_FollowsEveryRequestedCultureWithoutUsingCachedDescriptions()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var expected in new[]
                     {
                         (Locale: "zh-CN", Title: "STRM 桥接", Name: "播放路由模式", Description: "普通文件直放"),
                         (Locale: "en-US", Title: "STRM Bridge", Name: "Playback routing mode", Description: "Adaptive (recommended)"),
                         (Locale: "zh-TW", Title: "STRM 橋接", Name: "播放路由模式", Description: "一般檔案直放"),
                         (Locale: "zh-CN", Title: "STRM 桥接", Name: "播放路由模式", Description: "普通文件直放"),
                     })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(expected.Locale);
                var container = (EditObjectContainer)new PluginConfiguration().CreateEditContainer();
                var root = container.EditorRoot;
                var editor = root.EditorItems.Single(item =>
                    item.Name == nameof(PluginConfiguration.PlaybackMode));
                Assert.AreEqual(expected.Name, editor.DisplayName);
                StringAssert.Contains(editor.Description, expected.Description);
                Assert.AreEqual(expected.Title, root.DisplayName);
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [TestMethod]
    public async Task ConcurrentResourceReads_KeepTheirRequestedLanguages()
    {
        var cases = Enumerable.Range(0, 24).Select(index =>
            index % 3 == 0
                ? (Locale: "en-US", Title: "STRM Bridge", Description: "Adaptive (recommended)")
                : index % 3 == 1
                    ? (Locale: "zh-CN", Title: "STRM 桥接", Description: "普通文件直放")
                    : (Locale: "zh-TW", Title: "STRM 橋接", Description: "一般檔案直放"))
            .ToArray();
        var results = await Task.WhenAll(cases.Select(expected => Task.Run(() => (
            Expected: expected,
            Title: PluginStrings.GetText(
                nameof(PluginStrings.EditorTitle), CultureInfo.GetCultureInfo(expected.Locale)),
            Description: PluginStrings.GetText(
                nameof(PluginStrings.PlaybackModeDescription), CultureInfo.GetCultureInfo(expected.Locale))))));

        foreach (var result in results)
        {
            Assert.AreEqual(result.Expected.Title, result.Title);
            StringAssert.Contains(result.Description, result.Expected.Description);
        }
    }

    [TestMethod]
    public async Task ConcurrentNativeEditorModels_KeepTheirRequestedLanguages()
    {
        var cases = Enumerable.Range(0, 24).Select(index =>
            index % 3 == 0
                ? (Locale: "en-US", Title: "STRM Bridge", Description: "Adaptive (recommended)")
                : index % 3 == 1
                    ? (Locale: "zh-CN", Title: "STRM 桥接", Description: "普通文件直放")
                    : (Locale: "zh-TW", Title: "STRM 橋接", Description: "一般檔案直放"))
            .ToArray();
        var results = await Task.WhenAll(cases.Select(expected => Task.Run(() =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(expected.Locale);
            var container = (EditObjectContainer)new PluginConfiguration().CreateEditContainer();
            var playbackMode = container.EditorRoot.EditorItems.Single(item =>
                item.Name == nameof(PluginConfiguration.PlaybackMode));
            return (Expected: expected, container.EditorRoot.DisplayName, playbackMode.Description);
        })));

        foreach (var result in results)
        {
            Assert.AreEqual(result.Expected.Title, result.DisplayName);
            StringAssert.Contains(result.Description, result.Expected.Description);
        }
    }

    [TestMethod]
    public void EveryVisibleConfigurationEditorHasOfficialLocalizedMetadata()
    {
        var container = (EditObjectContainer)new PluginConfiguration().CreateEditContainer();
        foreach (var editor in container.EditorRoot.EditorItems)
        {
            var property = typeof(PluginConfiguration).GetProperty(
                editor.Name, BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(property, $"Missing configuration property for editor {editor.Name}.");
            Assert.IsNotNull(property.GetCustomAttribute<DisplayNameLAttribute>(),
                $"Missing localized display metadata for editor {editor.Name}.");
        }
    }
}
