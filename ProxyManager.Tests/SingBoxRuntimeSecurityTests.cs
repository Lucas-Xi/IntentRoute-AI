using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
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

                var error = Assert.Throws<SingBoxRuntimeOwnershipException>(() => new SingBoxRuntime(tempDirectory));
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
            Assert.Equal(SingBoxRuntimeState.RunningStale, replacement.Status.State);
            Assert.True(replacement.Status.IsRunning);
            Assert.False(string.IsNullOrWhiteSpace(replacement.Status.LastError));
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
    public async Task Apply_CheckFailurePreservesActualExecutableIdentity()
    {
        var tempDirectory = CreateTempDirectory();
        var executableA = Path.Combine(tempDirectory, "sing-box-a.exe");
        var executableB = Path.Combine(tempDirectory, "sing-box-b.exe");
        File.WriteAllBytes(executableA, []);
        File.WriteAllBytes(executableB, []);
        var backend = new FakeSingBoxExecutionBackend(_ => null)
        {
            VersionOutputForExecutable = path => PathsEqual(path, executableA)
                ? "sing-box version 1.13.1"
                : "sing-box version 1.14.0",
            CheckResultForExecutable = (path, _) => PathsEqual(path, executableB)
                ? SingBoxCheckResult.Fail("candidate check failed")
                : SingBoxCheckResult.Ok()
        };

        try
        {
            using var runtime = new SingBoxRuntime(
                tempDirectory,
                maxLogLines: 64,
                checkTimeout: TimeSpan.FromSeconds(1),
                startupSettleTime: TimeSpan.FromMilliseconds(20),
                executionBackend: backend,
                executableOverride: null);
            var configA = CreateConfig("127.0.0.1");
            configA.SingBoxExecutablePath = executableA;
            var configB = CreateConfig("127.0.0.2");
            configB.SingBoxExecutablePath = executableB;

            var first = await runtime.ApplyAsync(configA);
            Assert.True(first.Success, first.Error);
            var firstProcessId = first.Status.ProcessId;

            var rejected = await runtime.ApplyAsync(configB);

            Assert.False(rejected.Success);
            Assert.Equal(SingBoxRuntimeState.RunningStale, rejected.Status.State);
            Assert.True(rejected.Status.IsRunning);
            Assert.Equal(firstProcessId, rejected.Status.ProcessId);
            Assert.Equal(Path.GetFullPath(executableA), rejected.Status.ExecutablePath);
            Assert.Equal("1.13.1", rejected.Status.Version);
            Assert.Equal([Path.GetFullPath(executableA)], backend.StartedExecutablePaths);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_StartupFailureRollsBackWithPreviousExecutableIdentity()
    {
        var tempDirectory = CreateTempDirectory();
        var executableA = Path.Combine(tempDirectory, "sing-box-a.exe");
        var executableB = Path.Combine(tempDirectory, "sing-box-b.exe");
        File.WriteAllBytes(executableA, []);
        File.WriteAllBytes(executableB, []);
        var backend = new FakeSingBoxExecutionBackend(configJson =>
            configJson.Contains("127.0.0.2", StringComparison.Ordinal) ? 42 : null)
        {
            VersionOutputForExecutable = path => PathsEqual(path, executableA)
                ? "sing-box version 1.13.1"
                : "sing-box version 1.14.0"
        };

        try
        {
            using var runtime = new SingBoxRuntime(
                tempDirectory,
                maxLogLines: 64,
                checkTimeout: TimeSpan.FromSeconds(1),
                startupSettleTime: TimeSpan.FromMilliseconds(60),
                executionBackend: backend,
                executableOverride: null);
            var configA = CreateConfig("127.0.0.1");
            configA.SingBoxExecutablePath = executableA;
            var configB = CreateConfig("127.0.0.2");
            configB.SingBoxExecutablePath = executableB;

            var first = await runtime.ApplyAsync(configA);
            Assert.True(first.Success, first.Error);

            var replacement = await runtime.ApplyAsync(configB);

            Assert.False(replacement.Success);
            Assert.Equal(SingBoxRuntimeState.RunningStale, replacement.Status.State);
            Assert.Equal(Path.GetFullPath(executableA), replacement.Status.ExecutablePath);
            Assert.Equal("1.13.1", replacement.Status.Version);
            Assert.Equal(
                [Path.GetFullPath(executableA), Path.GetFullPath(executableB), Path.GetFullPath(executableA)],
                backend.StartedExecutablePaths);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_CancellationDuringStartupRestoresPreviousProcessAndCannotReturnToRunning()
    {
        var tempDirectory = CreateTempDirectory();
        var fakeExecutable = Path.Combine(tempDirectory, "fake-sing-box.exe");
        File.WriteAllBytes(fakeExecutable, []);
        var backend = new FakeSingBoxExecutionBackend(_ => null);

        try
        {
            using var runtime = new SingBoxRuntime(
                tempDirectory,
                maxLogLines: 64,
                checkTimeout: TimeSpan.FromSeconds(1),
                startupSettleTime: TimeSpan.FromMilliseconds(200),
                executionBackend: backend,
                executableOverride: fakeExecutable);

            var first = await runtime.ApplyAsync(CreateConfig("127.0.0.1"));
            Assert.True(first.Success, first.Error);

            using var cancellation = new CancellationTokenSource();
            var replacement = runtime.ApplyAsync(CreateConfig("127.0.0.2"), cancellation.Token);
            await WaitUntilAsync(() => backend.StartCount >= 2);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacement);

            var status = runtime.GetStatus();
            Assert.Equal(3, backend.StartCount);
            Assert.Equal(SingBoxRuntimeState.RunningStale, status.State);
            Assert.True(status.IsRunning);
            Assert.Contains("restored and restarted", status.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("127.0.0.1", File.ReadAllText(runtime.ConfigPath), StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.2", File.ReadAllText(runtime.ConfigPath), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MarkRunningConfigurationStale_PreservesProcessAndRaisesWarningState()
    {
        var tempDirectory = CreateTempDirectory();
        var fakeExecutable = Path.Combine(tempDirectory, "fake-sing-box.exe");
        File.WriteAllBytes(fakeExecutable, []);
        var backend = new FakeSingBoxExecutionBackend(_ => null);

        try
        {
            using var runtime = new SingBoxRuntime(
                tempDirectory,
                maxLogLines: 64,
                checkTimeout: TimeSpan.FromSeconds(1),
                startupSettleTime: TimeSpan.FromMilliseconds(20),
                executionBackend: backend,
                executableOverride: fakeExecutable);
            var applied = await runtime.ApplyAsync(CreateConfig("127.0.0.1"));
            Assert.True(applied.Success, applied.Error);
            var processId = applied.Status.ProcessId;

            runtime.MarkRunningConfigurationStale("Current configuration requires renewed approval.");

            var status = runtime.GetStatus();
            Assert.Equal(SingBoxRuntimeState.RunningStale, status.State);
            Assert.True(status.IsRunning);
            Assert.Equal(processId, status.ProcessId);
            Assert.Contains("renewed approval", status.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, backend.StartCount);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("sing-box version 1.12.9", false)]
    [InlineData("sing-box version 1.13.0", true)]
    [InlineData("sing-box version 1.14.2", true)]
    [InlineData("sing-box version v1.13.1", true)]
    [InlineData("sing-box version 1.13", false)]
    [InlineData("sing-box version 1.13.0-alpha", false)]
    [InlineData("unexpected output", false)]
    public async Task ProbeReadiness_EnforcesRecognizedVersion113OrNewer(string output, bool expectedReady)
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
                executionBackend: new FakeSingBoxExecutionBackend(_ => null, output),
                executableOverride: fakeExecutable);

            var result = await runtime.ProbeReadinessAsync();

            Assert.Equal(expectedReady, result.IsReady);
            Assert.Equal(Path.GetFullPath(fakeExecutable), result.ExecutablePath);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ProbeReadiness_DoesNotExecuteAnUnapprovedDiscoveredCandidate()
    {
        var tempDirectory = CreateTempDirectory();
        var fakeExecutable = Path.Combine(tempDirectory, "unapproved-sing-box.exe");
        var previous = Environment.GetEnvironmentVariable(SingBoxRuntime.EnvExecutable);
        File.WriteAllBytes(fakeExecutable, []);
        var backend = new FakeSingBoxExecutionBackend(_ => null);
        try
        {
            Environment.SetEnvironmentVariable(SingBoxRuntime.EnvExecutable, fakeExecutable);
            using var runtime = new SingBoxRuntime(
                Path.Combine(tempDirectory, "config"),
                maxLogLines: 64,
                checkTimeout: TimeSpan.FromSeconds(1),
                startupSettleTime: TimeSpan.FromMilliseconds(20),
                executionBackend: backend,
                executableOverride: null);

            var result = await runtime.ProbeReadinessAsync();

            Assert.False(result.IsReady);
            Assert.Equal(Path.GetFullPath(fakeExecutable), result.ExecutablePath);
            Assert.Contains("not been approved", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, backend.VersionCount);
            Assert.Equal(0, backend.CheckCount);
            Assert.Equal(0, backend.StartCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SingBoxRuntime.EnvExecutable, previous);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_DoesNotCheckOrStartWhenVersionIsUnsupported()
    {
        var tempDirectory = CreateTempDirectory();
        var fakeExecutable = Path.Combine(tempDirectory, "fake-sing-box.exe");
        File.WriteAllBytes(fakeExecutable, []);
        var backend = new FakeSingBoxExecutionBackend(_ => null, "sing-box version 1.12.9");
        try
        {
            using var runtime = new SingBoxRuntime(
                tempDirectory,
                maxLogLines: 64,
                checkTimeout: TimeSpan.FromSeconds(1),
                startupSettleTime: TimeSpan.FromMilliseconds(20),
                executionBackend: backend,
                executableOverride: fakeExecutable);

            var result = await runtime.ApplyAsync(CreateConfig("127.0.0.1"));

            Assert.False(result.Success);
            Assert.Contains("not supported", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, backend.CheckCount);
            Assert.Equal(0, backend.StartCount);
            Assert.False(File.Exists(runtime.ConfigPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_PreservesRunningProcessWhenReplacementVersionBecomesUnsupported()
    {
        var tempDirectory = CreateTempDirectory();
        var fakeExecutable = Path.Combine(tempDirectory, "fake-sing-box.exe");
        File.WriteAllBytes(fakeExecutable, []);
        var backend = new FakeSingBoxExecutionBackend(_ => null);
        try
        {
            using var runtime = new SingBoxRuntime(
                tempDirectory,
                maxLogLines: 64,
                checkTimeout: TimeSpan.FromSeconds(1),
                startupSettleTime: TimeSpan.FromMilliseconds(20),
                executionBackend: backend,
                executableOverride: fakeExecutable);

            var first = await runtime.ApplyAsync(CreateConfig("127.0.0.1"));
            Assert.True(first.Success, first.Error);
            backend.VersionOutput = "sing-box version 1.12.9";

            var replacement = await runtime.ApplyAsync(CreateConfig("127.0.0.2"));

            Assert.False(replacement.Success);
            Assert.True(replacement.Status.IsRunning);
            Assert.Equal(first.Status.ProcessId, replacement.Status.ProcessId);
            Assert.Equal(1, backend.CheckCount);
            Assert.Equal(1, backend.StartCount);
            Assert.Contains("127.0.0.1", File.ReadAllText(runtime.ConfigPath), StringComparison.Ordinal);
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for the expected runtime transition.");
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    internal sealed class FakeSingBoxExecutionBackend(
        Func<string, int?> startupExitCode,
        string versionOutput = "sing-box version 1.13.0")
        : ISingBoxExecutionBackend
    {
        private int _nextProcessId = 41000;
        private int _startCount;
        private readonly ConcurrentQueue<string> _startedExecutablePaths = new();
        public int VersionCount { get; private set; }
        public int CheckCount { get; private set; }
        public int StartCount => Volatile.Read(ref _startCount);
        public string VersionOutput { get; set; } = versionOutput;
        public Func<string, string>? VersionOutputForExecutable { get; init; }
        public Func<string, string, SingBoxCheckResult>? CheckResultForExecutable { get; init; }
        public IReadOnlyList<string> StartedExecutablePaths => _startedExecutablePaths.ToArray();

        public Task<SingBoxVersionProbeResult> GetVersionAsync(
            string executablePath,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VersionCount++;
            var output = VersionOutputForExecutable?.Invoke(executablePath) ?? VersionOutput;
            return Task.FromResult(SingBoxVersionProbeResult.Ok(output));
        }

        public Task<SingBoxCheckResult> CheckAsync(
            string executablePath,
            string configPath,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCount++;
            return Task.FromResult(
                CheckResultForExecutable?.Invoke(executablePath, configPath) ??
                SingBoxCheckResult.Ok());
        }

        public ISingBoxManagedProcess Start(
            string executablePath,
            string configPath,
            Action<string> outputReceived,
            Action<ISingBoxManagedProcess> exited)
        {
            Interlocked.Increment(ref _startCount);
            _startedExecutablePaths.Enqueue(Path.GetFullPath(executablePath));
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
