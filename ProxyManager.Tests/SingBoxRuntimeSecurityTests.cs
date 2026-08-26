using System.IO;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class SingBoxRuntimeSecurityTests
{
    [Fact]
    public void RedactSecrets_RemovesJsonAndLogCredentials()
    {
        const string input = "{\"password\":\"json-secret\"} password=line-secret token:token-secret";

        var redacted = SingBoxRuntime.RedactSecrets(input);

        Assert.DoesNotContain("json-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("line-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("token-secret", redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoverExecutable_PrefersExplicitEnvironmentPath()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "proxymanager-test-" + Guid.NewGuid().ToString("N"));
        var fakeExecutable = Path.Combine(tempDirectory, "sing-box.exe");
        var previous = Environment.GetEnvironmentVariable(SingBoxRuntime.EnvExecutable);

        Directory.CreateDirectory(tempDirectory);
        File.WriteAllBytes(fakeExecutable, []);
        try
        {
            Environment.SetEnvironmentVariable(SingBoxRuntime.EnvExecutable, fakeExecutable);
            using var runtime = new SingBoxRuntime(Path.Combine(tempDirectory, "config"));

            Assert.Equal(Path.GetFullPath(fakeExecutable), runtime.DiscoverExecutable());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SingBoxRuntime.EnvExecutable, previous);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Dispose_RemovesManagedConfiguration()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "proxymanager-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var runtime = new SingBoxRuntime(tempDirectory);
            File.WriteAllText(runtime.ConfigPath, "{}");

            runtime.Dispose();

            Assert.False(File.Exists(runtime.ConfigPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
