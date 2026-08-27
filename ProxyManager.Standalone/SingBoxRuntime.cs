using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProxyManager.Standalone;

/// <summary>
/// Discovers, validates, and manages a sing-box process for IntentRoute AI.
/// Does not download or bundle sing-box.
/// </summary>
public sealed class SingBoxRuntime : IDisposable, IAsyncDisposable
{
    public const string EnvExecutable = "INTENTROUTE_SING_BOX";
    public const string LegacyEnvExecutable = "PROXYMANAGER_SING_BOX";
    public const string DefaultExecutableName = "sing-box.exe";
    public const string DefaultConfigFileName = "sing-box.generated.json";
    internal const string RuntimeStateFileName = "sing-box.runtime-state.json";
    internal const string RuntimeLockFileName = "sing-box.runtime.lock";

    private static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultVersionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultStartupSettleTime = TimeSpan.FromMilliseconds(300);
    private static readonly Version MinimumSupportedVersion = new(1, 13, 0);
    private static readonly Regex VersionPattern = new(
        @"(?im)^\s*sing-box\s+version\s+v?(\d+)\.(\d+)\.(\d+)(?:\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
    private readonly string _configDirectory;
    private readonly string _configPath;
    private readonly string _processStatePath;
    private readonly string _runtimeLockPath;
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _startupSettleTime;
    private readonly ISingBoxExecutionBackend _executionBackend;
    private readonly string? _executableOverride;
    private readonly FileStream _runtimeLock;

    private ISingBoxManagedProcess? _process;
    private bool _disposed;
    private string? _executablePath;
    private string? _lastError;
    private string? _version;
    private SingBoxRuntimeState _state = SingBoxRuntimeState.Stopped;

    public SingBoxRuntime(string? configDirectory = null, int maxLogLines = 200, TimeSpan? checkTimeout = null)
        : this(
            configDirectory,
            maxLogLines,
            checkTimeout,
            DefaultStartupSettleTime,
            new SystemSingBoxExecutionBackend(),
            executableOverride: null)
    {
    }

    internal SingBoxRuntime(
        string? configDirectory,
        int maxLogLines,
        TimeSpan? checkTimeout,
        TimeSpan startupSettleTime,
        ISingBoxExecutionBackend executionBackend,
        string? executableOverride)
    {
        var dir = configDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppDataMigration.CurrentDirectoryName);
        }

        Directory.CreateDirectory(dir);
        _configDirectory = Path.GetFullPath(dir);
        _configPath = Path.Combine(_configDirectory, DefaultConfigFileName);
        _processStatePath = Path.Combine(_configDirectory, RuntimeStateFileName);
        _runtimeLockPath = Path.Combine(_configDirectory, RuntimeLockFileName);
        _maxLogLines = Math.Max(32, maxLogLines);
        _checkTimeout = checkTimeout ?? DefaultCheckTimeout;
        _startupSettleTime = startupSettleTime <= TimeSpan.Zero
            ? DefaultStartupSettleTime
            : startupSettleTime;
        _executionBackend = executionBackend ?? throw new ArgumentNullException(nameof(executionBackend));
        _executableOverride = executableOverride;

        _runtimeLock = AcquireRuntimeLock(_runtimeLockPath);
        try
        {
            RecoverOrphanedProcess();
            CleanupStaleRuntimeArtifacts();
        }
        catch
        {
            _runtimeLock.Dispose();
            throw;
        }
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

    internal void MarkRunningConfigurationStale(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var changed = false;
        lock (_gate)
        {
            var isRunning = false;
            try { isRunning = _process is { HasExited: false }; }
            catch { isRunning = false; }

            if (isRunning)
            {
                _state = SingBoxRuntimeState.RunningStale;
                _lastError = RedactSecrets(error);
                changed = true;
            }
        }

        if (changed)
            RaiseStatusChanged();
    }

    public IReadOnlyList<string> GetRecentLogs() => _recentLogs.ToArray();

