[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tests = Join-Path $root 'ProxyManager.Tests\ProxyManager.Tests.csproj'

$output = & dotnet list $tests package --vulnerable --include-transitive --format json 2>&1
if ($LASTEXITCODE -ne 0) {
    $output | Write-Host
    throw 'NuGet vulnerability query failed'
}

$json = $output -join [Environment]::NewLine
try {
    $null = $json | ConvertFrom-Json
}
catch {
    $output | Write-Host
    throw 'NuGet vulnerability query did not return valid JSON'
}

if ($json -match '"vulnerabilities"\s*:') {
    $output | Write-Host
    throw 'Known vulnerable NuGet dependencies were found'
}

Write-Host 'No known vulnerable NuGet dependencies were found.'
