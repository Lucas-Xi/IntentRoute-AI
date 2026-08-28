[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
$application = Join-Path $resolvedOutput 'IntentRouteAI.exe'

if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "Published package is missing IntentRouteAI.exe: $resolvedOutput"
}

if ((Get-Item -LiteralPath $application).Length -le 0) {
    throw 'Published IntentRouteAI.exe is empty.'
}

$forbidden = Get-ChildItem -LiteralPath $resolvedOutput -File -Recurse | Where-Object {
    $_.Name -match '(?i)^sing-box(?:\.exe)?$' -or
    $_.Name -match '(?i)\.generated\.json$' -or
    $_.Name -match '(?i)runtime-state|runtime\.lock|\.candidate$|\.rollback$' -or
    $_.Name -match '(?i)^\.env(?:\..+)?$'
}

if ($forbidden) {
    $relativeNames = $forbidden | ForEach-Object {
        [System.IO.Path]::GetRelativePath($resolvedOutput, $_.FullName)
    }
    throw "Published package contains forbidden runtime or secret-bearing files: $($relativeNames -join ', ')"
}

$provenancePath = Join-Path $resolvedOutput 'provenance.json'
if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
    throw 'Published package is missing provenance.json (unsigned build inventory).'
}
$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
foreach ($required in @('schema', 'version', 'commit', 'generatedAtUtc', 'builtBy', 'dependencies')) {
    if ($null -eq $provenance.$required) {
        throw "provenance.json is missing the required field '$required'."
    }
}
if ($provenance.dependencies.Count -lt 1) {
    throw 'provenance.json lists no dependencies; the inventory looks incomplete.'
}

Write-Host "Verified package contents at $resolvedOutput (no bundled sing-box or generated runtime files; provenance inventory present)."
