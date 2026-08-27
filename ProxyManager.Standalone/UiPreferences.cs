using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProxyManager.Standalone;

// Display-only preferences live outside the routing Configuration Workspace so a
// cosmetic choice is never blocked by configuration Recovery Protection. Any read
// failure falls back to the deterministic Chinese default.
public static class UiPreferences
{
    public const string Chinese = "zh";
    public const string English = "en";
    public const string FollowSystem = "system";
    public const string PreferenceFileName = "ui-preferences.json";

    public static string GetLanguage(string? directory = null)
    {
        var path = GetPreferencePath(directory);
        try
        {
            if (!File.Exists(path)) return Chinese;
            var json = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            var value = json?["language"]?.Value<string>();
            return Normalize(value);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Chinese;
        }
    }

    public static void SetLanguage(string language, string? directory = null)
    {
        var normalized = Normalize(language);
        var path = GetPreferencePath(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = new JObject { ["language"] = normalized };
        File.WriteAllText(path, json.ToString(Formatting.Indented), Encoding.UTF8);
    }

    public static CultureInfo ResolveLanguageCulture(string language)
    {
        return Normalize(language) switch
        {
            English => CultureInfo.GetCultureInfo("en"),
            FollowSystem => IsSystemChinese() ? CultureInfo.GetCultureInfo("zh-CN") : CultureInfo.GetCultureInfo("en"),
            _ => CultureInfo.GetCultureInfo("zh-CN")
        };
    }

    private static bool IsSystemChinese() =>
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) => value switch
    {
        English => English,
        FollowSystem => FollowSystem,
        _ => Chinese
    };

    private static string GetPreferencePath(string? directory)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppDataMigration.CurrentDirectoryName);
        return Path.Combine(root, PreferenceFileName);
    }
}
