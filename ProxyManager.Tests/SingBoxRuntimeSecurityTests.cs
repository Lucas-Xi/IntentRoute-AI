using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

    [Fact]
    public void Constructor_RemovesStaleRuntimeArtifactsAndPreventsConcurrentOwner()
    {
        var tempDirectory = CreateTempDirectory();
        var configPath = Path.Combine(tempDirectory, SingBoxRuntime.DefaultConfigFileName);
        var candidatePath = configPath + ".stale.candidate";
        var rollbackPath = configPath + ".stale.rollback";
        var statePath = Path.Combine(tempDirectory, SingBoxRuntime.RuntimeStateFileName);
        File.WriteAllText(configPath, "{\"password\":\"stale\"}");
        File.WriteAllText(candidatePath, "stale");
        File.WriteAllText(rollbackPath, "stale");
        File.WriteAllText(statePath, "{}");

        try
        {
            using (var runtime = new SingBoxRuntime(tempDirectory))
            {
                Assert.False(File.Exists(configPath));
                Assert.False(File.Exists(candidatePath));
                Assert.False(File.Exists(rollbackPath));
                Assert.False(File.Exists(statePath));

                var error = Assert.Throws<InvalidOperationException>(() => new SingBoxRuntime(tempDirectory));
                Assert.Contains("already managing", error.Message, StringComparison.OrdinalIgnoreCase);
            }

            using var replacementOwner = new SingBoxRuntime(tempDirectory);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_RestoresPreviousConfigAndProcessWhenReplacementExitsDuringStartup()
    {
        var tempDirectory = CreateTempDirectory();
        var fakeExecutable = Path.Combine(tempDirectory, "fake-sing-box.exe");
        File.WriteAllBytes(fakeExecutable, []);
        var backend = new FakeSingBoxExecutionBackend(configJson =>
            configJson.Contains("127.0.0.2", StringComparison.Ordinal) ? 42 : null);

        try
        {
            using var runtime = new SingBoxRuntime(
                tempDirectory,
                maxLogLines: 64,
                checkTimeout: TimeSpan.FromSeconds(1),
                startupSettleTime: TimeSpan.FromMilliseconds(60),
                executionBackend: backend,
                executableOverride: fakeExecutable);

            var first = await runtime.ApplyAsync(CreateConfig("127.0.0.1"));
            Assert.True(first.Success, first.Error);
            var firstProcessId = first.Status.ProcessId;

            var replacement = await runtime.ApplyAsync(CreateConfig("127.0.0.2"));

            Assert.False(replacement.Success);
            Assert.Contains("restored and restarted", replacement.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(SingBoxRuntimeState.Running, replacement.Status.State);
            Assert.True(replacement.Status.IsRunning);
            Assert.NotEqual(firstProcessId, replacement.Status.ProcessId);

            var activeConfig = File.ReadAllText(runtime.ConfigPath);
            Assert.Contains("127.0.0.1", activeConfig, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.2", activeConfig, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(tempDirectory, "*.candidate"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_RemovesSecretBearingRuntimeConfigAndProcessState()
    {
        var tempDirectory = CreateTempDirectory();
        var fakeExecutable = Path.Combine(tempDirectory, "fake-sing-box.exe");
        File.WriteAllBytes(fakeExecutable, []);

        try
        {
            using var runtime = new SingBoxRuntime(
                tempDirectory,
                maxLogLines: 64,
                checkTimeout: TimeSpan.FromSeconds(1),
                startupSettleTime: TimeSpan.FromMilliseconds(20),
                executionBackend: new FakeSingBoxExecutionBackend(_ => null),
                executableOverride: fakeExecutable);

            var result = await runtime.ApplyAsync(CreateConfig("127.0.0.1"));
            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(runtime.ConfigPath));
            Assert.True(File.Exists(Path.Combine(tempDirectory, SingBoxRuntime.RuntimeStateFileName)));

            runtime.Stop();

            Assert.False(File.Exists(runtime.ConfigPath));
            Assert.False(File.Exists(Path.Combine(tempDirectory, SingBoxRuntime.RuntimeStateFileName)));
            Assert.Equal(SingBoxRuntimeState.Stopped, runtime.GetStatus().State);
            Assert.False(runtime.GetStatus().IsRunning);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Constructor_RecoversRecordedOrphanProcessBeforeDeletingItsConfig()
    {
        var tempDirectory = CreateTempDirectory();
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var orphan = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = commandProcessor,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        orphan.StartInfo.ArgumentList.Add("/d");
        orphan.StartInfo.ArgumentList.Add("/c");
        orphan.StartInfo.ArgumentList.Add("ping 127.0.0.1 -n 30 > nul");
        Assert.True(orphan.Start());

        var configPath = Path.Combine(tempDirectory, SingBoxRuntime.DefaultConfigFileName);
        var statePath = Path.Combine(tempDirectory, SingBoxRuntime.RuntimeStateFileName);
        File.WriteAllText(configPath, "{\"password\":\"stale-secret\"}");
        File.WriteAllText(
            statePath,
            new JObject
            {
                ["process_id"] = orphan.Id,
                ["start_time_utc_ticks"] = orphan.StartTime.ToUniversalTime().Ticks,
                ["executable_path"] = Path.GetFullPath(commandProcessor)
            }.ToString(Formatting.None));

        try
        {
            using var runtime = new SingBoxRuntime(tempDirectory);

            Assert.True(orphan.WaitForExit(5000));
            Assert.False(File.Exists(configPath));
            Assert.False(File.Exists(statePath));
        }
        finally
        {
            try
            {
                if (!orphan.HasExited)
                    orphan.Kill(entireProcessTree: true);
            }
            catch { /* best-effort test cleanup */ }

            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "proxymanager-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static AppConfig CreateConfig(string host) => new()
    {
        ProxyServers =
        [
            new ProxyServer
            {
                Id = "proxy-1",
                Name = "Test proxy",
                ProxyType = ProxyType.Socks5,
                Host = host,
                Port = 10808,
                Enabled = true
            }
        ]
    };

    private sealed class FakeSingBoxExecutionBackend(Func<string, int?> startupExitCode)
        : ISingBoxExecutionBackend
    {
        private int _nextProcessId = 41000;

        public Task<SingBoxCheckResult> CheckAsync(
            string executablePath,
            string configPath,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SingBoxCheckResult.Ok());
        }

        public ISingBoxManagedProcess Start(
            string executablePath,
            string configPath,
            Action<string> outputReceived,
            Action<ISingBoxManagedProcess> exited)
        {
            var configJson = File.ReadAllText(configPath);
            var process = new FakeManagedProcess(
                Interlocked.Increment(ref _nextProcessId),
                exited);
            outputReceived("fake sing-box started");

            var exitCode = startupExitCode(configJson);
            if (exitCode.HasValue)
                process.ExitSoon(exitCode.Value);

            return process;
        }
    }

    private sealed class FakeManagedProcess(int id, Action<ISingBoxManagedProcess> exited)
        : ISingBoxManagedProcess
    {
        private readonly object _gate = new();
        private bool _hasExited;
        private int? _exitCode;

        public int Id { get; } = id;
        public DateTime StartTimeUtc { get; } = DateTime.UtcNow;

        public bool HasExited
        {
            get { lock (_gate) return _hasExited; }
        }

        public int? ExitCode
        {
            get { lock (_gate) return _exitCode; }
        }

        public void ExitSoon(int exitCode)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(10);
                Exit(exitCode);
            });
        }

        public void Kill() => Exit(0);
        public void Dispose() { }

        private void Exit(int exitCode)
        {
            lock (_gate)
            {
                if (_hasExited) return;
                _hasExited = true;
                _exitCode = exitCode;
            }

            exited(this);
        }
    }
}
