using ProxyManager.Standalone;
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
}
