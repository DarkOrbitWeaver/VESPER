<#
.SYNOPSIS  Runs every automated test: .NET engine/bridge/storage tests and the extension logic tests.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
dotnet test tests/Vesper.Tests -c Release --nologo --logger "trx;LogFileName=vesper-tests.trx" --results-directory release\test-results
$dotnet = $LASTEXITCODE
dotnet test tests/Vesper.MediaResolver.Tests -c Release --nologo --logger "trx;LogFileName=vesper-media-tests.trx" --results-directory release\test-results
$mediaDotnet = $LASTEXITCODE
if (Get-Command node -ErrorAction SilentlyContinue) {
    $testFiles = @(Get-ChildItem (Join-Path $root 'tests/extension') -Filter '*.test.mjs' -File | ForEach-Object FullName)
    if ($testFiles.Count -gt 0) { node --test @testFiles; $node = $LASTEXITCODE }
    else { Write-Warning 'No extension tests found.'; $node = 0 }
} else { Write-Warning 'Node.js not found; extension tests skipped.'; $node = 0 }
if ($dotnet -ne 0 -or $mediaDotnet -ne 0 -or $node -ne 0) { Write-Host "TESTS FAILED" -ForegroundColor Red; exit 1 }
Write-Host "All tests passed." -ForegroundColor Green
