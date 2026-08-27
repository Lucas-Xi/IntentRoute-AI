using System.Diagnostics;
using System.IO;
using System.Text;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RealSingBoxFactAttribute : FactAttribute
{
    public const string ExecutableEnvironmentVariable = "INTENTROUTE_TEST_SING_BOX_PATH";

    public RealSingBoxFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable)))
            Skip = $"Set {ExecutableEnvironmentVariable} to run the pinned real sing-box integration gate.";
    }
}

public sealed class SingBoxRealIntegrationTests
{
    [RealSingBoxFact]
    [Trait("Category", "RealSingBox")]
    public async Task RepresentativeBuilderOutputs_PassPinnedRealSingBoxCheck()
    {
        var executablePath = Environment.GetEnvironmentVariable(
            RealSingBoxFactAttribute.ExecutableEnvironmentVariable)!;
        Assert.True(File.Exists(executablePath), "The pinned sing-box test executable is missing.");

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "intentroute-real-sing-box-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var fixtures = new[]
            {
                (Name: "direct-all", Config: RepresentativeConfig(GlobalMode.DirectAll)),
                (Name: "proxy-all", Config: RepresentativeConfig(GlobalMode.ProxyAll))
            };

            foreach (var fixture in fixtures)
            {
                var build = SingBoxConfigBuilder.Build(fixture.Config);
                Assert.True(build.Success, $"Representative fixture '{fixture.Name}' failed local construction: {build.Error}");
                Assert.False(string.IsNullOrWhiteSpace(build.ConfigJson));

                var configPath = Path.Combine(tempDirectory, fixture.Name + ".json");
                await File.WriteAllTextAsync(configPath, build.ConfigJson!, new UTF8Encoding(false));
                await AssertSingBoxAcceptsAsync(executablePath, configPath, fixture.Name);
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static AppConfig RepresentativeConfig(GlobalMode globalMode)
    {
        var config = new AppConfig
        {
            GlobalMode = globalMode,
            ProxyServers =
            [
                Proxy("socks-primary", ProxyType.Socks5, "127.0.0.1", 10808, "fixture-user", "fixture-password"),
                Proxy("http-secondary", ProxyType.Http, "::1", 10809),
                Proxy("https-tertiary", ProxyType.Https, "127.0.0.2", 10810)
            ]
        };

        config.Rules =
        [
            Rule(
                "browser.exe",
                ProxyMode.Proxy,
                priority: 10,
                hosts: "github.com,*.openai.com",
                ips: "10.0.0.0/8,2001:db8::/32",
                ports: "443,8000-8100",
                protocol: "Both",
                proxyId: "socks-primary"),
            Rule("http-client.exe", ProxyMode.Proxy, 20, hosts: "example.com", ports: "80", protocol: "TCP", proxyId: "http-secondary"),
            Rule("secure-client.exe", ProxyMode.Proxy, 30, hosts: "secure.example.com", ports: "443", protocol: "TCP", proxyId: "https-tertiary"),
            Rule("default-proxy.exe", ProxyMode.Proxy, 35, hosts: "default.example.com", ports: "443", protocol: "TCP"),
            Rule("local-tool.exe", ProxyMode.Direct, 40, ips: "127.0.0.1/32,::1/128", ports: "53", protocol: "UDP"),
            Rule("blocked.exe", ProxyMode.Block, 50, hosts: "*.blocked.example", ports: "1000-2000", protocol: "UDP"),
            Rule("*", ProxyMode.Direct, 60, protocol: "TCP/UDP")
        ];
        return config;
    }

    private static ProxyServer Proxy(
        string id,
        ProxyType type,
        string host,
        int port,
        string username = "",
        string password = "") => new()
        {
            Id = id,
            Name = id,
            ProxyType = type,
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            Enabled = true
        };

    private static ProxyRule Rule(
        string process,
        ProxyMode mode,
        int priority,
        string hosts = "",
        string ips = "",
        string ports = "",
        string protocol = "Both",
        string proxyId = "") => new()
        {
            ExeName = process,
            Mode = mode,
            Priority = priority,
            CreatedAt = $"2026-08-26 20:{priority / 10:00}",
            TargetHosts = hosts,
            TargetIPs = ips,
            TargetPorts = ports,
            Protocol = protocol,
            ProxyId = proxyId,
            IsEnabled = true
        };

    private static async Task AssertSingBoxAcceptsAsync(
        string executablePath,
        string configPath,
        string fixtureName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("check");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), $"Pinned sing-box did not start for fixture '{fixtureName}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            Assert.Fail($"Pinned sing-box check timed out for fixture '{fixtureName}'.");
        }

        var output = await standardOutput;
        var error = await standardError;
        var diagnostic = Limit(SingBoxRuntime.RedactSecrets((error + Environment.NewLine + output).Trim()), 1200);
        Assert.True(
            process.ExitCode == 0,
            $"Pinned sing-box rejected representative fixture '{fixtureName}' (exit {process.ExitCode}). {diagnostic}");
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
