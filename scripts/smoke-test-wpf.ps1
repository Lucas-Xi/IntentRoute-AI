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
        $diagnostic = if (Test-Path -LiteralPath $diagnosticPath -PathType Leaf) {
            Get-Content -Raw -LiteralPath $diagnosticPath
        }
        else {
            'No redacted managed-exception diagnostic was produced.'
        }
        throw "Published IntentRouteAI.exe created an unexpected main-window title: '$($process.MainWindowTitle)'.`n$diagnostic"
    }

    # Keyboard-navigation coverage: the main window must expose an assistive-technology
    # name, a usable set of keyboard-focusable controls, Tab traversal that moves focus,
    # and arrow-key movement within the navigation radio group.
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -Namespace Native -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
'@
    $null = [Native.Win32]::SetForegroundWindow($process.MainWindowHandle)
    Start-Sleep -Milliseconds 500

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if ([string]::IsNullOrWhiteSpace($root.Current.Name)) {
        throw 'Main window exposes no UI Automation name for assistive technology.'
    }

    $focusableCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::IsKeyboardFocusableProperty, $true)
    $focusableCount = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $focusableCondition).Count
    # The count depends on first-run state (disabled controls are not keyboard focusable);
    # a fresh machine exposes about 19, so the floor keeps margin below that.
    if ($focusableCount -lt 15) {
        throw "Main window exposes only $focusableCount keyboard-focusable controls; expected at least 15."
    }

    $navIdCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavRules')
    $navRules = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $navIdCondition)
    if ($null -eq $navRules) {
        throw 'Main window does not expose the NavRules automation element.'
    }
    $navRules.SetFocus()
    Start-Sleep -Milliseconds 200

    [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
    Start-Sleep -Milliseconds 200
    $afterTab = [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($null -ne $afterTab -and $afterTab.Current.AutomationId -eq 'NavRules') {
        throw 'Tab key did not move keyboard focus away from the first navigation item.'
    }

    $navRules.SetFocus()
    Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
    Start-Sleep -Milliseconds 200
    $afterDown = [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($null -eq $afterDown -or $afterDown.Current.AutomationId -ne 'NavPolicy') {
        $seenId = if ($null -ne $afterDown) { $afterDown.Current.AutomationId } else { '<none>' }
        throw "Down arrow did not move focus within the navigation group (focused: '$seenId')."
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
