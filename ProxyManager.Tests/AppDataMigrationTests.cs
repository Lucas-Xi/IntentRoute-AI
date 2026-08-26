using System.IO;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class AppDataMigrationTests
{
    [Fact]
    public void Migration_CopiesKnownFilesAndKeepsLegacyDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacy = Path.Combine(root, AppDataMigration.LegacyDirectoryName);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "config.json"), "{\"sentinel\":true}");
            File.WriteAllText(Path.Combine(legacy, "work.profile.json"), "{\"profile\":true}");
            File.WriteAllText(Path.Combine(legacy, "sing-box.generated.json"), "secret-runtime-data");

            var current = AppDataMigration.PrepareConfigDirectory(root);

            Assert.True(Directory.Exists(legacy));
            Assert.True(File.Exists(Path.Combine(current, "config.json")));
            Assert.True(File.Exists(Path.Combine(current, "work.profile.json")));
            Assert.True(File.Exists(Path.Combine(current, AppDataMigration.MigrationMarkerName)));
            Assert.False(File.Exists(Path.Combine(current, "sing-box.generated.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Migration_IsRepeatableAndNeverOverwritesCurrentData()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacy = Path.Combine(root, AppDataMigration.LegacyDirectoryName);
            var current = Path.Combine(root, AppDataMigration.CurrentDirectoryName);
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(legacy, "config.json"), "legacy");
            File.WriteAllText(Path.Combine(current, "config.json"), "current");

            var first = AppDataMigration.PrepareConfigDirectory(root);
            var second = AppDataMigration.PrepareConfigDirectory(root);

            Assert.Equal(first, second);
            Assert.Equal("current", File.ReadAllText(Path.Combine(current, "config.json")));
            Assert.False(File.Exists(Path.Combine(current, "work.profile.json")));
            Assert.False(File.Exists(Path.Combine(current, AppDataMigration.MigrationMarkerName)));
            Assert.True(Directory.Exists(legacy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Migration_RetriesAfterPartialCopyFailure()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacy = Path.Combine(root, AppDataMigration.LegacyDirectoryName);
            var current = Path.Combine(root, AppDataMigration.CurrentDirectoryName);
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(legacy, "config.json"), "legacy-config");
            File.WriteAllText(Path.Combine(legacy, "work.profile.json"), "legacy-profile");

            var blockingDirectory = Path.Combine(current, "work.profile.json");
            Directory.CreateDirectory(blockingDirectory);

            Assert.ThrowsAny<IOException>(() => AppDataMigration.PrepareConfigDirectory(root));
            Assert.Equal("legacy-config", File.ReadAllText(Path.Combine(current, "config.json")));
            Assert.True(File.Exists(Path.Combine(current, AppDataMigration.MigrationInProgressName)));
            Assert.False(File.Exists(Path.Combine(current, AppDataMigration.MigrationMarkerName)));

            Directory.Delete(blockingDirectory);
            AppDataMigration.PrepareConfigDirectory(root);

            Assert.Equal("legacy-profile", File.ReadAllText(Path.Combine(current, "work.profile.json")));
            Assert.True(File.Exists(Path.Combine(current, AppDataMigration.MigrationMarkerName)));
            Assert.False(File.Exists(Path.Combine(current, AppDataMigration.MigrationInProgressName)));
            Assert.True(Directory.Exists(legacy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Migration_SerializesConcurrentStartsWithoutMissingOrOverwritingFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var legacy = Path.Combine(root, AppDataMigration.LegacyDirectoryName);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "config.json"), "legacy-config");
            for (var index = 0; index < 40; index++)
                File.WriteAllText(Path.Combine(legacy, $"profile-{index}.profile.json"), $"legacy-{index}");
            File.WriteAllText(Path.Combine(legacy, "sing-box.generated.json"), "runtime-only");

            var tasks = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => AppDataMigration.PrepareConfigDirectory(root)))
                .ToArray();
            await Task.WhenAll(tasks);

            var current = Path.Combine(root, AppDataMigration.CurrentDirectoryName);
            Assert.All(tasks, task => Assert.Equal(current, task.Result));
            Assert.Equal("legacy-config", File.ReadAllText(Path.Combine(current, "config.json")));
            for (var index = 0; index < 40; index++)
                Assert.Equal($"legacy-{index}", File.ReadAllText(Path.Combine(current, $"profile-{index}.profile.json")));
            Assert.False(File.Exists(Path.Combine(current, "sing-box.generated.json")));
            Assert.True(File.Exists(Path.Combine(current, AppDataMigration.MigrationMarkerName)));
            Assert.False(File.Exists(Path.Combine(current, AppDataMigration.MigrationInProgressName)));
            Assert.False(File.Exists(Path.Combine(current, AppDataMigration.MigrationLockName)));
            Assert.True(Directory.Exists(legacy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IntentRouteAI.Migration.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
