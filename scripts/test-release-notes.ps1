$ErrorActionPreference = 'Stop'
# Validate the release-notes extraction exactly as release.yml does it, for every released version.
$changelog = Get-Content "$PSScriptRoot\..\CHANGELOG.md" -Raw
$released = [regex]::Matches($changelog, '(?m)^## \[(\d+\.\d+\.\d+)\]') |
    ForEach-Object { $_.Groups[1].Value } |
    Select-Object -Unique
if (-not $released) { throw 'No released versions found in CHANGELOG.md.' }
foreach ($version in $released) {
    $pattern = "(?ms)^## \[$version\] - .*?(?=^## \[|\z)"
    $section = [regex]::Match($changelog, $pattern).Value
    if ([string]::IsNullOrWhiteSpace($section)) {
        throw "CHANGELOG section extraction failed for [$version]."
    }
    Write-Host "[$version] section OK ($($section.Length) chars)"
}
