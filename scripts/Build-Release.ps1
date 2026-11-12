<#
.SYNOPSIS  Builds, tests and packages VESPER for Windows 11 x64.
.EXAMPLE   .\scripts\Build-Release.ps1 -Version 1.0.0
.EXAMPLE   .\scripts\Build-Release.ps1 -Version 1.0.0 -Setup
.NOTES     Output: release\VESPER-<version>\ and release\VESPER-<version>.zip (+ SHA256SUMS.txt).
           The executables are NOT code-signed. Windows SmartScreen may warn on first run.
           Default is framework-dependent (needs the .NET 10 Desktop Runtime, small download).
           Use -SelfContained to bundle the runtime instead (much larger, no prerequisite).
#>
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = "1.0.0",
    [switch]$SkipTests,
    [switch]$SelfContained,
    [switch]$Setup,
    [ValidatePattern('^10\.0\.[0-9]+$')][string]$RuntimeVersion
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Run($label, [scriptblock]$block) {
    Write-Host "==> $label" -ForegroundColor Green
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$label failed (exit code $LASTEXITCODE)" }
}

if (-not $SkipTests) {
    Run "Engine, bridge and storage tests" { dotnet test tests/Vesper.Tests -c Release --nologo }
    Run "Media resolver tests" { dotnet test tests/Vesper.MediaResolver.Tests -c Release --nologo }
    Run "Setup and recovery tests" { dotnet test tests/Vesper.Setup.Tests -c Release --nologo }
    if (Get-Command node -ErrorAction SilentlyContinue) {
        $extensionTests = @(Get-ChildItem (Join-Path $root 'tests/extension') -Filter '*.test.mjs' -File | ForEach-Object FullName)
        if ($extensionTests.Count -eq 0) { throw 'No extension regression tests were found.' }
        Run "Extension logic tests" { node --test @extensionTests }
    } else { Write-Warning "Node.js not found: extension logic tests were skipped." }
}

$out = Join-Path $root "release\VESPER-$Version"
if ((Test-Path $out) -or (Test-Path "$out.zip")) { throw "Release output already exists: $out. Archive it or choose another version; nothing was overwritten." }
$setupOut = Join-Path $root "release\setup-$Version"
if ($Setup -and (Test-Path -LiteralPath $setupOut)) { throw "Setup output already exists: $setupOut." }
$sc = if ($SelfContained -or $Setup) { "--self-contained" } else { "--no-self-contained" }
$runtimeArgs = @()
if ($RuntimeVersion) { $runtimeArgs += "-p:RuntimeFrameworkVersion=$RuntimeVersion" }

Run "Publish desktop app" { dotnet publish VESPER.csproj -c Release -r win-x64 $sc -p:Version=$Version -p:DebugType=none @runtimeArgs -o $out }
Run "Publish native-messaging host" { dotnet publish src/Vesper.NativeHost/Vesper.NativeHost.csproj -c Release -r win-x64 $sc -p:Version=$Version -p:DebugType=none @runtimeArgs -o $out }

Write-Host "==> Extension and scripts" -ForegroundColor Green
Copy-Item (Join-Path $root "extension") (Join-Path $out "extension") -Recurse -Force
if ($Setup) {
    Write-Host "==> Build offline First Flight setup"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = Join-Path $root ("release\.setup-" + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($package) | Out-Null
    $payloadPath = Join-Path $package "payload.zip"
    $manifestPath = Join-Path $package "payload.json"
    $payload = $null
    try {
        $payload = [IO.Compression.ZipFile]::Open($payloadPath, [IO.Compression.ZipArchiveMode]::Create)
        $files = @(
            foreach ($file in Get-ChildItem -LiteralPath $out -Recurse -File | Sort-Object FullName) {
                if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unexpected link in the payload." }
                $name = $file.FullName.Substring($out.Length + 1).Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($payload, $file.FullName, $name, 'Optimal') | Out-Null
                @{ Path = $name; Size = $file.Length; Sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash }
            }
        )
        $payload.Dispose(); $payload = $null
        @{ Version = $Version; Files = $files } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        Run "Publish First Flight" {
            dotnet publish src/Vesper.Setup/Vesper.Setup.csproj -c Release -r win-x64 --self-contained -o $setupOut `
                -p:Version=$Version -p:DebugType=none -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
                -p:EnableCompressionInSingleFile=true "-p:SetupPayload=$payloadPath" "-p:SetupManifest=$manifestPath" @runtimeArgs
        }
        $setupExe = Join-Path $setupOut 'VESPER-Setup.exe'
        "$((Get-FileHash -LiteralPath $setupExe).Hash)  VESPER-Setup.exe" | Set-Content -LiteralPath (Join-Path $setupOut 'SHA256SUMS.txt')
        Write-Host "Setup: $setupExe"
    }
    finally {
        if ($payload) { $payload.Dispose() }
        foreach ($file in @($payloadPath, $manifestPath)) {
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
        }
        Remove-Item -LiteralPath $package
    }
}
Copy-Item (Join-Path $root "scripts\Install.ps1"), (Join-Path $root "scripts\Uninstall.ps1") $out
Copy-Item (Join-Path $root "DOCUMENTATION\RELEASE.md") (Join-Path $out "RELEASE.md")

$zip = "$out.zip"
if (Test-Path $zip) { throw "Release ZIP already exists: $zip. Nothing was overwritten." }
Get-ChildItem $out -Recurse -File | Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash)  $($_.Path.Substring($out.Length + 1))" } | Set-Content (Join-Path $out "SHA256SUMS.txt")
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Done: $zip" -ForegroundColor Green
if ($Setup) { Write-Host "The offline setup is unsigned." }
else { Write-Host "Not code-signed. Run Install.ps1 from the unpacked folder for a per-user install (no administrator rights)." }
