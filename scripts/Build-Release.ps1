<#
.SYNOPSIS  Builds, tests and packages VESPER for Windows 11 x64.
.EXAMPLE   .\scripts\Build-Release.ps1 -AllowUnsigned  # isolated development only
.EXAMPLE   .\scripts\Build-Release.ps1 -PortableOnly -AllowUnsigned
.NOTES     Output: release\setup-<version>\VESPER-Setup.exe and release\VESPER-<version>.zip.
           Signing requires VESPER_SIGN_CERT_SHA1 (Windows certificate store). Use -AllowUnsigned only for development.
           Release packages require a clean Git tree. Use -AllowDirtyWorkingTreeForDevelopment only for local tests;
           those binaries are stamped `local-dirty`, never with the current commit ID.
           The setup is self-contained. The ZIP is a portable app payload; use -SelfContained to bundle
           the .NET runtime in that payload as well.
#>
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = "1.0.2",
    [switch]$SkipTests,
    [string]$SigningCertificateThumbprint = $env:VESPER_SIGN_CERT_SHA1,
    [uri]$TimestampServer = 'https://timestamp.digicert.com',
    [switch]$AllowUnsigned,
    [switch]$SelfContained,
    [switch]$PortableOnly,
    [switch]$AllowDirtyWorkingTreeForDevelopment,
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

function Resolve-SourceRevision([string]$revision, [string[]]$status, [bool]$allowDirty) {
    if ($revision -notmatch '^[0-9a-f]{40}$') { throw 'Release packaging requires a valid Git commit.' }
    if ($status.Count -gt 0) {
        if (-not $allowDirty) { throw 'Release packaging requires a clean Git tree. Commit the candidate or use -AllowDirtyWorkingTreeForDevelopment for local testing.' }
        Write-Warning 'Packaging a dirty working tree for development; binaries will be stamped local-dirty.'
        return 'local-dirty'
    }
    return $revision
}

function Get-SourceRevision([bool]$allowDirty) {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Release packaging requires Git to identify the source revision.' }
    $revision = (& git -C $root rev-parse --verify HEAD 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Git could not identify the current source revision.' }
    $status = @(& git -C $root status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Git could not verify the working tree state.' }
    return Resolve-SourceRevision $revision $status $allowDirty
}

$sourceRevision = Get-SourceRevision ([bool]$AllowDirtyWorkingTreeForDevelopment)

if (-not $PSBoundParameters.ContainsKey('Version')) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $Version = [string]$props.Project.PropertyGroup.Version
}
if (-not $SigningCertificateThumbprint -and -not $AllowUnsigned) {
    throw 'A release needs an Authenticode certificate. Set VESPER_SIGN_CERT_SHA1, or explicitly use -AllowUnsigned for local development.'
}
if ($TimestampServer.Scheme -ne 'https') { throw 'The signing timestamp service must use HTTPS.' }
function Sign-ReleaseFile([string]$path) {
    if (-not $SigningCertificateThumbprint) { Write-Warning "Unsigned development binary: $path"; return }
    $tool = Get-Command signtool.exe -ErrorAction Stop
    Run "Sign $path" { & $tool.Source sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr $TimestampServer.AbsoluteUri /td SHA256 $path }
    Run "Verify signature $path" { & $tool.Source verify /pa $path }
}

if (-not $SkipTests) {
    Run "Engine, bridge and storage tests" { dotnet test tests/Vesper.Tests/Vesper.Tests.csproj -c Release --nologo }
    Run "Media resolver tests" { dotnet test tests/Vesper.MediaResolver.Tests/Vesper.MediaResolver.Tests.csproj -c Release --nologo }
    Run "Setup and recovery tests" { dotnet test tests/Vesper.Setup.Tests/Vesper.Setup.Tests.csproj -c Release --nologo }
    Run "Desktop persistence tests" { dotnet test tests/Vesper.Windows.Tests/Vesper.Windows.Tests.csproj -c Release --nologo }
    if (Get-Command node -ErrorAction SilentlyContinue) {
        $extensionTests = @(Get-ChildItem (Join-Path $root 'tests/extension') -Filter '*.test.mjs' -File | ForEach-Object FullName)
        if ($extensionTests.Count -eq 0) { throw 'No extension regression tests were found.' }
        Run "Extension logic tests" { node --test @extensionTests }
    } else { Write-Warning "Node.js not found: extension logic tests were skipped." }
}

$out = Join-Path $root "release\VESPER-$Version"
$zip = "$out.zip"
$zipChecksumPath = "$zip.sha256"
if ((Test-Path $out) -or (Test-Path $zip) -or (Test-Path $zipChecksumPath)) { throw "Release output already exists: $out. Archive it or choose another version; nothing was overwritten." }
$setupOut = Join-Path $root "release\setup-$Version"
if (-not $PortableOnly -and (Test-Path -LiteralPath $setupOut)) { throw "Setup output already exists: $setupOut." }
$sc = if ($SelfContained -or -not $PortableOnly) { "--self-contained" } else { "--no-self-contained" }
$buildArgs = @("-p:Version=$Version", "-p:DebugType=none")
$buildArgs += "-p:SourceRevisionId=$sourceRevision"
if ($RuntimeVersion) { $buildArgs += "-p:RuntimeFrameworkVersion=$RuntimeVersion" }

Run "Publish desktop app" { dotnet publish VESPER.csproj -c Release -r win-x64 $sc @buildArgs -o $out }
Run "Publish native-messaging host" { dotnet publish src/Vesper.NativeHost/Vesper.NativeHost.csproj -c Release -r win-x64 $sc @buildArgs -o $out }

Sign-ReleaseFile (Join-Path $out 'VESPER.exe')
Sign-ReleaseFile (Join-Path $out 'vesper-host.exe')

Write-Host "==> Extension" -ForegroundColor Green
Copy-Item (Join-Path $root "extension") (Join-Path $out "extension") -Recurse -Force
$manifestFile = Join-Path $out 'extension/manifest.json'
$extensionManifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
$extensionManifest.version = ($Version -split '-')[0]
$extensionManifest | Add-Member -NotePropertyName version_name -NotePropertyValue $Version -Force
$extensionManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestFile -Encoding utf8

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
                -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
                -p:EnableCompressionInSingleFile=true "-p:SetupPayload=$payloadPath" "-p:SetupManifest=$manifestPath" @buildArgs
        }
        $setupExe = Join-Path $setupOut 'VESPER-Setup.exe'
        Sign-ReleaseFile $setupExe
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

Get-ChildItem $out -Recurse -File | Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash)  $($_.Path.Substring($out.Length + 1))" } | Set-Content (Join-Path $out "SHA256SUMS.txt")
Compress-Archive -Path "$out\*" -DestinationPath $zip
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $(Split-Path -Leaf $zip)" | Set-Content -LiteralPath $zipChecksumPath -Encoding ascii
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $zipHash) {
    throw "The portable ZIP checksum did not verify: $zip"
}
Write-Host "Done: $zip" -ForegroundColor Green
Write-Host "ZIP checksum: $zipChecksumPath"
if (-not $PortableOnly) { Write-Host "First Flight: $(Join-Path $setupOut 'VESPER-Setup.exe')" }
else { Write-Host "Portable ZIP only. It does not register VESPER's browser helper." }
