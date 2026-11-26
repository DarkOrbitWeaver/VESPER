<#
.SYNOPSIS  Builds, tests and packages the VESPER Windows 11 x64 installer.
.EXAMPLE   .\scripts\Build-Release.ps1 -AllowUnsigned
.NOTES     Output: release\setup-<version>\VESPER-Setup.exe.
           Signing requires VESPER_SIGN_CERT_SHA1 (Windows certificate store). Use -AllowUnsigned only for local development.
           Release packages require a clean Git tree. Use -AllowDirtyWorkingTreeForDevelopment only for local testing;
           those binaries are stamped `local-dirty`, never with the current commit ID.
#>
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [switch]$SkipTests,
    [string]$SigningCertificateThumbprint = $env:VESPER_SIGN_CERT_SHA1,
    [uri]$TimestampServer = 'https://timestamp.digicert.com',
    [switch]$AllowUnsigned,
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
    $Version = [string]$props.Project.PropertyGroup.Version.InnerText
}
$sourceManifest = Get-Content -LiteralPath (Join-Path $root 'extension/manifest.json') -Raw | ConvertFrom-Json
if (-not $PSBoundParameters.ContainsKey('Version') -and
    ($sourceManifest.version_name -ne $Version -or $sourceManifest.version -ne ($Version -split '-')[0])) {
    throw 'Desktop and source extension versions disagree. Update both before packaging.'
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
    Run "Release tests" { & (Join-Path $PSScriptRoot 'Run-Tests.ps1') }
}

$setupOut = Join-Path $root "release\setup-$Version"
if (Test-Path -LiteralPath $setupOut) { throw "Setup output already exists: $setupOut. Archive it or choose another version; nothing was overwritten." }

$buildArgs = @("-p:Version=$Version", "-p:DebugType=none", "-p:SourceRevisionId=$sourceRevision")
if ($RuntimeVersion) { $buildArgs += "-p:RuntimeFrameworkVersion=$RuntimeVersion" }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Join-Path $root ("release\.setup-" + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $package "stage"
$payloadPath = Join-Path $package "payload.zip"
$manifestPath = Join-Path $package "payload.json"
[IO.Directory]::CreateDirectory($stage) | Out-Null
$payload = $null

try {
    Run "Publish desktop app" { dotnet publish VESPER.csproj -c Release -r win-x64 --self-contained @buildArgs -o $stage }
    Run "Publish native-messaging host" { dotnet publish src/Vesper.NativeHost/Vesper.NativeHost.csproj -c Release -r win-x64 --self-contained @buildArgs -o $stage }

    Sign-ReleaseFile (Join-Path $stage 'VESPER.exe')
    Sign-ReleaseFile (Join-Path $stage 'vesper-host.exe')

    Write-Host "==> Extension" -ForegroundColor Green
    Copy-Item (Join-Path $root "extension") (Join-Path $stage "extension") -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'extension/LICENSE')
    $manifestFile = Join-Path $stage 'extension/manifest.json'
    $extensionManifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    $extensionManifest.version = ($Version -split '-')[0]
    $extensionManifest | Add-Member -NotePropertyName version_name -NotePropertyValue $Version -Force
    [IO.File]::WriteAllText($manifestFile, ($extensionManifest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))

    Write-Host "==> Build First Flight setup" -ForegroundColor Green
    $payload = [IO.Compression.ZipFile]::Open($payloadPath, [IO.Compression.ZipArchiveMode]::Create)
    $files = @(
        foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unexpected link in the payload." }
            $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($payload, $file.FullName, $name, 'Optimal') | Out-Null
            @{ Path = $name; Size = $file.Length; Sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash }
        }
    )
    $payload.Dispose(); $payload = $null
    [IO.File]::WriteAllText($manifestPath, (@{ Version = $Version; Files = $files } | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))

    Run "Publish First Flight" {
        dotnet publish src/Vesper.Setup/Vesper.Setup.csproj -c Release -r win-x64 --self-contained -o $setupOut `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true "-p:SetupPayload=$payloadPath" "-p:SetupManifest=$manifestPath" @buildArgs
    }

    $setupExe = Join-Path $setupOut 'VESPER-Setup.exe'
    Run 'Verify embedded setup payload' {
        $verification = Start-Process -FilePath $setupExe -ArgumentList '--verify-package' -Wait -PassThru
        $global:LASTEXITCODE = $verification.ExitCode
    }
    Sign-ReleaseFile $setupExe
    "$((Get-FileHash -LiteralPath $setupExe).Hash)  VESPER-Setup.exe" | Set-Content -LiteralPath (Join-Path $setupOut 'SHA256SUMS.txt')
    Write-Host "Setup: $setupExe" -ForegroundColor Green
}
finally {
    if ($payload) { $payload.Dispose() }
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Recurse -Force }
}
