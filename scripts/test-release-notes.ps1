$ErrorActionPreference = 'Stop'
# Validate the curated release-notes extraction for every released version by
# running the exact script the Release workflow runs — a syntax error or
# extraction failure in make-release-notes.ps1 fails here first, in CI.
$changelog = Get-Content "$PSScriptRoot\..\CHANGELOG.md" -Raw
$released = [regex]::Matches($changelog, '(?m)^## \[(\d+\.\d+\.\d+)\]') |
    ForEach-Object { $_.Groups[1].Value } |
    Select-Object -Unique
if (-not $released) { throw 'No released versions found in CHANGELOG.md.' }
foreach ($version in $released) {
    $expected = Join-Path ([IO.Path]::GetTempPath()) "release-notes-$version-test.md"
    $notesPath = & "$PSScriptRoot\make-release-notes.ps1" -Version $version -OutputPath $expected
    if ($notesPath -ne $expected) { throw "make-release-notes.ps1 returned an unexpected path for [$version]." }
    $notes = Get-Content $notesPath -Raw
    if ([string]::IsNullOrWhiteSpace($notes)) { throw "Release notes for [$version] came back empty." }
    if (-not $notes.Contains("## [$version]")) { throw "Release notes for [$version] do not contain its section header." }
    Remove-Item $expected
    Write-Host "[$version] notes OK ($($notes.Length) chars)"
}
