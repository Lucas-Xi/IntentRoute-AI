using System.IO;
using System.Text;

namespace ProxyManager.Standalone;

public static class AppDataMigration
{
    public const string CurrentDirectoryName = "IntentRouteAI";
    public const string LegacyDirectoryName = "ProxyManager";
    public const string MigrationMarkerName = ".legacy-proxymanager-migrated";

    public static string PrepareConfigDirectory(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        var currentDirectory = Path.Combine(appDataRoot, CurrentDirectoryName);
        var legacyDirectory = Path.Combine(appDataRoot, LegacyDirectoryName);
        Directory.CreateDirectory(currentDirectory);

        var markerPath = Path.Combine(currentDirectory, MigrationMarkerName);
        if (File.Exists(markerPath) || !Directory.Exists(legacyDirectory) || HasCurrentUserData(currentDirectory))
            return currentDirectory;

        CopyKnownFileIfMissing(Path.Combine(legacyDirectory, "config.json"), Path.Combine(currentDirectory, "config.json"));
        foreach (var source in Directory.GetFiles(legacyDirectory, "*.profile.json", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(currentDirectory, Path.GetFileName(source));
            CopyKnownFileIfMissing(source, target);
        }

        File.WriteAllText(
            markerPath,
            $"Migrated known configuration files from ProxyManager on {DateTimeOffset.UtcNow:O}.{Environment.NewLine}" +
            "The legacy directory was intentionally retained for recovery.",
            new UTF8Encoding(false));
        return currentDirectory;
    }

    private static bool HasCurrentUserData(string directory) =>
        File.Exists(Path.Combine(directory, "config.json")) ||
        Directory.GetFiles(directory, "*.profile.json", SearchOption.TopDirectoryOnly).Length > 0;

    private static void CopyKnownFileIfMissing(string source, string target)
    {
        if (!File.Exists(source) || File.Exists(target)) return;
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp, overwrite: false);
            File.Move(temp, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }
}
