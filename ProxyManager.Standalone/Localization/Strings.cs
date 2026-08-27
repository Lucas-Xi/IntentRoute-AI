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

    public static string WindowTitle => GetString(nameof(WindowTitle));
    public static string NavItemRules => GetString(nameof(NavItemRules));
    public static string NavItemPolicy => GetString(nameof(NavItemPolicy));
    public static string NavItemRouteSimulator => GetString(nameof(NavItemRouteSimulator));
    public static string NavItemAi => GetString(nameof(NavItemAi));
    public static string NavItemMonitor => GetString(nameof(NavItemMonitor));
    public static string NavItemProcess => GetString(nameof(NavItemProcess));
    public static string NavItemSettings => GetString(nameof(NavItemSettings));
    public static string NavItemAbout => GetString(nameof(NavItemAbout));
    public static string SidebarVersion => GetString(nameof(SidebarVersion));
    public static string StatusFooterLabel => GetString(nameof(StatusFooterLabel));
    public static string StatusFooterInitial => GetString(nameof(StatusFooterInitial));
    public static string ModeDirectLabel => GetString(nameof(ModeDirectLabel));
    public static string ModeProxyLabel => GetString(nameof(ModeProxyLabel));
    public static string WindowMinimizeName => GetString(nameof(WindowMinimizeName));
    public static string WindowMaximizeName => GetString(nameof(WindowMaximizeName));
    public static string WindowCloseName => GetString(nameof(WindowCloseName));
    public static string PageTitleRules => GetString(nameof(PageTitleRules));
    public static string PageTitleAi => GetString(nameof(PageTitleAi));
    public static string PageTitlePolicy => GetString(nameof(PageTitlePolicy));
    public static string PageTitleRouteSimulator => GetString(nameof(PageTitleRouteSimulator));
    public static string PageTitleMonitor => GetString(nameof(PageTitleMonitor));
    public static string PageTitleProcess => GetString(nameof(PageTitleProcess));
    public static string PageTitleSettings => GetString(nameof(PageTitleSettings));
    public static string PageTitleAbout => GetString(nameof(PageTitleAbout));
    public static string PageSubtitleRules => GetString(nameof(PageSubtitleRules));
    public static string PageSubtitleAi => GetString(nameof(PageSubtitleAi));
    public static string PageSubtitlePolicy => GetString(nameof(PageSubtitlePolicy));
    public static string PageSubtitleRouteSimulator => GetString(nameof(PageSubtitleRouteSimulator));
    public static string PageSubtitleMonitor => GetString(nameof(PageSubtitleMonitor));
    public static string PageSubtitleProcess => GetString(nameof(PageSubtitleProcess));
    public static string PageSubtitleSettings => GetString(nameof(PageSubtitleSettings));
    public static string PageSubtitleAbout => GetString(nameof(PageSubtitleAbout));
    public static string MenuToggleEnabled => GetString(nameof(MenuToggleEnabled));
    public static string MenuForceProxy => GetString(nameof(MenuForceProxy));
    public static string MenuForceDirect => GetString(nameof(MenuForceDirect));
    public static string MenuForceBlock => GetString(nameof(MenuForceBlock));
    public static string MenuMoveUp => GetString(nameof(MenuMoveUp));
    public static string MenuMoveDown => GetString(nameof(MenuMoveDown));
    public static string MenuDelete => GetString(nameof(MenuDelete));
    public static string AboutTagline => GetString(nameof(AboutTagline));
    public static string AboutFeaturesTitle => GetString(nameof(AboutFeaturesTitle));
    public static string AboutFeature1 => GetString(nameof(AboutFeature1));
    public static string AboutFeature2 => GetString(nameof(AboutFeature2));
    public static string AboutFeature3 => GetString(nameof(AboutFeature3));
    public static string AboutFeature4 => GetString(nameof(AboutFeature4));
    public static string AboutFeature5 => GetString(nameof(AboutFeature5));
    public static string AboutFeature6 => GetString(nameof(AboutFeature6));
    public static string AboutFeature7 => GetString(nameof(AboutFeature7));
    public static string AboutFeature8 => GetString(nameof(AboutFeature8));
    public static string AboutFooter => GetString(nameof(AboutFooter));

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
