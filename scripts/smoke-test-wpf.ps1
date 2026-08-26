[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidateRange(5, 60)]
    [int]$LaunchTimeoutSeconds = 20,

    [ValidateRange(5, 60)]
    [int]$CloseTimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
$application = Join-Path $resolvedOutput 'IntentRouteAI.exe'
$expectedTitle = 'IntentRoute AI - Windows 智能分流'
$diagnosticVariable = 'INTENTROUTE_SMOKE_DIAGNOSTIC_PATH'
$diagnosticPath = Join-Path ([System.IO.Path]::GetTempPath()) (
    'intentroute-wpf-smoke-' + [Guid]::NewGuid().ToString('N') + '.txt')
$previousDiagnosticPath = [Environment]::GetEnvironmentVariable($diagnosticVariable, 'Process')

if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "Published package is missing IntentRouteAI.exe: $resolvedOutput"
}

$process = $null
try {
    [Environment]::SetEnvironmentVariable($diagnosticVariable, $diagnosticPath, 'Process')
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $application
    $startInfo.WorkingDirectory = $resolvedOutput
    # Honor the packaged requireAdministrator manifest exactly as an end user does.
    # GitHub-hosted Windows runners execute with the required administrative token.
    $startInfo.UseShellExecute = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'Published IntentRouteAI.exe did not start.'
    }

    $launchDeadline = [DateTime]::UtcNow.AddSeconds($LaunchTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $launchDeadline) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) {
            throw "Published IntentRouteAI.exe exited before creating its main window (exit code $($process.ExitCode))."
        }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            break
        }
    }

    $process.Refresh()
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw "Published IntentRouteAI.exe did not create a main window within $LaunchTimeoutSeconds seconds."
    }
    if ($process.MainWindowTitle -ne $expectedTitle) {
        throw "Published IntentRouteAI.exe created an unexpected main-window title: '$($process.MainWindowTitle)'."
    }

    if (-not $process.CloseMainWindow()) {
        throw 'Published IntentRouteAI.exe did not accept a normal window-close request.'
    }
    if (-not $process.WaitForExit($CloseTimeoutSeconds * 1000)) {
        throw "Published IntentRouteAI.exe did not close within $CloseTimeoutSeconds seconds."
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        $diagnostic = if (Test-Path -LiteralPath $diagnosticPath -PathType Leaf) {
            Get-Content -Raw -LiteralPath $diagnosticPath
        }
        else {
            'No redacted managed-exception diagnostic was produced.'
        }
        throw "Published IntentRouteAI.exe returned exit code $($process.ExitCode) after a normal close.`n$diagnostic"
    }

    Write-Host 'WPF smoke test passed: published single-file app created the expected main window and closed cleanly.'
}
finally {
    if ($process) {
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
                $null = $process.WaitForExit(5000)
            }
        }
        catch {
            # Best-effort cleanup; preserve the original smoke-test failure.
        }
        $process.Dispose()
    }
    Remove-Item -LiteralPath $diagnosticPath -Force -ErrorAction SilentlyContinue
    [Environment]::SetEnvironmentVariable($diagnosticVariable, $previousDiagnosticPath, 'Process')
}
