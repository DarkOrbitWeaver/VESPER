<#
.SYNOPSIS  Runs every automated test: .NET engine/bridge/storage tests and the extension logic tests.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Install Node.js to run the extension tests.' }
$testFiles = @(Get-ChildItem (Join-Path $root 'tests/extension') -Filter '*.test.mjs' -File | ForEach-Object FullName)
if ($testFiles.Count -eq 0) { throw 'No extension regression tests were found.' }
dotnet test tests/Vesper.Tests/Vesper.Tests.csproj -c Release --nologo --logger "trx;LogFileName=vesper-tests.trx" --results-directory release\test-results
$dotnet = $LASTEXITCODE
dotnet test tests/Vesper.MediaResolver.Tests/Vesper.MediaResolver.Tests.csproj -c Release --nologo --logger "trx;LogFileName=vesper-media-tests.trx" --results-directory release\test-results
$mediaDotnet = $LASTEXITCODE
dotnet test tests/Vesper.Intelligence.Tests/Vesper.Intelligence.Tests.csproj -c Release --nologo --logger "trx;LogFileName=vesper-intelligence-tests.trx" --results-directory release\test-results
$intelligence = $LASTEXITCODE
dotnet test tests/Vesper.Setup.Tests/Vesper.Setup.Tests.csproj -c Release --nologo --logger "trx;LogFileName=vesper-setup-tests.trx" --results-directory release\test-results
$setupDotnet = $LASTEXITCODE
dotnet test tests/Vesper.Windows.Tests/Vesper.Windows.Tests.csproj -c Release --nologo --logger "trx;LogFileName=vesper-windows-tests.trx" --results-directory release\test-results
$windowsDotnet = $LASTEXITCODE
node --test --test-reporter=dot @testFiles
$node = $LASTEXITCODE
if ($dotnet -ne 0 -or $mediaDotnet -ne 0 -or $setupDotnet -ne 0 -or $windowsDotnet -ne 0 -or $intelligence -ne 0 -or $node -ne 0) {
    Write-Host "TESTS FAILED" -ForegroundColor Red
    exit 1
}
Write-Host "All tests passed." -ForegroundColor Green
