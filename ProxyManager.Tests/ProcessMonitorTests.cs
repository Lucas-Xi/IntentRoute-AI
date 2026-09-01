using System.IO;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class ProcessMonitorTests
{
    [Fact]
    public void TryGetProcessPath_ResolvesCurrentProcessPath()
    {
        var success = ProcessMonitor.TryGetProcessPath((uint)Environment.ProcessId, out var path);

        Assert.True(success);
        Assert.NotEmpty(path);
        Assert.Contains(Path.DirectorySeparatorChar, path);
    }

    [Fact]
    public void TryGetProcessPath_FailsForUnmatchedPid()
    {
        var success = ProcessMonitor.TryGetProcessPath(0x7FFFFFFF, out var path);

        Assert.False(success);
        Assert.Empty(path);
    }

    [Fact]
    public void GetRunningProcesses_IncludesCurrentProcess()
    {
        var snapshot = ProcessMonitor.GetRunningProcesses();

        if (Environment.ProcessPath is not { } processPath)
        {
            Assert.Fail("The test host process path is unavailable.");
            return;
        }
        var currentName = Path.GetFileName(processPath);
        Assert.Contains(snapshot.Values, name => string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AddRuleByName_PersistsNewRule()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            var rule = service.AddRuleByName("testproc.exe", @"C:\dir\testproc.exe");
            Assert.NotNull(rule);

            var stored = Assert.Single(service.Config.Rules);
            Assert.Equal(rule!.Id, stored.Id);
            Assert.Equal("testproc.exe", stored.ExeName);
            Assert.Equal(@"C:\dir\testproc.exe", stored.ExePath);
            Assert.Equal(ProxyMode.Proxy, stored.Mode);
            Assert.Equal(10, stored.Priority);
            Assert.True(stored.IsEnabled);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void AddRuleByName_RejectsDuplicateCaseInsensitively()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            var first = service.AddRuleByName("TESTPROC.exe", @"C:\dir\TESTPROC.exe");
            Assert.NotNull(first);
            Assert.Null(service.AddRuleByName("testproc.exe", @"C:\dir\testproc.exe"));
            Assert.Single(service.Config.Rules);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddRuleByName_RejectsBlankName(string exeName)
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            Assert.Throws<ArgumentException>(() => service.AddRuleByName(exeName, @"C:\dir\x.exe"));
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void AddRuleByName_AllowsEmptyExePath()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            var rule = service.AddRuleByName("testproc.exe", "");

            Assert.NotNull(rule);
            Assert.Equal("", rule!.ExePath);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    [Fact]
    public void AddRule_FilePathWrapperStillWorks()
    {
        var appDataRoot = CreateTempDirectory();
        try
        {
            using var service = new AppService(appDataRoot, startMonitor: false, applyOnStart: false);

            var rule = service.AddRule(@"C:\x\foo.exe");

            Assert.NotNull(rule);
            Assert.Equal("foo.exe", rule!.ExeName);
            Assert.Equal(@"C:\x\foo.exe", rule.ExePath);
        }
        finally
        {
            Directory.Delete(appDataRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "intentroute-process-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
