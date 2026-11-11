<#
.SYNOPSIS  Per-user install of VESPER. No administrator rights, nothing machine-wide.
.DESCRIPTION
  Copies the release folder to %LOCALAPPDATA%\Programs\VESPER, creates a Start Menu shortcut and registers the
  native-messaging host for the current user in Chrome, Edge and Brave. An existing installation is kept as
  VESPER.previous so an upgrade can be rolled back by swapping the folders back. Downloads, settings and the
  database in %LOCALAPPDATA%\VESPER are never touched by installing or upgrading.
#>
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source "VESPER.exe"))) { throw "Run this script from the unpacked release folder (VESPER.exe not found)." }
if (-not (Test-Path (Join-Path $source "vesper-host.exe"))) { throw "vesper-host.exe is missing from the release folder." }

$target   = Join-Path $env:LOCALAPPDATA "Programs\VESPER"
$previous = "$target.previous"
$data     = Join-Path $env:LOCALAPPDATA "VESPER"
$hostName = "com.vesper.downloader"
$extensionId = "aidnpdcnkhgojimnkcgieebdjdicfemi"   # derived from the key pinned in extension\manifest.json

if (Get-Process -Name VESPER -ErrorAction SilentlyContinue) {
    throw "VESPER is running. Exit it from the tray icon first, so running downloads are paused safely."
}

if (Test-Path $target) {
    if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
    Move-Item $target $previous           # rollback copy
    Write-Host "Previous version kept at $previous"
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem $source -Exclude "Install.ps1", "Uninstall.ps1" | Copy-Item -Destination $target -Recurse -Force

# Native messaging host manifest + per-user registry keys.
$manifestDir = Join-Path $data "native-host"
New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
$manifestPath = Join-Path $manifestDir "$hostName.json"
@{
    name = $hostName; description = "VESPER download manager"
    path = (Join-Path $target "vesper-host.exe"); type = "stdio"
    allowed_origins = @("chrome-extension://$extensionId/")
} | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding UTF8
foreach ($key in @("Software\Google\Chrome", "Software\Microsoft\Edge", "Software\BraveSoftware\Brave-Browser")) {
    $path = "HKCU:\$key\NativeMessagingHosts\$hostName"
    New-Item -Path $path -Force | Out-Null
    Set-ItemProperty -Path $path -Name "(default)" -Value $manifestPath
}

# Start Menu shortcut.
$shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "VESPER.lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $target "VESPER.exe"
$link.WorkingDirectory = $target
$link.IconLocation = (Join-Path $target "VESPER.exe") + ",0"
$link.Save()

Write-Host "VESPER installed to $target" -ForegroundColor Green
Write-Host "Chrome extension: open chrome://extensions, enable Developer mode, choose 'Load unpacked' and select:"
Write-Host "  $(Join-Path $target 'extension')"
Write-Host "Roll back with: Move the folders back ($previous -> $target). Your downloads and settings are unaffected."
