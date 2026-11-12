<#
.SYNOPSIS  Builds, tests and packages VESPER for Windows 11 x64.
.EXAMPLE   .\scripts\Build-Release.ps1 -Version 1.0.0
.EXAMPLE   .\scripts\Build-Release.ps1 -Version 1.0.0 -PortableOnly
.NOTES     Output: release\setup-<version>\VESPER-Setup.exe and release\VESPER-<version>.zip.
           The executables are NOT code-signed. Windows SmartScreen may warn on first run.
           The setup is self-contained. The ZIP is a portable app payload; use -SelfContained to bundle
           the .NET runtime in that payload as well.
#>
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = "1.0.0",
    [switch]$SkipTests,
    [switch]$SelfContained,
    [switch]$PortableOnly,
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
    Run "Desktop persistence tests" { dotnet test tests/Vesper.Windows.Tests -c Release --nologo }
    if (Get-Command node -ErrorAction SilentlyContinue) {
        $extensionTests = @(Get-ChildItem (Join-Path $root 'tests/extension') -Filter '*.test.mjs' -File | ForEach-Object FullName)
        if ($extensionTests.Count -eq 0) { throw 'No extension regression tests were found.' }
        Run "Extension logic tests" { node --test @extensionTests }
    } else { Write-Warning "Node.js not found: extension logic tests were skipped." }
}

$out = Join-Path $root "release\VESPER-$Version"
if ((Test-Path $out) -or (Test-Path "$out.zip")) { throw "Release output already exists: $out. Archive it or choose another version; nothing was overwritten." }
$setupOut = Join-Path $root "release\setup-$Version"
if (-not $PortableOnly -and (Test-Path -LiteralPath $setupOut)) { throw "Setup output already exists: $setupOut." }
$sc = if ($SelfContained -or -not $PortableOnly) { "--self-contained" } else { "--no-self-contained" }
$runtimeArgs = @()
if ($RuntimeVersion) { $runtimeArgs += "-p:RuntimeFrameworkVersion=$RuntimeVersion" }

Run "Publish desktop app" { dotnet publish VESPER.csproj -c Release -r win-x64 $sc -p:Version=$Version -p:DebugType=none @runtimeArgs -o $out }
Run "Publish native-messaging host" { dotnet publish src/Vesper.NativeHost/Vesper.NativeHost.csproj -c Release -r win-x64 $sc -p:Version=$Version -p:DebugType=none @runtimeArgs -o $out }

Write-Host "==> Extension" -ForegroundColor Green
Copy-Item (Join-Path $root "extension") (Join-Path $out "extension") -Recurse -Force
if (-not $PortableOnly) {
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
Copy-Item (Join-Path $root "DOCUMENTATION\RELEASE.md") (Join-Path $out "RELEASE.md")

$zip = "$out.zip"
if (Test-Path $zip) { throw "Release ZIP already exists: $zip. Nothing was overwritten." }
Get-ChildItem $out -Recurse -File | Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash)  $($_.Path.Substring($out.Length + 1))" } | Set-Content (Join-Path $out "SHA256SUMS.txt")
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Done: $zip" -ForegroundColor Green
if (-not $PortableOnly) { Write-Host "First Flight setup is unsigned: $(Join-Path $setupOut 'VESPER-Setup.exe')" }
else { Write-Host "Portable ZIP only. It does not register VESPER's browser helper." }
