[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
# Single source of the curated release-notes extraction used by the Release
# workflow and exercised for every released version by test-release-notes.ps1.
$changelog = Get-Content "$PSScriptRoot\..\CHANGELOG.md" -Raw
$pattern = "(?ms)^## \[$Version\] - .*?(?=^## \[|\z)"
$section = [regex]::Match($changelog, $pattern).Value
if ([string]::IsNullOrWhiteSpace($section)) {
    throw "CHANGELOG.md has no section for [$Version]."
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path ([IO.Path]::GetTempPath()) "release-notes-$Version.md"
}
@($section.Trim(), '', '---', '', '### Commits since the previous release', '') |
    Set-Content $OutputPath -Encoding utf8
Write-Output $OutputPath
