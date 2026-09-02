using System.IO;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class RuleEditServiceTests
{
    [Fact]
    public void UpdateRuleConstraints_PersistsAllFields()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var rule = service.AddRuleByName("chrome.exe", @"C:\chrome.exe")!;

            service.UpdateRuleConstraints(
                rule.Id,
                "*.github.com, github.com",
                "10.0.0.0/8, ::1",
                "443, 1000-2000",
                "TCP",
                "team note");

            var snapshot = service.Config.Rules.Single(item => item.Id == rule.Id);
            Assert.Equal("*.github.com, github.com", snapshot.TargetHosts);
            Assert.Equal("10.0.0.0/8, ::1", snapshot.TargetIPs);
            Assert.Equal("443, 1000-2000", snapshot.TargetPorts);
            Assert.Equal("TCP", snapshot.Protocol);
            Assert.Equal("team note", snapshot.Note);
            // 未提及字段保持不变
            Assert.Equal("chrome.exe", snapshot.ExeName);
            Assert.Equal(ProxyMode.Proxy, snapshot.Mode);
            Assert.True(snapshot.IsEnabled);
            Assert.Equal(10, snapshot.Priority);

            var stored = AppConfigStore.Deserialize(File.ReadAllText(service.ConfigPath));
            var storedRule = stored.Rules.Single(item => item.Id == rule.Id);
            Assert.Equal("*.github.com, github.com", storedRule.TargetHosts);
            Assert.Equal("10.0.0.0/8, ::1", storedRule.TargetIPs);
            Assert.Equal("443, 1000-2000", storedRule.TargetPorts);
            Assert.Equal("TCP", storedRule.Protocol);
            Assert.Equal("team note", storedRule.Note);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void UpdateRuleConstraints_RejectsBadProtocol()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var rule = service.AddRuleByName("chrome.exe", @"C:\chrome.exe")!;

            Assert.Throws<ArgumentException>(() =>
                service.UpdateRuleConstraints(rule.Id, "a.com", "", "", "QUIC", ""));

            // 拒绝后配置保持不变
            var snapshot = service.Config.Rules.Single(item => item.Id == rule.Id);
            Assert.Equal("", snapshot.TargetHosts);
            Assert.Equal("", snapshot.Protocol);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void UpdateRuleConstraints_UnknownIdIsSilent()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var rule = service.AddRuleByName("chrome.exe", @"C:\chrome.exe")!;

            service.UpdateRuleConstraints("missing-id", "a.com", "1.2.3.4", "443", "UDP", "x");

            var snapshot = Assert.Single(service.Config.Rules);
            Assert.Equal(rule.Id, snapshot.Id);
            Assert.Equal("", snapshot.TargetHosts);
            Assert.Equal("", snapshot.TargetIPs);
            Assert.Equal("", snapshot.TargetPorts);
            Assert.Equal("", snapshot.Protocol);
            Assert.Equal("", snapshot.Note);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void UpdateRuleConstraints_NormalizesProtocolAndToleratesNulls()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var rule = service.AddRuleByName("chrome.exe", @"C:\chrome.exe")!;

            service.UpdateRuleConstraints(rule.Id, null!, null!, null!, "tcp", null);

            var snapshot = service.Config.Rules.Single(item => item.Id == rule.Id);
            Assert.Equal("", snapshot.TargetHosts);
            Assert.Equal("", snapshot.TargetIPs);
            Assert.Equal("", snapshot.TargetPorts);
            Assert.Equal("TCP", snapshot.Protocol);
            Assert.Equal("", snapshot.Note);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "intentroute-rule-edit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
