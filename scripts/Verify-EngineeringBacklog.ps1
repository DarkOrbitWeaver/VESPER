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

$matches = [regex]::Matches($todo, '(?m)^\s*-\s*\[(?<mark>[ xX])\]\s*\*\*(?<id>(?:VES-\d{3}|VSP-\d{3}|USER-[A-Z0-9-]+|N-\d{2}|AUDIT-EXTRA-\d{3}))\b')
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
$auditPath = Join-Path $root 'DOCUMENTATION\AUDIT-RECONCILIATION.md'
$gatesPath = Join-Path $root 'DOCUMENTATION\QUALITY-RELEASE-GATES.md'
$handoffPath = Join-Path $root 'DOCUMENTATION\NEXT-CHAT-HANDOFF.md'
foreach ($required in @($auditPath, $gatesPath, $handoffPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Mandatory handoff / audit document is missing: $required"
    }
}
$audit = [System.IO.File]::ReadAllText($auditPath)
$quality = [System.IO.File]::ReadAllText($gatesPath)
if ($todo -notmatch 'AUDIT-RECONCILIATION\.md' -or $todo -notmatch 'QUALITY-RELEASE-GATES\.md') {
    throw 'Master TODO no longer references mandatory audit reconciliation or quality gates.'
}
foreach ($number in 0..10) {
    $gate = 'Q' + $number
    if ($quality -notmatch ('(?m)^\| ' + $gate + '\s')) {
        throw "AAA-quality gate $gate is missing from QUALITY-RELEASE-GATES.md."
    }
}
if ($audit -notmatch 'Original-audit reconciliation' -and $audit -notmatch 'Original-audit reconciliation ledger') {
    throw 'Audit reconciliation ledger does not contain its required scope heading.'
}

# The portable ZIP must not ship an older version's test counts and readiness claims.
$releasePath = Join-Path $root 'DOCUMENTATION\RELEASE.md'
$buildScriptPath = Join-Path $root 'scripts\Build-Release.ps1'
foreach ($required in @($releasePath, $buildScriptPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Release documentation or packaging script is missing: $required"
    }
}
$releaseNotes = [IO.File]::ReadAllText($releasePath)
$buildScript = [IO.File]::ReadAllText($buildScriptPath)
if ($releaseNotes -match '(?i)verification run on 2026-11-12|^# VESPER 1\.0\.0 release status') {
    throw 'Current packaged release notes contain the obsolete 1.0.0 verification report.'
}
if ($quality -match 'Current release status: NOT APPROVED' -and
    $releaseNotes -notmatch '(?i)not approved for public release') {
    throw 'Packaged release notes claim more readiness than the quality gates support.'
}
if ($buildScript -notmatch 'Copy-Item[^\r\n]+DOCUMENTATION\\RELEASE\.md') {
    throw 'Packaging no longer copies the current release information.'
}
Write-Host "VESPER backlog OK: $($rows.Count) unique IDs, $openCount unfinished; audit ledger, Q0-Q10 and release-documentation checks present. Actual completion requires independent evidence."
