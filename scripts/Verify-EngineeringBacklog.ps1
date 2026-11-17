# Verify that every unfinished audit/backlog ID has one corresponding checkbox.
# Windows PowerShell 5.1+; no external dependencies; documentation-only validation.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$registryPath = Join-Path $root 'DOCUMENTATION\STATUS.tsv'
$todoPath = Join-Path $root 'DOCUMENTATION\MASTER-TODO.md'
$rows = @(Import-Csv -LiteralPath $registryPath -Delimiter "`t")
$todo = [System.IO.File]::ReadAllText($todoPath)
if ($rows.Count -eq 0) { throw 'Backlog registry is empty.' }

$seen = @{}
foreach ($row in $rows) {
    if ([string]::IsNullOrWhiteSpace($row.ID)) { throw 'Found a blank backlog ID.' }
    if ($seen.ContainsKey($row.ID)) { throw "Duplicate registry ID: $($row.ID)" }
    $seen[$row.ID] = $true
}

$matches = [regex]::Matches($todo, '(?m)^\s*-\s*\[(?<mark>[ xX])\]\s*\*\*(?<id>(?:VES-\d{3}|VSP-\d{3}|USER-[A-Z0-9-]+|N-\d{2}))\b')
$checks = @{}
foreach ($match in $matches) {
    $id = $match.Groups['id'].Value
    $mark = $match.Groups['mark'].Value
    if ($checks.ContainsKey($id)) { throw "Duplicate master TODO checkbox: $id" }
    if (-not $seen.ContainsKey($id)) { throw "Master TODO references an unknown ID: $id" }
    $checks[$id] = $mark
}

$openCount = 0
foreach ($row in $rows) {
    $closed = $row.Status -match '^(Implemented|Preserved)'
    if ($closed) {
        if ($checks.ContainsKey($row.ID) -and $checks[$row.ID] -eq ' ') {
            throw "Marked closed in TSV but unchecked in TODO: $($row.ID)"
        }
    }
    else {
        $openCount++
        if (-not $checks.ContainsKey($row.ID)) { throw "Unfinished ID missing from master TODO: $($row.ID)" }
        if ($checks[$row.ID] -ne ' ') { throw "Unfinished ID prematurely checked: $($row.ID)" }
    }
}

foreach ($number in 1..9) {
    $id = 'VSP-{0:000}' -f $number
    if (-not $seen.ContainsKey($id)) { throw "Independent Claude audit is missing $id" }
}
Write-Host "VESPER backlog OK: $($rows.Count) unique registered IDs, $openCount unfinished and represented in MASTER-TODO.md."
