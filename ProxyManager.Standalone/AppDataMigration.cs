using System.IO;
using System.Text;

namespace ProxyManager.Standalone;

public static class AppDataMigration
{
    public const string CurrentDirectoryName = "IntentRouteAI";
    public const string LegacyDirectoryName = "ProxyManager";
    public const string MigrationMarkerName = ".legacy-proxymanager-migrated";
    public const string MigrationInProgressName = ".legacy-proxymanager-migration-in-progress";
    public const string MigrationLockName = ".legacy-proxymanager-migration.lock";

    public static string PrepareConfigDirectory(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        var currentDirectory = Path.Combine(appDataRoot, CurrentDirectoryName);
        var legacyDirectory = Path.Combine(appDataRoot, LegacyDirectoryName);
        Directory.CreateDirectory(currentDirectory);
        using var migrationLock = AcquireMigrationLock(Path.Combine(currentDirectory, MigrationLockName));

        var markerPath = Path.Combine(currentDirectory, MigrationMarkerName);
        var inProgressPath = Path.Combine(currentDirectory, MigrationInProgressName);
        if (File.Exists(markerPath))
        {
            TryDeleteFile(inProgressPath);
            return currentDirectory;
        }

        if (!Directory.Exists(legacyDirectory))
            return currentDirectory;

        var isRetryingInterruptedMigration = File.Exists(inProgressPath);
        if (HasCurrentUserData(currentDirectory) && !isRetryingInterruptedMigration)
            return currentDirectory;

        if (!isRetryingInterruptedMigration)
        {
            WriteMarkerAtomically(
                inProgressPath,
                $"Started migration from ProxyManager on {DateTimeOffset.UtcNow:O}.{Environment.NewLine}");
        }

        CopyKnownFileIfMissing(Path.Combine(legacyDirectory, "config.json"), Path.Combine(currentDirectory, "config.json"));
        foreach (var source in Directory.GetFiles(legacyDirectory, "*.profile.json", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(currentDirectory, Path.GetFileName(source));
            CopyKnownFileIfMissing(source, target);
        }

        WriteMarkerAtomically(
            markerPath,
            $"Migrated known configuration files from ProxyManager on {DateTimeOffset.UtcNow:O}.{Environment.NewLine}" +
            "The legacy directory was intentionally retained for recovery.");
        TryDeleteFile(inProgressPath);
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

    private static void WriteMarkerAtomically(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    // 有界等待 60 秒：AV/CI 文件占用抖动下 10 秒不够，而真实启动迁移远用不到该上限，超时仍大声失败。
    private static FileStream AcquireMigrationLock(string path)
    {
        var startedAt = Environment.TickCount64;
        while (true)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (Environment.TickCount64 - startedAt < 60_000)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
