using System.IO;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class PolicyRuntimeOrderTests
{
    [Fact]
    public void MoveRule_UsesTheCanonicalOrderShownByTheUi()
    {
        var directory = Path.Combine(Path.GetTempPath(), "intent-route-order-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var service = new AppService(directory, startMonitor: false, applyOnStart: false);
            service.ImportRules(
            [
                Rule("source-first.exe", priority: 30),
                Rule("runtime-first.exe", priority: 10),
                Rule("runtime-second.exe", priority: 20)
            ]);

            var runtimeSecond = service.Config.Rules.Single(rule => rule.ExeName == "runtime-second.exe");
            service.MoveRule(runtimeSecond.Id, -1);

            var moved = PolicyRuntimeOrder.All(service.Config.Rules);
            Assert.Equal(
                ["runtime-second.exe", "runtime-first.exe", "source-first.exe"],
                moved.Select(rule => rule.ExeName).ToArray());
            Assert.Equal([10, 20, 30], moved.Select(rule => rule.Priority).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProxyRule Rule(string processName, int priority) => new()
    {
        ExeName = processName,
        Mode = ProxyMode.Direct,
        IsEnabled = true,
        Priority = priority,
        CreatedAt = "2026-01-01 00:00"
    };
}
