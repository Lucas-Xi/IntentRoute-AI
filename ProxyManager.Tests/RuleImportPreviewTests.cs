using System.IO;
using ProxyManager.Standalone;
using ProxyManager.Standalone.Localization;
using Xunit;

namespace ProxyManager.Tests;

public sealed class RuleImportPreviewTests
{
    [Fact]
    public void CreateKey_TreatsConstraintListsAsNormalizedIdentity()
    {
        var left = Rule("chrome.exe", hosts: "GitHub.com, *.github.com", ips: "10.0.0.1;10.0.0.2", ports: "443 80", protocol: "TCP", mode: ProxyMode.Proxy);
        var right = Rule("CHROME.EXE", hosts: "*.github.com, github.com", ips: "10.0.0.2, 10.0.0.1", ports: "80,443", protocol: "TCP", mode: ProxyMode.Proxy);

        Assert.Equal(RuleIdentity.CreateKey(left), RuleIdentity.CreateKey(right));
        Assert.NotEqual(
            RuleIdentity.CreateKey(left),
            RuleIdentity.CreateKey(Rule("chrome.exe", hosts: "gitlab.com", protocol: "TCP", mode: ProxyMode.Proxy)));
        Assert.NotEqual(
            RuleIdentity.CreateKey(left),
            RuleIdentity.CreateKey(Rule("chrome.exe", hosts: "github.com, *.github.com", protocol: "TCP", mode: ProxyMode.Direct)));
    }

    [Fact]
    public void Preview_ClassifiesAddSkipExistingAndInFileDuplicates()
    {
        var existing = new[]
        {
            Rule("chrome.exe", hosts: "github.com", protocol: "TCP", mode: ProxyMode.Proxy)
        };
        var incoming = new[]
        {
            Rule("chrome.exe", hosts: "github.com", protocol: "TCP", mode: ProxyMode.Proxy),
            Rule("chrome.exe", hosts: "openai.com", protocol: "TCP", mode: ProxyMode.Proxy),
            Rule("chrome.exe", hosts: "openai.com", protocol: "TCP", mode: ProxyMode.Proxy),
            Rule("curl.exe", mode: ProxyMode.Direct)
        };

        var preview = RuleImportPlanner.Build(existing, incoming);

        Assert.Equal(2, preview.AddCount);
        Assert.Equal(1, preview.SkipExistingCount);
        Assert.Equal(1, preview.SkipDuplicateInFileCount);
        Assert.Equal(2, preview.SkipCount);
        Assert.True(preview.HasAdditions);
        Assert.Equal(
            new[]
            {
                RuleImportDisposition.SkipExisting,
                RuleImportDisposition.Add,
                RuleImportDisposition.SkipDuplicateInFile,
                RuleImportDisposition.Add
            },
            preview.Rows.Select(row => row.Disposition));
        Assert.Equal(["chrome.exe", "curl.exe"], preview.RulesToAdd.Select(rule => rule.ExeName));
        Assert.Contains("openai.com", preview.RulesToAdd[0].TargetHosts, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Strings.ImportPreviewDispositionSkipExisting, preview.Rows[0].DispositionText);
        Assert.Contains("2", preview.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_EmptyIncomingIsNothingToAdd()
    {
        var preview = RuleImportPlanner.Build([Rule("a.exe")], []);

        Assert.Equal(0, preview.AddCount);
        Assert.False(preview.HasAdditions);
        Assert.Empty(preview.Rows);
        Assert.Empty(preview.RulesToAdd);
    }

    [Fact]
    public void Preview_NullIncomingRuleThrowsWithoutClassifying()
    {
        Assert.Throws<InvalidDataException>(() =>
            RuleImportPlanner.Build([], [Rule("a.exe"), null!]));
    }

    [Fact]
    public void ImportRules_AddsSameProcessWhenConstraintsDiffer()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            service.AddRuleByName("chrome.exe", @"C:\chrome.exe");

            var added = service.ImportRules(
            [
                Rule("chrome.exe", hosts: "github.com", protocol: "TCP", mode: ProxyMode.Proxy),
                Rule("chrome.exe", hosts: "openai.com", protocol: "UDP", mode: ProxyMode.Direct)
            ]);

            Assert.Equal(2, added);
            Assert.Equal(3, service.Config.Rules.Count);
            Assert.Equal(3, service.Config.Rules.Count(rule => rule.ExeName == "chrome.exe"));
            var stored = AppConfigStore.Deserialize(File.ReadAllText(service.ConfigPath));
            Assert.Equal(3, stored.Rules.Count);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void ImportRules_SkipsExactIdentityAndInFileDuplicatesWithoutCommitWhenNothingAdds()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var existing = service.AddRuleByName("chrome.exe", @"C:\chrome.exe")!;
            service.UpdateRuleConstraints(existing.Id, "github.com", "", "", "TCP", "");
            var original = File.ReadAllBytes(service.ConfigPath);

            var added = service.ImportRules(
            [
                Rule("chrome.exe", hosts: "github.com", protocol: "TCP", mode: ProxyMode.Proxy),
                Rule("chrome.exe", hosts: "github.com", protocol: "TCP", mode: ProxyMode.Proxy)
            ]);

            Assert.Equal(0, added);
            Assert.Single(service.Config.Rules);
            Assert.Equal(original, File.ReadAllBytes(service.ConfigPath));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void PreviewAndImport_ShareTheSameClassification()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            service.AddRuleByName("keep.exe", @"C:\keep.exe");
            var incoming = new[]
            {
                Rule("keep.exe", mode: ProxyMode.Proxy),
                Rule("new.exe", mode: ProxyMode.Block)
            };

            var preview = service.PreviewRuleImport(incoming);
            var added = service.ImportRules(incoming);

            Assert.Equal(preview.AddCount, added);
            Assert.Equal(1, preview.SkipExistingCount);
            Assert.Equal(["new.exe"], service.Config.Rules.Select(rule => rule.ExeName).Where(name => name == "new.exe"));
            Assert.Equal(2, service.Config.Rules.Count);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    private static ProxyRule Rule(
        string exeName,
        string hosts = "",
        string ips = "",
        string ports = "",
        string protocol = "",
        ProxyMode mode = ProxyMode.Proxy) => new()
    {
        ExeName = exeName,
        TargetHosts = hosts,
        TargetIPs = ips,
        TargetPorts = ports,
        Protocol = protocol,
        Mode = mode,
        IsEnabled = true
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "intentroute-rule-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
