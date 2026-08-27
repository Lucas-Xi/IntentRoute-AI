[CmdletBinding()]
param(
    [string]$SingBoxPath
)

$ErrorActionPreference = 'Stop'
$version = '1.13.19'
$archiveName = "sing-box-$version-windows-amd64.zip"
$downloadUrl = "https://github.com/SagerNet/sing-box/releases/download/v$version/$archiveName"
$expectedSha256 = 'e011a4def2f5e2b143ed54adb2b1a20a6be407806ab4442f3667f1dd817a2c8d'
$environmentVariable = 'INTENTROUTE_TEST_SING_BOX_PATH'
$root = Split-Path -Parent $PSScriptRoot
$tests = Join-Path $root 'ProxyManager.Tests\ProxyManager.Tests.csproj'
$downloadRoot = $null
$previousExecutable = [Environment]::GetEnvironmentVariable($environmentVariable, 'Process')

try {
    if ([string]::IsNullOrWhiteSpace($SingBoxPath)) {
        $downloadRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
            'intentroute-pinned-sing-box-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $downloadRoot | Out-Null
        $archivePath = Join-Path $downloadRoot $archiveName
        $expandedPath = Join-Path $downloadRoot 'expanded'

        Write-Host "Downloading official sing-box v$version test dependency..."
        Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath
        $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualSha256 -ne $expectedSha256) {
            throw "Pinned sing-box archive SHA-256 mismatch. Expected $expectedSha256 but received $actualSha256."
        }

        Expand-Archive -LiteralPath $archivePath -DestinationPath $expandedPath
        $SingBoxPath = Join-Path $expandedPath "sing-box-$version-windows-amd64\sing-box.exe"
    }

    $resolvedExecutable = (Resolve-Path -LiteralPath $SingBoxPath).Path
    $versionOutput = (& $resolvedExecutable version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Pinned sing-box version probe failed with exit code $LASTEXITCODE." }
    if ($versionOutput -notmatch "(?m)^sing-box version $([Regex]::Escape($version))\r?$") {
        throw "Expected sing-box version $version; the supplied test executable reported a different or unrecognized version."
    }

    [Environment]::SetEnvironmentVariable($environmentVariable, $resolvedExecutable, 'Process')
    dotnet test $tests `
        --configuration Release `
        --no-restore `
        --filter 'Category=RealSingBox' `
        --logger 'trx;LogFileName=sing-box-integration.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Pinned real sing-box integration tests failed.' }

    Write-Host "Pinned real sing-box v$version accepted representative IntentRoute AI builder output."
}
finally {
    [Environment]::SetEnvironmentVariable($environmentVariable, $previousExecutable, 'Process')
    if ($downloadRoot -and (Test-Path -LiteralPath $downloadRoot)) {
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $resolvedDownloadRoot = [System.IO.Path]::GetFullPath($downloadRoot)
        if (-not $resolvedDownloadRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a pinned-test directory outside the system temp root: $resolvedDownloadRoot"
        }
        Remove-Item -LiteralPath $resolvedDownloadRoot -Recurse -Force
    }
}
