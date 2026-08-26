[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tests = Join-Path $root 'ProxyManager.Tests\ProxyManager.Tests.csproj'

dotnet restore $tests --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }

dotnet test $tests --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }
