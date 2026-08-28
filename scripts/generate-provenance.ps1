[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

# Emits an unsigned build-provenance inventory into the package: version, commit,
# builder, SDK, and the exact NuGet dependency set from the lock files. This is an
# inventory manifest, not a signature; code signing still requires a certificate.
$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path

[xml]$csproj = Get-Content (Join-Path $project 'ProxyManager.Standalone.csproj')
$version = [string]$csproj.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Cannot read <Version> from the project file.' }

$commit = git -C $project rev-parse HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) { $commit = 'unknown' }

if ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY -and $env:GITHUB_RUN_ID) {
    $builtBy = "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)"
}
else {
    $builtBy = 'local build'
}

$globalJson = Get-Content (Join-Path (Split-Path $project -Parent) 'global.json') -Raw | ConvertFrom-Json
$sdk = $globalJson.sdk.version

$dependencies = New-Object System.Collections.Generic.List[object]
$seen = @{}
function Add-Dependency([string]$name, [string]$version, [string]$contentHash) {
    if ([string]::IsNullOrWhiteSpace($name)) { return }
    # Skip project-to-project references and path-shaped versions from the lock files.
    if ($version -match '[\\/]') { return }
    $key = "$name/$version"
    if ($seen.ContainsKey($key)) { return }
    $seen[$key] = $true
    $script:dependencies.Add([ordered]@{
        name = $name
        version = $version
        contentHash = $contentHash
    })
}
foreach ($lockName in @('ProxyManager.Standalone/packages.lock.json', 'ProxyManager.Tests/packages.lock.json')) {
    $lockPath = Join-Path (Split-Path $project -Parent) $lockName
    if (-not (Test-Path -LiteralPath $lockPath)) { continue }
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    foreach ($framework in @($lock.dependencies.PSObject.Properties)) {
        foreach ($entry in @($framework.Value.PSObject.Properties)) {
            Add-Dependency $entry.Name ([string]$entry.Value.resolved) ([string]$entry.Value.contentHash)
            foreach ($child in @($entry.Value.dependencies.PSObject.Properties)) {
                Add-Dependency $child.Name ([string]$child.Value) $null
            }
        }
    }
}
$unique = $dependencies |
    ForEach-Object { [pscustomobject]$_ } |
    Sort-Object name, version

$provenance = [ordered]@{
    schema = 'intentroute-provenance/1'
    version = $version
    commit = $commit
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    builtBy = $builtBy
    dotnetSdk = $sdk
    targetFramework = 'net8.0-windows'
    runtimeIdentifier = 'win-x64'
    dependencies = @($unique)
}

$path = Join-Path $output 'provenance.json'
$provenance | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8
Write-Host "Wrote build provenance inventory to $path"
