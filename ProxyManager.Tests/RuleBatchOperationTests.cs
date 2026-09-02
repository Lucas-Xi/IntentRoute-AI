using System.IO;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class RuleBatchOperationTests
{
    [Fact]
    public void SetRulesEnabled_UpdatesAllMatchingRulesAtomically()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var first = service.AddRuleByName("a.exe", @"C:\a.exe")!;
            var second = service.AddRuleByName("b.exe", @"C:\b.exe")!;
            var third = service.AddRuleByName("c.exe", @"C:\c.exe")!;
            service.ToggleRule(third.Id);

            var updated = service.SetRulesEnabled(new[] { first.Id, second.Id, third.Id }, enabled: true);

            Assert.Equal(3, updated);
            Assert.All(service.Config.Rules, rule => Assert.True(rule.IsEnabled));
            Assert.True(File.Exists(service.ConfigPath));
            var stored = AppConfigStore.Deserialize(File.ReadAllText(service.ConfigPath));
            Assert.Equal(3, stored.Rules.Count);
            Assert.All(stored.Rules, rule => Assert.True(rule.IsEnabled));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void SetRulesEnabled_IgnoresUnknownIdsAndEmptyInput()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var first = service.AddRuleByName("a.exe", @"C:\a.exe")!;
            var second = service.AddRuleByName("b.exe", @"C:\b.exe")!;

            var updated = service.SetRulesEnabled(new[] { first.Id, "missing-id" }, enabled: false);

            Assert.Equal(1, updated);
            Assert.False(service.Config.Rules.Single(rule => rule.Id == first.Id).IsEnabled);
            Assert.True(service.Config.Rules.Single(rule => rule.Id == second.Id).IsEnabled);

            Assert.Equal(0, service.SetRulesEnabled(Array.Empty<string>(), enabled: true));
            Assert.Equal(2, service.Config.Rules.Count);
            Assert.False(service.Config.Rules.Single(rule => rule.Id == first.Id).IsEnabled);
            Assert.True(service.Config.Rules.Single(rule => rule.Id == second.Id).IsEnabled);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void SetRulesMode_SetsModeOnAllMatches()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var first = service.AddRuleByName("a.exe", @"C:\a.exe")!;
            var second = service.AddRuleByName("b.exe", @"C:\b.exe")!;
            var third = service.AddRuleByName("c.exe", @"C:\c.exe")!;

            var updated = service.SetRulesMode(new[] { first.Id, second.Id, third.Id }, ProxyMode.Block);

            Assert.Equal(3, updated);
            Assert.All(service.Config.Rules, rule => Assert.Equal(ProxyMode.Block, rule.Mode));
            Assert.Equal("a.exe", service.Config.Rules.Single(rule => rule.Id == first.Id).ExeName);
            Assert.Equal(10, service.Config.Rules.Single(rule => rule.Id == first.Id).Priority);
            Assert.Equal(20, service.Config.Rules.Single(rule => rule.Id == second.Id).Priority);
            Assert.Equal(30, service.Config.Rules.Single(rule => rule.Id == third.Id).Priority);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void RemoveRules_RemovesMatchesAndPreservesOrderAndPriorityOfTheRest()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var first = service.AddRuleByName("a.exe", @"C:\a.exe")!;
            var second = service.AddRuleByName("b.exe", @"C:\b.exe")!;
            var third = service.AddRuleByName("c.exe", @"C:\c.exe")!;
            var fourth = service.AddRuleByName("d.exe", @"C:\d.exe")!;
            var before = service.Config.Rules.ToDictionary(rule => rule.Id, rule => rule.Priority);

            var removed = service.RemoveRules(new[] { first.Id, third.Id });

            Assert.Equal(2, removed);
            Assert.Equal(new[] { second.Id, fourth.Id }, service.Config.Rules.Select(rule => rule.Id));
            Assert.Equal(before[second.Id], service.Config.Rules.Single(rule => rule.Id == second.Id).Priority);
            Assert.Equal(before[fourth.Id], service.Config.Rules.Single(rule => rule.Id == fourth.Id).Priority);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void BatchOperations_DoNotTouchPriority()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);
            var first = service.AddRuleByName("a.exe", @"C:\a.exe")!;
            var second = service.AddRuleByName("b.exe", @"C:\b.exe")!;
            var third = service.AddRuleByName("c.exe", @"C:\c.exe")!;
            var ids = new[] { first.Id, second.Id, third.Id };
            var prioritiesBefore = service.Config.Rules.ToDictionary(rule => rule.Id, rule => rule.Priority);

            Assert.Equal(3, service.SetRulesEnabled(ids, enabled: false));
            Assert.Equal(3, service.SetRulesMode(ids, ProxyMode.Direct));

            Assert.Equal(3, service.Config.Rules.Count);
            Assert.All(
                service.Config.Rules,
                rule => Assert.Equal(prioritiesBefore[rule.Id], rule.Priority));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "intentroute-rule-batch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
