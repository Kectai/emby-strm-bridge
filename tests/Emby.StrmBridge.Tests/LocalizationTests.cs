using System.Globalization;
using Emby.StrmBridge.Configuration;
using Emby.StrmBridge.Extraction;
using Emby.StrmBridge.Localization;

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
            Assert.AreEqual("媒体库", new ExtractionScheduledTask().Category);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-TW");
            Assert.AreEqual("STRM 橋接", new PluginConfiguration().EditorTitle);
            Assert.IsTrue(PluginStrings.DetectedRedirectHostsDescription.Contains("自動重試", StringComparison.Ordinal));
            Assert.AreEqual("媒體庫", new ClearStoredMediaInfoScheduledTask().Category);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.AreEqual("STRM Bridge", new PluginConfiguration().EditorTitle);
            Assert.IsTrue(PluginStrings.DetectedRedirectHostsDescription.Contains("automatically retries", StringComparison.Ordinal));
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
        var original = CultureInfo.CurrentUICulture;
        try
        {
            var properties = typeof(PluginStrings).GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            foreach (var cultureName in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                foreach (var property in properties)
                {
                    Assert.AreEqual(typeof(string), property.PropertyType);
                    var value = property.GetValue(null) as string;
                    Assert.IsFalse(string.IsNullOrWhiteSpace(value),
                        $"Missing {property.Name} for {cultureName}.");
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
