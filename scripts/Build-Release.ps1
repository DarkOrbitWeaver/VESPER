<#
.SYNOPSIS  Builds, tests and packages VESPER for Windows 11 x64.
.EXAMPLE   .\scripts\Build-Release.ps1 -Version 1.0.0
.NOTES     Output: release\VESPER-<version>\ and release\VESPER-<version>.zip (+ SHA256SUMS.txt).
           The executables are NOT code-signed. Windows SmartScreen may warn on first run.
           Default is framework-dependent (needs the .NET 10 Desktop Runtime, small download).
           Use -SelfContained to bundle the runtime instead (much larger, no prerequisite).
#>
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = "1.0.0",
    [switch]$SkipTests,
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Run($label, [scriptblock]$block) {
    Write-Host "==> $label" -ForegroundColor Green
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$label failed (exit code $LASTEXITCODE)" }
}

Run "Restore" { dotnet restore VESPER.slnx }
if (-not $SkipTests) {
    Run "Engine, bridge and storage tests" { dotnet test tests/Vesper.Tests -c Release --nologo }
    Run "Media resolver tests" { dotnet test tests/Vesper.MediaResolver.Tests -c Release --nologo }
    if (Get-Command node -ErrorAction SilentlyContinue) {
        $extensionTests = @(Get-ChildItem (Join-Path $root 'tests/extension') -Filter '*.test.mjs' -File | ForEach-Object FullName)
        if ($extensionTests.Count -eq 0) { throw 'No extension regression tests were found.' }
        Run "Extension logic tests" { node --test @extensionTests }
    } else { Write-Warning "Node.js not found: extension logic tests were skipped." }
}

$out = Join-Path $root "release\VESPER-$Version"
if ((Test-Path $out) -or (Test-Path "$out.zip")) { throw "Release output already exists: $out. Archive it or choose another version; nothing was overwritten." }
$sc = if ($SelfContained) { "--self-contained" } else { "--no-self-contained" }

Run "Publish desktop app" { dotnet publish VESPER.csproj -c Release -r win-x64 $sc -p:Version=$Version -p:DebugType=none -o $out }
Run "Publish native-messaging host" { dotnet publish src/Vesper.NativeHost/Vesper.NativeHost.csproj -c Release -r win-x64 $sc -p:Version=$Version -p:DebugType=none -o $out }

Write-Host "==> Extension and scripts" -ForegroundColor Green
Copy-Item (Join-Path $root "extension") (Join-Path $out "extension") -Recurse -Force
Copy-Item (Join-Path $root "scripts\Install.ps1"), (Join-Path $root "scripts\Uninstall.ps1") $out
Copy-Item (Join-Path $root "DOCUMENTATION\RELEASE.md") (Join-Path $out "RELEASE.md")

$zip = "$out.zip"
if (Test-Path $zip) { throw "Release ZIP already exists: $zip. Nothing was overwritten." }
Get-ChildItem $out -Recurse -File | Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash)  $($_.Path.Substring($out.Length + 1))" } | Set-Content (Join-Path $out "SHA256SUMS.txt")
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Done: $zip" -ForegroundColor Green
Write-Host "Not code-signed. Run Install.ps1 from the unpacked folder for a per-user install (no administrator rights)."
