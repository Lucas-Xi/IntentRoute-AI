[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'ProxyManager.Standalone\ProxyManager.Standalone.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts\win-x64'
}

dotnet restore $project --runtime win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host "Published ProxyManager to $OutputDirectory"
