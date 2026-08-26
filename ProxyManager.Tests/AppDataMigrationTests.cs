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
