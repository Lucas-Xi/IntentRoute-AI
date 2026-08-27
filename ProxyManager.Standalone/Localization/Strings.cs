using System.Collections;
using System.Globalization;
using System.Resources;

namespace ProxyManager.Standalone.Localization;

// Hand-written accessor so command-line builds do not depend on the Visual Studio
// resx code generator. Properties are public for XAML x:Static references.
public static class Strings
{
    private static readonly ResourceManager Manager = new(
        "ProxyManager.Standalone.Localization.Strings",
        typeof(Strings).Assembly);

    public static string SettingsRuntimeTitle => GetString(nameof(SettingsRuntimeTitle));
    public static string SettingsRuntimeIntro => GetString(nameof(SettingsRuntimeIntro));
    public static string SettingsRuntimePathTooltip => GetString(nameof(SettingsRuntimePathTooltip));
    public static string SettingsRuntimeBrowse => GetString(nameof(SettingsRuntimeBrowse));
    public static string SettingsRuntimeRecheck => GetString(nameof(SettingsRuntimeRecheck));
    public static string SettingsRuntimeClear => GetString(nameof(SettingsRuntimeClear));
    public static string SettingsRuntimeVersionUnchecked => GetString(nameof(SettingsRuntimeVersionUnchecked));
    public static string SettingsRuntimeChecking => GetString(nameof(SettingsRuntimeChecking));
    public static string SettingsAiHealthTitle => GetString(nameof(SettingsAiHealthTitle));
    public static string SettingsAiHealthIntro => GetString(nameof(SettingsAiHealthIntro));
    public static string SettingsAiHealthRun => GetString(nameof(SettingsAiHealthRun));
    public static string SettingsAiHealthNotRun => GetString(nameof(SettingsAiHealthNotRun));
    public static string SettingsProxyTitle => GetString(nameof(SettingsProxyTitle));
    public static string SettingsProxyIntro => GetString(nameof(SettingsProxyIntro));
    public static string SettingsProxyType => GetString(nameof(SettingsProxyType));
    public static string SettingsProxyHost => GetString(nameof(SettingsProxyHost));
    public static string SettingsProxyPort => GetString(nameof(SettingsProxyPort));
    public static string SettingsProxyUsername => GetString(nameof(SettingsProxyUsername));
    public static string SettingsProxyPassword => GetString(nameof(SettingsProxyPassword));
    public static string SettingsProxySave => GetString(nameof(SettingsProxySave));
    public static string SettingsProxyTest => GetString(nameof(SettingsProxyTest));
    public static string SettingsProxyTestHint => GetString(nameof(SettingsProxyTestHint));
    public static string SettingsLanguageTitle => GetString(nameof(SettingsLanguageTitle));
    public static string SettingsLanguageIntro => GetString(nameof(SettingsLanguageIntro));
    public static string SettingsLanguageChinese => GetString(nameof(SettingsLanguageChinese));
    public static string SettingsLanguageEnglish => GetString(nameof(SettingsLanguageEnglish));
    public static string SettingsLanguageFollowSystem => GetString(nameof(SettingsLanguageFollowSystem));
    public static string SettingsLanguageRestartHint => GetString(nameof(SettingsLanguageRestartHint));

    public static string GetString(string name) => Manager.GetString(name) ?? string.Empty;

    public static IReadOnlySet<string> GetKeySet(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        // GetResourceSet hands out the manager's cached instance; disposing it here
        // would poison the cache for every later GetString call.
        var resourceSet = Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        if (resourceSet == null) return new HashSet<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in resourceSet)
            if (entry.Key is string key)
                keys.Add(key);
        return keys;
    }
}
