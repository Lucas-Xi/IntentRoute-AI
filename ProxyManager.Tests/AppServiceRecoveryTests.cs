using System.IO;
using System.Net;
using System.Net.Sockets;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class AppServiceRecoveryTests
{
    [Fact]
    public void CorruptConfiguration_BlocksSaveAndLeavesOriginalBytesUntouched()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        Directory.CreateDirectory(configDirectory);
        var original = new byte[] { 0x7B, 0x22, 0x52, 0x75, 0x6C, 0x65 };
        File.WriteAllBytes(configPath, original);

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            Assert.False(service.IsConfigurationWritable);
            Assert.NotNull(service.ConfigurationRecoveryBackupPath);
            Assert.Equal(original, File.ReadAllBytes(configPath));
            Assert.Equal(original, File.ReadAllBytes(service.ConfigurationRecoveryBackupPath!));

            Assert.Throws<InvalidOperationException>(() =>
                service.AddRule("must-not-save.exe", ProxyMode.Direct));
            Assert.Empty(service.Config.Rules);
            Assert.Equal(original, File.ReadAllBytes(configPath));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void ExplicitReset_ReplacesUnusableConfigurationButKeepsRecoveryCopy()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(configPath, "not-json");

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var backupPath = service.ConfigurationRecoveryBackupPath;

            service.ResetUnusableConfiguration();

            Assert.True(service.IsConfigurationWritable);
            Assert.True(File.Exists(backupPath));
            Assert.Equal("not-json", File.ReadAllText(backupPath!));
            var reloaded = AppConfigStore.Deserialize(File.ReadAllText(configPath));
            Assert.Single(reloaded.ProxyServers);
            Assert.Equal("127.0.0.1", reloaded.ProxyServers[0].Host);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void ExplicitReset_IsBlockedWhenRecoveryCopyNoLongerExists()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(configPath, "unique-corrupt-source");

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            File.Delete(service.ConfigurationRecoveryBackupPath!);

            var error = Assert.Throws<InvalidOperationException>(() => service.ResetUnusableConfiguration());

            Assert.Contains("恢复副本", error.Message, StringComparison.Ordinal);
            Assert.False(service.IsConfigurationWritable);
            Assert.Equal("unique-corrupt-source", File.ReadAllText(configPath));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LocalProxyTest_ConnectsOnlyToLiteralLoopback()
    {
        var appDataRoot = CreateTempDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            Assert.True(await service.TestLocalProxyAsync("127.0.0.1", port));
            Assert.False(await service.TestLocalProxyAsync("localhost", port));
            Assert.False(await service.TestLocalProxyAsync("192.168.1.10", port));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LocalProxyTest_SupportsIpv6Loopback()
    {
        if (!Socket.OSSupportsIPv6) return;
        var appDataRoot = CreateTempDirectory();
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            Assert.True(await service.TestLocalProxyAsync("::1", port));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void RecoveryImport_RejectsUnsupportedConfigWithoutReplacingProtectedOriginal()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        var importPath = Path.Combine(appDataRoot, "import.json");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(configPath, "protected-corrupt-source");
        File.WriteAllText(importPath, AppConfigStore.Serialize(new AppConfig
        {
            ProxyServers = [new ProxyServer { Host = "8.8.8.8", Port = 1080 }]
        }));

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            Assert.Throws<ArgumentException>(() => service.RecoverConfigurationFromFile(importPath));

            Assert.False(service.IsConfigurationWritable);
            Assert.Equal("protected-corrupt-source", File.ReadAllText(configPath));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void RecoveryImport_AcceptsValidConfigAndKeepsRecoveryCopy()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        var importPath = Path.Combine(appDataRoot, "import.json");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(configPath, "protected-corrupt-source");
        File.WriteAllText(importPath, AppConfigStore.Serialize(new AppConfig
        {
            ProxyServers = [new ProxyServer { Host = "::1", Port = 1080 }]
        }));

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var backupPath = service.ConfigurationRecoveryBackupPath;

            service.RecoverConfigurationFromFile(importPath);

            Assert.True(service.IsConfigurationWritable);
            Assert.True(File.Exists(backupPath));
            Assert.Equal("protected-corrupt-source", File.ReadAllText(backupPath!));
            Assert.Equal("::1", service.Config.ProxyServers[0].Host);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void RecoveryImport_IsBlockedWhenRecoveryCopyNoLongerExists()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        var importPath = Path.Combine(appDataRoot, "import.json");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(configPath, "unique-corrupt-source");
        File.WriteAllText(importPath, AppConfigStore.Serialize(new AppConfig()));

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            File.Delete(service.ConfigurationRecoveryBackupPath!);

            var error = Assert.Throws<InvalidOperationException>(() =>
                service.RecoverConfigurationFromFile(importPath));

            Assert.Contains("恢复副本", error.Message, StringComparison.Ordinal);
            Assert.False(service.IsConfigurationWritable);
            Assert.Equal("unique-corrupt-source", File.ReadAllText(configPath));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SavedExecutablePath_IsUnapprovedAtTheStartOfEverySession()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        var candidatePath = Path.Combine(appDataRoot, "unapproved-sing-box.exe");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllBytes(candidatePath, []);
        AppConfigStore.SaveAtomic(configPath, new AppConfig
        {
            SingBoxExecutablePath = candidatePath,
            ProxyServers = [new ProxyServer { Host = "127.0.0.1", Port = 1080 }]
        });

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: true);

            var readiness = await service.ProbeRuntimeReadinessAsync();

            Assert.False(service.IsSingBoxExecutableApprovedForSession);
            Assert.False(readiness.IsReady);
            Assert.Equal(Path.GetFullPath(candidatePath), readiness.ExecutablePath);
            Assert.Contains("本次启动中批准", readiness.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(configDirectory, SingBoxRuntime.DefaultConfigFileName)));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryImport_DoesNotApproveOrExecuteAnEmbeddedExecutablePath()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        var importPath = Path.Combine(appDataRoot, "import.json");
        var candidatePath = Path.Combine(appDataRoot, "embedded-sing-box.exe");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(configPath, "protected-corrupt-source");
        File.WriteAllBytes(candidatePath, []);
        File.WriteAllText(importPath, AppConfigStore.Serialize(new AppConfig
        {
            SingBoxExecutablePath = candidatePath,
            ProxyServers = [new ProxyServer { Host = "127.0.0.1", Port = 1080 }]
        }));

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            service.RecoverConfigurationFromFile(importPath);
            var readiness = await service.ProbeRuntimeReadinessAsync();

            Assert.False(service.IsSingBoxExecutableApprovedForSession);
            Assert.False(readiness.IsReady);
            Assert.Contains("本次启动中批准", readiness.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(configDirectory, SingBoxRuntime.DefaultConfigFileName)));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidSavedExecutablePath_IsReportedWithoutExecutionOrStartupFailure()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        Directory.CreateDirectory(configDirectory);
        AppConfigStore.SaveAtomic(configPath, new AppConfig
        {
            SingBoxExecutablePath = "invalid\0path",
            ProxyServers = [new ProxyServer { Host = "127.0.0.1", Port = 1080 }]
        });

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: true);

            var readiness = await service.ProbeRuntimeReadinessAsync();

            Assert.False(readiness.IsReady);
            Assert.Contains("路径无效", readiness.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(configDirectory, SingBoxRuntime.DefaultConfigFileName)));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void RuleImport_RejectsUnsupportedSemanticsBeforeChangingMemoryOrDisk()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        Directory.CreateDirectory(configDirectory);
        AppConfigStore.SaveAtomic(configPath, new AppConfig
        {
            ProxyServers = [new ProxyServer { Host = "127.0.0.1", Port = 1080 }]
        });
        var original = File.ReadAllBytes(configPath);

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            Assert.Throws<InvalidDataException>(() => service.ImportRules([
                new ProxyRule
                {
                    ExeName = "unsupported.exe",
                    ProxyChainId = "unsupported-chain",
                    IsEnabled = true
                }
            ]));

            Assert.Empty(service.Config.Rules);
            Assert.Equal(original, File.ReadAllBytes(configPath));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void ConfigurationSnapshot_CannotMutateTheWorkspace()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            var snapshot = service.Config;
            snapshot.Rules.Add(new ProxyRule { ExeName = "snapshot-only.exe" });
            snapshot.ProxyServers[0].Host = "::1";

            var nextSnapshot = service.Config;
            Assert.Empty(nextSnapshot.Rules);
            Assert.Equal("127.0.0.1", nextSnapshot.ProxyServers[0].Host);
            Assert.False(File.Exists(service.ConfigPath));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void PersistenceFailure_DoesNotPublishCandidateOrChangeDisk()
    {
        var appDataRoot = CreateTempDirectory();
        var configDirectory = Path.Combine(appDataRoot, AppDataMigration.CurrentDirectoryName);
        var configPath = Path.Combine(configDirectory, "config.json");
        Directory.CreateDirectory(configDirectory);
        AppConfigStore.SaveAtomic(configPath, new AppConfig
        {
            ProxyServers = [new ProxyServer { Host = "127.0.0.1", Port = 1080 }]
        });
        var original = File.ReadAllBytes(configPath);

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var statuses = new List<string>();
            service.StatusChanged += statuses.Add;
            Exception? error;
            using (new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                error = Record.Exception(() => service.AddRule("rollback.exe", ProxyMode.Direct));
            }

            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.Empty(service.Config.Rules);
            Assert.Equal(original, File.ReadAllBytes(configPath));
            Assert.Empty(statuses);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void UnsupportedMutation_DoesNotPublishCandidateOrChangeDisk()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var rule = service.AddRule("safe.exe", ProxyMode.Direct);
            var original = File.ReadAllBytes(service.ConfigPath);

            Assert.Throws<InvalidDataException>(() =>
                service.UpdateRuleMode(rule.Id, (ProxyMode)999));

            Assert.Equal(ProxyMode.Direct, Assert.Single(service.Config.Rules).Mode);
            Assert.Equal(original, File.ReadAllBytes(service.ConfigPath));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void LocalMutationPreservesApprovalButProfileReplacementClearsIt()
    {
        var appDataRoot = CreateTempDirectory();
        var executablePath = Path.Combine(appDataRoot, "selected-sing-box.exe");
        var profilePath = Path.Combine(appDataRoot, "replacement.profile.json");
        File.WriteAllBytes(executablePath, []);
        AppConfigStore.SaveAtomic(profilePath, new AppConfig
        {
            SingBoxExecutablePath = executablePath,
            ProxyServers = [new ProxyServer { Host = "127.0.0.1", Port = 1080 }]
        });

        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            service.SetSingBoxExecutablePath(executablePath);
            Assert.True(service.IsSingBoxExecutableApprovedForSession);

            service.AddRule("preserve-approval.exe", ProxyMode.Direct);
            Assert.True(service.IsSingBoxExecutableApprovedForSession);

            service.ImportProfile(profilePath);
            Assert.False(service.IsSingBoxExecutableApprovedForSession);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "intentroute-service-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
