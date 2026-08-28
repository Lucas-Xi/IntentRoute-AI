using System.Globalization;
using System.IO;
using System.Text;
using ProxyManager.Standalone;
using ProxyManager.Standalone.Localization;
using Xunit;

namespace ProxyManager.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void ResourceKeySets_MatchBetweenChineseAndEnglish()
    {
        var chinese = Strings.GetKeySet(CultureInfo.InvariantCulture);
        var english = Strings.GetKeySet(CultureInfo.GetCultureInfo("en"));

        Assert.NotEmpty(chinese);
        Assert.Empty(chinese.Except(english));
        Assert.Empty(english.Except(chinese));
    }

    [Fact]
    public void EveryKey_HasNonEmptyValueInBothLanguages()
    {
        var chinese = Strings.GetKeySet(CultureInfo.InvariantCulture);
        var english = Strings.GetKeySet(CultureInfo.GetCultureInfo("en"));

        Assert.All(chinese, key => Assert.False(string.IsNullOrWhiteSpace(Strings.GetString(key)), $"Neutral value missing for {key}"));
        Assert.All(english, key => Assert.False(string.IsNullOrWhiteSpace(Strings.GetString(key)), $"English value missing for {key}"));
    }

    [Fact]
    public void EveryAccessorProperty_ResolvesToANonEmptyValue()
    {
        // The accessor is hand-written: a typo between the property and its resx key
        // would make GetString fall back to an empty string while the parity tests
        // still pass. Reflection closes that gap for every property at once.
        var properties = typeof(Strings)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string) && property.CanRead)
            .ToList();
        Assert.NotEmpty(properties);
        foreach (var property in properties)
        {
            var value = (string?)property.GetValue(null);
            Assert.False(string.IsNullOrWhiteSpace(value), $"Property {property.Name} resolves to an empty value");
        }
    }

    [Fact]
    public void Localization_DefaultsToChineseForEnglishCultureUnrelatedToUiPreference()
    {
        // The neutral resource is Chinese: any culture without a specific translation
        // must fall back to Chinese rather than to English.
        var current = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Contains("就绪", Strings.SettingsRuntimeTitle, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = current;
        }
    }

    [Fact]
    public void UiPreferences_RoundTripsLanguageAndFallsBackOnCorruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IntentRouteAI.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(UiPreferences.Chinese, UiPreferences.GetLanguage(directory));

            UiPreferences.SetLanguage(UiPreferences.English, directory);
            Assert.Equal(UiPreferences.English, UiPreferences.GetLanguage(directory));

            UiPreferences.SetLanguage(UiPreferences.FollowSystem, directory);
            Assert.Equal(UiPreferences.FollowSystem, UiPreferences.GetLanguage(directory));

            File.WriteAllText(
                Path.Combine(directory, UiPreferences.PreferenceFileName),
                "{ not valid json",
                Encoding.UTF8);
            Assert.Equal(UiPreferences.Chinese, UiPreferences.GetLanguage(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UiPreferences_RejectsUnknownStoredValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IntentRouteAI.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, UiPreferences.PreferenceFileName),
                """{ "language": "klingon" }""",
                Encoding.UTF8);
            Assert.Equal(UiPreferences.Chinese, UiPreferences.GetLanguage(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveLanguageCulture_MapsKnownPreferences()
    {
        Assert.Equal("zh-CN", UiPreferences.ResolveLanguageCulture(UiPreferences.Chinese).Name);
        Assert.Equal("en", UiPreferences.ResolveLanguageCulture(UiPreferences.English).Name);

        var current = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Assert.Equal("zh-CN", UiPreferences.ResolveLanguageCulture(UiPreferences.FollowSystem).Name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("en", UiPreferences.ResolveLanguageCulture(UiPreferences.FollowSystem).Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = current;
        }
    }
}
