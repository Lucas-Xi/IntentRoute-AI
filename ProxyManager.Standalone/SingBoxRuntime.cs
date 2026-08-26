using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProxyManager.Standalone;

/// <summary>
/// Discovers, validates, and manages a sing-box process for ProxyManager.
/// Does not download or bundle sing-box.
/// </summary>
public sealed class SingBoxRuntime : IDisposable
{
    public const string EnvExecutable = "PROXYMANAGER_SING_BOX";
    public const string DefaultExecutableName = "sing-box.exe";
    public const string DefaultConfigFileName = "sing-box.generated.json";

    private static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly Regex SecretLinePattern = new(
        @"(?i)(password|passwd|pwd|secret|token|credential)\s*([:=]\s*)\S+",
        RegexOptions.Compiled);
    private static readonly Regex JsonPasswordPattern = new(
        @"(?i)(""password""\s*:\s*)""[^""]*""",
        RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly ConcurrentQueue<string> _recentLogs = new();
    private readonly int _maxLogLines;
    private readonly string _configPath;
    private readonly TimeSpan _checkTimeout;

    private Process? _process;
    private bool _disposed;
    private string? _executablePath;
    private string? _lastError;
    private SingBoxRuntimeState _state = SingBoxRuntimeState.Stopped;

    public SingBoxRuntime(string? configDirectory = null, int maxLogLines = 200, TimeSpan? checkTimeout = null)
    {
        var dir = configDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProxyManager");
        }

        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, DefaultConfigFileName);
        _maxLogLines = Math.Max(32, maxLogLines);
        _checkTimeout = checkTimeout ?? DefaultCheckTimeout;
    }

    public event Action<string>? LogReceived;
    public event Action<SingBoxRuntimeStatus>? StatusChanged;

    public string ConfigPath => _configPath;

    public SingBoxRuntimeStatus GetStatus()
    {
        lock (_gate)
        {
            return SnapshotStatus_NoLock();
        }
    }

    public IReadOnlyList<string> GetRecentLogs() => _recentLogs.ToArray();

    public void ClearRecentLogs()
    {
        while (_recentLogs.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Resolves sing-box from PROXYMANAGER_SING_BOX, the application directory, or PATH.
    /// </summary>
    public string? DiscoverExecutable()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvExecutable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var resolved = ResolveCandidate(fromEnv.Trim());
            if (resolved != null) return resolved;
        }

        var besideApp = Path.Combine(AppContext.BaseDirectory, DefaultExecutableName);
        if (File.Exists(besideApp)) return Path.GetFullPath(besideApp);

        var besideAppNoExt = Path.Combine(AppContext.BaseDirectory, "sing-box");
        if (File.Exists(besideAppNoExt)) return Path.GetFullPath(besideAppNoExt);

        return FindOnPath(DefaultExecutableName) ?? FindOnPath("sing-box");
    }

    public async Task<SingBoxApplyResult> ApplyAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            SetState(SingBoxRuntimeState.Starting, clearError: true);

            var exe = DiscoverExecutable();
            if (exe == null)
            {
                return FailApply(
                    "sing-box executable not found. Set PROXYMANAGER_SING_BOX, place sing-box.exe beside the app, or add it to PATH.",
                    preserveRunningProcess: true);
            }

            _executablePath = exe;

            var build = SingBoxConfigBuilder.Build(config);
            if (!build.Success || string.IsNullOrEmpty(build.ConfigJson))
            {
                return FailApply(
                    build.Error ?? "Failed to build sing-box configuration.",
                    preserveRunningProcess: true);
            }

            string? candidatePath = null;
            try
            {
                candidatePath = await WriteCandidateConfigAsync(build.ConfigJson, cancellationToken).ConfigureAwait(false);

                SetState(SingBoxRuntimeState.Checking);

                var check = await RunCheckAsync(exe, candidatePath, cancellationToken).ConfigureAwait(false);
                if (!check.Success)
                {
                    return FailApply(
                        check.Error ?? "sing-box check failed.",
                        preserveRunningProcess: true);
                }

                PromoteCandidateConfig(candidatePath);
                candidatePath = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return FailApply(
                    "Failed to prepare sing-box config: " + RedactSecrets(ex.Message),
                    preserveRunningProcess: true);
            }
            finally
            {
                TryDeleteFile(candidatePath);
            }

            try
            {
                await ReplaceProcessAsync(exe, _configPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return FailApply("Failed to start sing-box: " + RedactSecrets(ex.Message));
            }

            SetState(SingBoxRuntimeState.Running, clearError: true);
            return SingBoxApplyResult.Ok(GetStatus());
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public void Stop()
    {
        _applyGate.Wait();
        try
        {
            lock (_gate)
            {
                KillManagedProcess_NoLock();
                _state = SingBoxRuntimeState.Stopped;
                _lastError = null;
            }
        }
        finally
        {
            _applyGate.Release();
        }

        RaiseStatusChanged();
    }

    public void Dispose()
    {
        _applyGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;

            lock (_gate)
            {
                KillManagedProcess_NoLock();
                _state = SingBoxRuntimeState.Stopped;
            }
        }
        finally
        {
            _applyGate.Release();
        }

        TryDeleteConfig();
        RaiseStatusChanged();
        GC.SuppressFinalize(this);
    }

    private async Task<string> WriteCandidateConfigAsync(string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);
        var candidatePath = _configPath + "." + Guid.NewGuid().ToString("N") + ".candidate";

        try
        {
            await File.WriteAllTextAsync(candidatePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                .ConfigureAwait(false);
            return candidatePath;
        }
        catch
        {
            TryDeleteFile(candidatePath);
            throw;
        }
    }

    private void PromoteCandidateConfig(string candidatePath)
    {
        if (File.Exists(_configPath))
            File.Replace(candidatePath, _configPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(candidatePath, _configPath);
    }

    private async Task<SingBoxCheckResult> RunCheckAsync(string exe, string configPath, CancellationToken cancellationToken)
    {
        var psi = CreateSingBoxStartInfo(exe);
        psi.ArgumentList.Add("check");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
                return SingBoxCheckResult.Fail("Failed to start sing-box check process.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_checkTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                return SingBoxCheckResult.Fail($"sing-box check timed out after {_checkTimeout.TotalSeconds:0}s.");
            }

            if (process.ExitCode != 0)
            {
                var detail = RedactSecrets((stderr.ToString() + "\n" + stdout.ToString()).Trim());
                if (string.IsNullOrWhiteSpace(detail))
                    detail = $"exit code {process.ExitCode}";
                return SingBoxCheckResult.Fail("sing-box check failed: " + Truncate(detail, 800));
            }

            return SingBoxCheckResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryKillProcessTree(process);
            return SingBoxCheckResult.Fail("sing-box check error: " + RedactSecrets(ex.Message));
        }
    }

    private async Task ReplaceProcessAsync(string exe, string configPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Process process;
        lock (_gate)
        {
            KillManagedProcess_NoLock();

            var psi = CreateSingBoxStartInfo(exe);
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += OnProcessOutput;
            process.ErrorDataReceived += OnProcessOutput;
            process.Exited += OnProcessExited;

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Process.Start returned false.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
        }

        // Brief settle so immediate crash is visible to callers.
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_process == process && process.HasExited)
            {
                var code = process.ExitCode;
                _process = null;
                throw new InvalidOperationException($"sing-box exited immediately with code {code}.");
            }
        }
    }

    private void OnProcessOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        var line = RedactSecrets(e.Data);
        AppendLog(line);
        try { LogReceived?.Invoke(line); } catch { /* ignore subscriber errors */ }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_process != null && ReferenceEquals(sender, _process))
            {
                var code = _process.HasExited ? _process.ExitCode : -1;
                _lastError = $"sing-box exited with code {code}.";
                _state = SingBoxRuntimeState.Failed;
                try { _process.Dispose(); } catch { /* ignore */ }
                _process = null;
            }
        }

        RaiseStatusChanged();
    }

    private void KillManagedProcess_NoLock()
    {
        var process = _process;
        _process = null;
        if (process == null) return;

        try
        {
            process.OutputDataReceived -= OnProcessOutput;
            process.ErrorDataReceived -= OnProcessOutput;
            process.Exited -= OnProcessExited;
        }
        catch { /* ignore */ }

        TryKillProcessTree(process);
        try { process.Dispose(); } catch { /* ignore */ }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* already exited or access denied */ }

        try { process.WaitForExit(3000); } catch { /* ignore */ }
    }

    private void TryDeleteConfig()
    {
        TryDeleteFile(_configPath);
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort cleanup */ }
    }

    private SingBoxApplyResult FailApply(string error, bool preserveRunningProcess = false)
    {
        lock (_gate)
        {
            var isRunning = false;
            if (preserveRunningProcess)
            {
                try { isRunning = _process is { HasExited: false }; }
                catch { isRunning = false; }
            }

            if (!isRunning)
                KillManagedProcess_NoLock();
            _lastError = error;
            _state = isRunning ? SingBoxRuntimeState.Running : SingBoxRuntimeState.Failed;
        }

        RaiseStatusChanged();
        return SingBoxApplyResult.Fail(error, GetStatus());
    }

    private void SetState(SingBoxRuntimeState state, bool clearError = false)
    {
        lock (_gate)
        {
            _state = state;
            if (clearError) _lastError = null;
        }

        RaiseStatusChanged();
    }

    private SingBoxRuntimeStatus SnapshotStatus_NoLock()
    {
        int? pid = null;
        var running = false;
        try
        {
            if (_process is { HasExited: false })
            {
                running = true;
                pid = _process.Id;
            }
        }
        catch { /* process may have exited */ }

        return new SingBoxRuntimeStatus
        {
            State = _state,
            ExecutablePath = _executablePath,
            ConfigPath = _configPath,
            LastError = _lastError,
            ProcessId = pid,
            IsRunning = running
        };
    }

    private void RaiseStatusChanged()
    {
        SingBoxRuntimeStatus status;
        try { status = GetStatus(); }
        catch { return; }

        try { StatusChanged?.Invoke(status); } catch { /* ignore */ }
    }

    private void AppendLog(string line)
    {
        _recentLogs.Enqueue(line);
        while (_recentLogs.Count > _maxLogLines && _recentLogs.TryDequeue(out _)) { }
    }

    private static ProcessStartInfo CreateSingBoxStartInfo(string exe) => new()
    {
        FileName = exe,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = false,
        WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
    };

    private static string? ResolveCandidate(string pathOrDir)
    {
        try
        {
            if (File.Exists(pathOrDir))
                return Path.GetFullPath(pathOrDir);

            if (Directory.Exists(pathOrDir))
            {
                var exe = Path.Combine(pathOrDir, DefaultExecutableName);
                if (File.Exists(exe)) return Path.GetFullPath(exe);
                var noExt = Path.Combine(pathOrDir, "sing-box");
                if (File.Exists(noExt)) return Path.GetFullPath(noExt);
            }
        }
        catch { /* ignore invalid paths */ }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(segment.Trim().Trim('"'), fileName);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch { /* ignore bad PATH entries */ }
        }

        return null;
    }

    internal static string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = JsonPasswordPattern.Replace(text, "$1\"***\"");
        text = SecretLinePattern.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "***");
        return text;
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
        return text[..max] + "…";
    }

    private readonly struct SingBoxCheckResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public static SingBoxCheckResult Ok() => new() { Success = true };
        public static SingBoxCheckResult Fail(string error) => new() { Success = false, Error = error };
    }
}

public enum SingBoxRuntimeState
{
    Stopped,
    Starting,
    Checking,
    Running,
    Failed
}

public sealed class SingBoxRuntimeStatus
{
    public SingBoxRuntimeState State { get; init; }
    public string? ExecutablePath { get; init; }
    public string? ConfigPath { get; init; }
    public string? LastError { get; init; }
    public int? ProcessId { get; init; }
    public bool IsRunning { get; init; }
}

public sealed class SingBoxApplyResult
{
    private SingBoxApplyResult(bool success, string? error, SingBoxRuntimeStatus status)
    {
        Success = success;
        Error = error;
        Status = status;
    }

    public bool Success { get; }
    public string? Error { get; }
    public SingBoxRuntimeStatus Status { get; }

    public static SingBoxApplyResult Ok(SingBoxRuntimeStatus status) => new(true, null, status);
    public static SingBoxApplyResult Fail(string error, SingBoxRuntimeStatus status) => new(false, error, status);
}
