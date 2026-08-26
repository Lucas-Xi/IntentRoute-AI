using System.Diagnostics;
using System.IO;
using System.Text;

namespace ProxyManager.Standalone;

internal interface ISingBoxExecutionBackend
{
    Task<SingBoxCheckResult> CheckAsync(
        string executablePath,
        string configPath,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ISingBoxManagedProcess Start(
        string executablePath,
        string configPath,
        Action<string> outputReceived,
        Action<ISingBoxManagedProcess> exited);
}

internal interface ISingBoxManagedProcess : IDisposable
{
    int Id { get; }
    DateTime StartTimeUtc { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    void Kill();
}

internal readonly struct SingBoxCheckResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    public static SingBoxCheckResult Ok() => new() { Success = true };
    public static SingBoxCheckResult Fail(string error) => new() { Success = false, Error = error };
}

internal sealed class SystemSingBoxExecutionBackend : ISingBoxExecutionBackend
{
    public async Task<SingBoxCheckResult> CheckAsync(
        string executablePath,
        string configPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = CreateStartInfo(executablePath);
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
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                return SingBoxCheckResult.Fail($"sing-box check timed out after {timeout.TotalSeconds:0}s.");
            }

            if (process.ExitCode != 0)
            {
                var detail = (stderr.ToString() + "\n" + stdout.ToString()).Trim();
                if (string.IsNullOrWhiteSpace(detail))
                    detail = $"exit code {process.ExitCode}";
                return SingBoxCheckResult.Fail("sing-box check failed: " + detail);
            }

            return SingBoxCheckResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryKillProcessTree(process);
            return SingBoxCheckResult.Fail("sing-box check error: " + ex.Message);
        }
    }

    public ISingBoxManagedProcess Start(
        string executablePath,
        string configPath,
        Action<string> outputReceived,
        Action<ISingBoxManagedProcess> exited)
    {
        var psi = CreateStartInfo(executablePath);
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);

        var managed = new SystemSingBoxManagedProcess(psi, outputReceived, exited);
        try
        {
            managed.Start();
            return managed;
        }
        catch
        {
            managed.Dispose();
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath) => new()
    {
        FileName = executablePath,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = false,
        WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
    };

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

    private sealed class SystemSingBoxManagedProcess : ISingBoxManagedProcess
    {
        private readonly Process _process;
        private readonly Action<string> _outputReceived;
        private readonly Action<ISingBoxManagedProcess> _exited;
        private bool _disposed;

        public SystemSingBoxManagedProcess(
            ProcessStartInfo startInfo,
            Action<string> outputReceived,
            Action<ISingBoxManagedProcess> exited)
        {
            _outputReceived = outputReceived;
            _exited = exited;
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutput;
            _process.ErrorDataReceived += OnOutput;
            _process.Exited += OnExited;
        }

        public int Id => _process.Id;
        public DateTime StartTimeUtc => _process.StartTime.ToUniversalTime();

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch { return true; }
            }
        }

        public int? ExitCode
        {
            get
            {
                try { return _process.HasExited ? _process.ExitCode : null; }
                catch { return null; }
            }
        }

        public void Start()
        {
            if (!_process.Start())
                throw new InvalidOperationException("Process.Start returned false.");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public void Kill() => TryKillProcessTree(_process);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _process.OutputDataReceived -= OnOutput;
                _process.ErrorDataReceived -= OnOutput;
                _process.Exited -= OnExited;
            }
            catch { /* ignore teardown races */ }

            try { _process.Dispose(); } catch { /* ignore teardown races */ }
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
                _outputReceived(e.Data);
        }

        private void OnExited(object? sender, EventArgs e) => _exited(this);
    }
}
