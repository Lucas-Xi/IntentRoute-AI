using System.IO;
using ProxyManager.Standalone;
using Newtonsoft.Json;
using Xunit;

namespace ProxyManager.Tests;

public sealed class AppConfigStoreTests
{
    [Fact]
    public void Serialize_EncryptsPasswordAndRoundTripsForCurrentUser()
    {
        var config = ConfigWithPassword("correct horse battery staple");

        var json = AppConfigStore.Serialize(config);
        var restored = AppConfigStore.Deserialize(json);

        Assert.DoesNotContain("correct horse battery staple", json, StringComparison.Ordinal);
        Assert.Contains("dpapi:", json, StringComparison.Ordinal);
        Assert.Equal("correct horse battery staple", restored.ProxyServers[0].Password);
    }

    [Fact]
    public void Serialize_EncryptsLiteralPasswordBeginningWithDpapiMarker()
    {
        const string password = "dpapi:literal-password";
        var config = ConfigWithPassword(password);

        var json = AppConfigStore.Serialize(config);
        var restored = AppConfigStore.Deserialize(json);

        Assert.DoesNotContain(password, json, StringComparison.Ordinal);
        Assert.Equal(password, restored.ProxyServers[0].Password);
    }

    [Fact]
    public void Serialize_RedactedExportOmitsCredentials()
    {
        var json = AppConfigStore.Serialize(ConfigWithPassword("must-not-export"), redactPasswords: true);

        Assert.DoesNotContain("must-not-export", json, StringComparison.Ordinal);
        Assert.DoesNotContain("dpapi:", json, StringComparison.Ordinal);
        var restored = AppConfigStore.Deserialize(json);
        Assert.Equal(string.Empty, restored.ProxyServers[0].Password);
    }

    [Fact]
    public void Deserialize_AcceptsLegacyPlaintextForOneTimeMigration()
    {
        const string json = """
        {
          "ProxyServers": [
            { "Id": "legacy", "Name": "Legacy", "Host": "127.0.0.1", "Port": 1080, "Password": "legacy-password" }
          ]
        }
        """;

        var config = AppConfigStore.Deserialize(json);

        Assert.Equal("legacy-password", config.ProxyServers[0].Password);
        Assert.DoesNotContain("legacy-password", AppConfigStore.Serialize(config), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadPreservingInvalidFile_KeepsOriginalAndCreatesRecoveryCopy()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "config.json");
        var original = "{ truncated configuration";
        File.WriteAllText(path, original);
        try
        {
            var result = AppConfigStore.LoadPreservingInvalidFile(
                path,
                new DateTime(2026, 8, 26, 12, 34, 56, DateTimeKind.Utc));

            Assert.Equal(AppConfigLoadStatus.Unusable, result.Status);
            Assert.Null(result.Config);
            Assert.NotNull(result.BackupPath);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Equal(original, File.ReadAllText(result.BackupPath!));
            Assert.Contains("corrupt-20260826T123456Z", result.BackupPath!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadPreservingInvalidFile_TreatsBrokenDpapiAsUnusableInsteadOfEmptyPassword()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "config.json");
        const string json = """
        {
          "ProxyServers": [
            { "Id": "broken", "Name": "Broken", "Host": "127.0.0.1", "Port": 1080, "Password": "dpapi:not-base64" }
          ]
        }
        """;
        File.WriteAllText(path, json);
        try
        {
            var result = AppConfigStore.LoadPreservingInvalidFile(path);

            Assert.Equal(AppConfigLoadStatus.Unusable, result.Status);
            Assert.Contains("无法解密", result.Error, StringComparison.Ordinal);
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadPreservingInvalidFile_RejectsInvalidUtf8AndPreservesExactBytes()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "config.json");
        byte[] invalidUtf8 = [0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D];
        File.WriteAllBytes(path, invalidUtf8);
        try
        {
            var result = AppConfigStore.LoadPreservingInvalidFile(path);

            Assert.Equal(AppConfigLoadStatus.Unusable, result.Status);
            Assert.Equal(invalidUtf8, File.ReadAllBytes(path));
            Assert.NotNull(result.BackupPath);
            Assert.Equal(invalidUtf8, File.ReadAllBytes(result.BackupPath!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadPreservingInvalidFile_DistinguishesMissingAndValidFiles()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "config.json");
        try
        {
            Assert.Equal(AppConfigLoadStatus.Missing, AppConfigStore.LoadPreservingInvalidFile(path).Status);

            File.WriteAllText(path, AppConfigStore.Serialize(new AppConfig()));
            var loaded = AppConfigStore.LoadPreservingInvalidFile(path);

            Assert.Equal(AppConfigLoadStatus.Loaded, loaded.Status);
            Assert.NotNull(loaded.Config);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void LoadPreservingInvalidFile_RejectsEmptyOrNullDocument(string json)
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "config.json");
        File.WriteAllText(path, json);
        try
        {
            var result = AppConfigStore.LoadPreservingInvalidFile(path);

            Assert.Equal(AppConfigLoadStatus.Unusable, result.Status);
            Assert.Equal(json, File.ReadAllText(path));
            Assert.NotNull(result.BackupPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("Rules")]
    [InlineData("ProxyServers")]
    [InlineData("ProxyChains")]
    public void LoadPreservingInvalidFile_RejectsNullCollectionEntriesWithoutStartupCrash(string collectionName)
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "config.json");
        var json = $"{{ \"{collectionName}\": [null] }}";
        File.WriteAllText(path, json);
        try
        {
            var result = AppConfigStore.LoadPreservingInvalidFile(path);

            Assert.Equal(AppConfigLoadStatus.Unusable, result.Status);
            Assert.Null(result.Config);
            Assert.Equal(json, File.ReadAllText(path));
            Assert.NotNull(result.BackupPath);
            Assert.Equal(json, File.ReadAllText(result.BackupPath!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{ \"Rules\": [ { \"ExeName\": \"safe.exe\", \"IsEnabled\": false } ] }")]
    [InlineData("{ \"ProxyServers\": [ { \"Host\": \"127.0.0.1\", \"Port\": 1080 } ] }")]
    [InlineData("{ \"ProxyChains\": [ { \"Name\": \"legacy\", \"Servers\": [] } ] }")]
    public void Deserialize_RejectsObjectsWhoseIdPropertyIsOmitted(string json)
    {
        Assert.Throws<JsonSerializationException>(() => AppConfigStore.Deserialize(json));
    }

    private static AppConfig ConfigWithPassword(string password) => new()
    {
        ProxyServers =
        [
            new ProxyServer
            {
                Id = "server-1",
                Name = "Secured",
                Host = "127.0.0.1",
                Port = 1080,
                Password = password
            }
        ]
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "intentroute-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