    public void ClearRecentLogs()
    {
        while (_recentLogs.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Resolves sing-box from INTENTROUTE_SING_BOX, the legacy PROXYMANAGER_SING_BOX,
    /// the application directory, or PATH.
    /// </summary>
    public string? DiscoverExecutable(string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
            return ResolveCandidate(preferredPath.Trim());

        if (!string.IsNullOrWhiteSpace(_executableOverride))
            return ResolveCandidate(_executableOverride);

        var fromEnv = Environment.GetEnvironmentVariable(EnvExecutable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var resolved = ResolveCandidate(fromEnv.Trim());
            if (resolved != null) return resolved;
        }

        var fromLegacyEnv = Environment.GetEnvironmentVariable(LegacyEnvExecutable);
        if (!string.IsNullOrWhiteSpace(fromLegacyEnv))
        {
            var resolved = ResolveCandidate(fromLegacyEnv.Trim());
            if (resolved != null) return resolved;
        }

        var besideApp = Path.Combine(AppContext.BaseDirectory, DefaultExecutableName);
        if (File.Exists(besideApp)) return Path.GetFullPath(besideApp);

        var besideAppNoExt = Path.Combine(AppContext.BaseDirectory, "sing-box");
        if (File.Exists(besideAppNoExt)) return Path.GetFullPath(besideAppNoExt);

        return FindOnPath(DefaultExecutableName) ?? FindOnPath("sing-box");
    }

    public async Task<SingBoxReadinessResult> ProbeReadinessAsync(
        string? preferredPath = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? executablePath;
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            executablePath = ResolveCandidate(preferredPath.Trim());
            if (executablePath == null)
            {
                return SingBoxReadinessResult.NotReady(
                    preferredPath.Trim(),
                    "The selected sing-box executable no longer exists. Select another file or return to automatic discovery.");
            }
        }
        else
        {
            executablePath = !string.IsNullOrWhiteSpace(_executableOverride)
                ? ResolveCandidate(_executableOverride)
                : DiscoverExecutable();
            if (string.IsNullOrWhiteSpace(_executableOverride) && executablePath != null)
            {
                return SingBoxReadinessResult.NotReady(
                    executablePath,
                    "A sing-box candidate was discovered but has not been approved. Select that exact file with Browse before IntentRoute AI executes it.");
            }
        }
        if (executablePath == null)
        {
            return SingBoxReadinessResult.NotReady(
                null,
                "sing-box executable not found. Select a separately installed sing-box v1.13+ executable, set INTENTROUTE_SING_BOX, place it beside the app, or add it to PATH.");
        }

        var probe = await _executionBackend
            .GetVersionAsync(executablePath, DefaultVersionTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!probe.Success)
        {
            return SingBoxReadinessResult.NotReady(
                executablePath,
                RedactSecrets(Truncate(probe.Error ?? "sing-box version probe failed.", 800)));
        }

        var match = VersionPattern.Match(probe.Output ?? string.Empty);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Success ? match.Groups[3].Value : "0", out var patch))
        {
            return SingBoxReadinessResult.NotReady(
                executablePath,
                "sing-box version output could not be recognized; v1.13 or newer is required.");
        }

        var version = new Version(major, minor, patch);
        if (version < MinimumSupportedVersion)
        {
            return SingBoxReadinessResult.NotReady(
                executablePath,
                $"sing-box {version} is not supported; v{MinimumSupportedVersion} or newer is required.",
                version.ToString());
        }

        return SingBoxReadinessResult.Ready(executablePath, version.ToString());
    }

    public async Task<SingBoxApplyResult> ApplyAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            SetState(SingBoxRuntimeState.Starting, cancellationToken, clearError: true);

            SetState(SingBoxRuntimeState.Probing, cancellationToken);
            var readiness = await ProbeReadinessAsync(config.SingBoxExecutablePath, cancellationToken)
                .ConfigureAwait(false);
            if (!readiness.IsReady || readiness.ExecutablePath == null)
            {
                return FailApply(
                    readiness.Error ?? "sing-box is not ready.",
                    preserveRunningProcess: true);
            }

            var exe = readiness.ExecutablePath;
            var version = readiness.Version;

            var build = SingBoxConfigBuilder.Build(config, cancellationToken);
            if (!build.Success || string.IsNullOrEmpty(build.ConfigJson))
            {
                return FailApply(
                    build.Error ?? "Failed to build sing-box configuration.",
                    preserveRunningProcess: true);
            }

            var previousStatus = GetStatus();
            var previousConfig = TryReadConfigBytes();
            if (previousStatus.IsRunning && previousConfig == null)
            {
                return FailApply(
                    "The managed sing-box process is running, but its previous generated config is unavailable; stop it before applying a replacement.",
                    preserveRunningProcess: true);
            }

            var previousExecutablePath = previousStatus.IsRunning
                ? previousStatus.ExecutablePath
                : null;
            var previousVersion = previousStatus.IsRunning
                ? previousStatus.Version
                : null;

            string? candidatePath = null;
            try
            {
                candidatePath = await WriteCandidateConfigAsync(build.ConfigJson, cancellationToken).ConfigureAwait(false);

                SetState(SingBoxRuntimeState.Checking, cancellationToken);

                var check = await _executionBackend
                    .CheckAsync(exe, candidatePath, _checkTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (!check.Success)
                {
                    return FailApply(
                        RedactSecrets(Truncate(check.Error ?? "sing-box check failed.", 800)),
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
                await ReplaceProcessAsync(exe, version, _configPath, cancellationToken).ConfigureAwait(false);
                var runningStatus = CompleteSuccessfulApply(cancellationToken);
                return SingBoxApplyResult.Ok(runningStatus);
            }
            catch (OperationCanceledException)
            {
                var rollback = await TryRollbackAsync(
                    previousExecutablePath,
                    previousVersion,
                    previousConfig).ConfigureAwait(false);
                if (rollback.Success)
                {
                    FailApply(
                        "The replacement was canceled. The previous configuration was restored and restarted.",
                        preserveRunningProcess: true);
                }
                else
                {
                    var rollbackDetail = string.IsNullOrWhiteSpace(rollback.Error)
                        ? " No previous running configuration was available."
                        : " Rollback also failed: " + RedactSecrets(rollback.Error);
                    FailApply("The replacement was canceled." + rollbackDetail);
                }

                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var startupError = "Failed to start replacement sing-box: " + RedactSecrets(ex.Message);
                var rollback = await TryRollbackAsync(
                    previousExecutablePath,
                    previousVersion,
                    previousConfig).ConfigureAwait(false);
                if (rollback.Success)
                {
                    return FailApply(
                        startupError + " Previous configuration was restored and restarted.",
                        preserveRunningProcess: true);
                }

                TryDeleteConfig();
                var rollbackDetail = string.IsNullOrWhiteSpace(rollback.Error)
                    ? "No previous running configuration was available."
                    : " Rollback also failed: " + RedactSecrets(rollback.Error);
                return FailApply(startupError + rollbackDetail);
            }
        }
        catch (OperationCanceledException)
        {
            ConvergeCanceledApplyState();
            throw;
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

            CleanupManagedFiles();
        }
        finally
        {
            _applyGate.Release();
        }

        RaiseStatusChanged();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _applyGate.WaitAsync().ConfigureAwait(false);
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

        CleanupManagedFiles();
        _runtimeLock.Dispose();
        TryDeleteFile(_runtimeLockPath);
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

    private async Task<RollbackResult> TryRollbackAsync(
        string? executablePath,
        string? version,
        byte[]? previousConfig)
    {
        if (previousConfig == null)
            return RollbackResult.Fail(null);
        if (string.IsNullOrWhiteSpace(executablePath))
            return RollbackResult.Fail("The previous sing-box executable identity is unavailable.");

        try
        {
            RestoreConfig(previousConfig);
            await ReplaceProcessAsync(
                executablePath,
                version,
                _configPath,
                CancellationToken.None).ConfigureAwait(false);
            return RollbackResult.Ok();
        }
        catch (Exception ex)
        {
            TryDeleteConfig();
            return RollbackResult.Fail(ex.Message);
        }
    }

    private byte[]? TryReadConfigBytes()
    {
        try { return File.Exists(_configPath) ? File.ReadAllBytes(_configPath) : null; }
        catch { return null; }
    }

    private void RestoreConfig(byte[]? configBytes)
    {
        if (configBytes == null)
        {
            TryDeleteConfig();
            return;
        }

        var temporaryPath = _configPath + "." + Guid.NewGuid().ToString("N") + ".rollback";
        try
        {
            File.WriteAllBytes(temporaryPath, configBytes);
            if (File.Exists(_configPath))
                File.Replace(temporaryPath, _configPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, _configPath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task ReplaceProcessAsync(
        string exe,
        string? version,
        string configPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ISingBoxManagedProcess process;
        lock (_gate)
        {
            KillManagedProcess_NoLock();
            process = _executionBackend.Start(exe, configPath, OnProcessOutput, OnProcessExited);
            _process = process;
            _executablePath = exe;
            _version = version;
            WriteProcessState_NoLock(process, exe);
        }

        // Brief settle so immediate crash is visible to callers.
        await Task.Delay(_startupSettleTime, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (!ReferenceEquals(_process, process) || process.HasExited)
            {
                var code = process.ExitCode?.ToString() ?? "unknown";
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    TryDeleteFile(_processStatePath);
                }

                try { process.Dispose(); } catch { /* ignore teardown races */ }
                throw new InvalidOperationException($"sing-box exited during startup with code {code}.");
            }
        }
    }

    private SingBoxRuntimeStatus CompleteSuccessfulApply(CancellationToken cancellationToken)
    {
        SingBoxRuntimeStatus status;
        lock (_gate)
        {
            // The cancellation check and green-state publication must be atomic with
            // MarkRunningConfigurationStale. Whichever takes the lock second owns the
            // final state, so revoked approval cannot be overwritten by a late Apply.
            cancellationToken.ThrowIfCancellationRequested();

            var isRunning = false;
            try { isRunning = _process is { HasExited: false }; }
            catch { isRunning = false; }
            if (!isRunning)
                throw new InvalidOperationException("sing-box exited before startup completed.");

            _state = SingBoxRuntimeState.Running;
            _lastError = null;
            status = SnapshotStatus_NoLock();
        }

        RaiseStatusChanged();
        return status;
    }

    private void OnProcessOutput(string output)
    {
        if (string.IsNullOrEmpty(output)) return;
        var line = RedactSecrets(output);
        AppendLog(line);
        try { LogReceived?.Invoke(line); } catch { /* ignore subscriber errors */ }
    }

    private void OnProcessExited(ISingBoxManagedProcess process)
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (_process != null && ReferenceEquals(process, _process))
            {
                var code = _process.ExitCode?.ToString() ?? "unknown";
                _lastError = $"sing-box exited with code {code}.";
                _state = SingBoxRuntimeState.Failed;
                try { _process.Dispose(); } catch { /* ignore */ }
                _process = null;
                _executablePath = null;
                _version = null;
                TryDeleteFile(_processStatePath);
                TryDeleteConfig();
                changed = true;
            }
        }

        if (changed)
            RaiseStatusChanged();
    }

    private void KillManagedProcess_NoLock()
    {
        var process = _process;
        _process = null;
        _executablePath = null;
        _version = null;
        TryDeleteFile(_processStatePath);
        if (process == null) return;

        try { process.Kill(); } catch { /* already exited or access denied */ }
        try { process.Dispose(); } catch { /* ignore */ }
    }

    private static FileStream AcquireRuntimeLock(string lockPath)
    {
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException ex)
        {
            throw new SingBoxRuntimeOwnershipException(
                "Another IntentRoute AI instance is already managing this configuration directory.",
                ex);
        }
    }

    private void RecoverOrphanedProcess()
    {
        try
        {
            if (!File.Exists(_processStatePath)) return;

            var state = JObject.Parse(File.ReadAllText(_processStatePath));
            var processId = state["process_id"]?.Value<int>() ?? 0;
            var expectedStartTicks = state["start_time_utc_ticks"]?.Value<long>() ?? 0;
            var expectedExecutable = state["executable_path"]?.Value<string>();
            if (processId <= 0 || expectedStartTicks <= 0) return;

            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return;

            var actualStartTicks = process.StartTime.ToUniversalTime().Ticks;
            if (Math.Abs(actualStartTicks - expectedStartTicks) > TimeSpan.TicksPerSecond)
                return;

            try
            {
                var actualExecutable = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(expectedExecutable) &&
                    !string.IsNullOrWhiteSpace(actualExecutable) &&
                    !Path.GetFullPath(actualExecutable).Equals(
                        Path.GetFullPath(expectedExecutable),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch
            {
                // Exact PID plus start time still protects against ordinary PID reuse.
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch
        {
            // Recovery is best effort; stale secret-bearing files are still removed below.
        }
        finally
        {
            TryDeleteFile(_processStatePath);
        }
    }

    private void WriteProcessState_NoLock(ISingBoxManagedProcess process, string executablePath)
    {
        var state = new JObject
        {
            ["process_id"] = process.Id,
            ["start_time_utc_ticks"] = process.StartTimeUtc.Ticks,
            ["executable_path"] = Path.GetFullPath(executablePath)
        };

        var temporaryPath = _processStatePath + "." + Guid.NewGuid().ToString("N") + ".candidate";
        try
        {
            File.WriteAllText(
                temporaryPath,
                state.ToString(Formatting.None),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(_processStatePath))
                File.Replace(temporaryPath, _processStatePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, _processStatePath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private void CleanupStaleRuntimeArtifacts()
    {
        TryDeleteConfig();
        TryDeleteFile(_processStatePath);
        CleanupCandidateFiles();
    }

    private void CleanupManagedFiles()
    {
        TryDeleteConfig();
        TryDeleteFile(_processStatePath);
        CleanupCandidateFiles();
    }

    private void CleanupCandidateFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _configDirectory,
                         DefaultConfigFileName + ".*.candidate",
                         SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(path);
            }

            foreach (var path in Directory.EnumerateFiles(
                         _configDirectory,
                         DefaultConfigFileName + ".*.rollback",
                         SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(path);
            }

            foreach (var path in Directory.EnumerateFiles(
                         _configDirectory,
                         RuntimeStateFileName + ".*.candidate",
                         SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(path);
            }
        }
        catch { /* best effort cleanup */ }
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
            {
                KillManagedProcess_NoLock();
                TryDeleteConfig();
            }
            _lastError = error;
            _state = isRunning ? SingBoxRuntimeState.RunningStale : SingBoxRuntimeState.Failed;
        }

        RaiseStatusChanged();
        return SingBoxApplyResult.Fail(error, GetStatus());
    }

    private void ConvergeCanceledApplyState()
    {
        var changed = false;
        lock (_gate)
        {
            // Replacement-stage cancellation already publishes a precise rollback
            // outcome, while approval revocation may already have marked the process
            // stale. Preserve either result instead of overwriting its explanation.
            if (_state is SingBoxRuntimeState.RunningStale or SingBoxRuntimeState.Failed)
                return;

            var isRunning = false;
            try { isRunning = _process is { HasExited: false }; }
            catch { isRunning = false; }

            if (!isRunning)
            {
                KillManagedProcess_NoLock();
                TryDeleteConfig();
            }

            _lastError = isRunning
                ? "The apply was canceled; the previously active sing-box process remains running with an older configuration."
                : "The apply was canceled before sing-box became active.";
            _state = isRunning
                ? SingBoxRuntimeState.RunningStale
                : SingBoxRuntimeState.Failed;
            changed = true;
        }

        if (changed)
            RaiseStatusChanged();
    }

    private void SetState(
        SingBoxRuntimeState state,
        CancellationToken cancellationToken,
        bool clearError = false)
    {
        lock (_gate)
        {
            // Transient Apply states must obey the same ordering as Running. Once
            // approval revocation cancels the token, a late continuation cannot
            // overwrite RunningStale with Probing, Checking, or Starting.
            cancellationToken.ThrowIfCancellationRequested();
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
            Version = _version,
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

    private readonly struct RollbackResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public static RollbackResult Ok() => new() { Success = true };
        public static RollbackResult Fail(string? error) => new() { Success = false, Error = error };
    }
}

public enum SingBoxRuntimeState
{
    Stopped,
    Probing,
    Starting,
    Checking,
    Running,
    RunningStale,
    Failed
}

public sealed class SingBoxRuntimeStatus
{
    public SingBoxRuntimeState State { get; init; }
    public string? ExecutablePath { get; init; }
    public string? ConfigPath { get; init; }
    public string? LastError { get; init; }
    public string? Version { get; init; }
    public int? ProcessId { get; init; }
    public bool IsRunning { get; init; }
}

public sealed class SingBoxReadinessResult
{
    private SingBoxReadinessResult(
        bool isReady,
        string? executablePath,
        string? version,
        string? error)
    {
        IsReady = isReady;
        ExecutablePath = executablePath;
        Version = version;
        Error = error;
    }

    public bool IsReady { get; }
    public string? ExecutablePath { get; }
    public string? Version { get; }
    public string? Error { get; }

    public static SingBoxReadinessResult Ready(string executablePath, string version) =>
        new(true, executablePath, version, null);

    public static SingBoxReadinessResult NotReady(
        string? executablePath,
        string error,
        string? version = null) =>
        new(false, executablePath, version, error);
}

public sealed class SingBoxRuntimeOwnershipException : InvalidOperationException
{
    public SingBoxRuntimeOwnershipException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
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
