<#
.SYNOPSIS  Removes the per-user VESPER installation.
.DESCRIPTION
  Removes the program folder, the Start Menu shortcut and the native-messaging registration.
  Your downloads, partial files, settings and download history (%LOCALAPPDATA%\VESPER) are KEPT unless you
  pass -RemoveData and confirm. Downloaded files in your download folders are never deleted by this script.
#>
param([switch]$RemoveData)
$ErrorActionPreference = 'Stop'

if (Get-Process -Name VESPER -ErrorAction SilentlyContinue) {
    throw "VESPER is running. Exit it from the tray icon first, so running downloads are paused safely."
}

$target = Join-Path $env:LOCALAPPDATA "Programs\VESPER"
$data   = Join-Path $env:LOCALAPPDATA "VESPER"
$hostName = "com.vesper.downloader"

foreach ($key in @("Software\Google\Chrome", "Software\Microsoft\Edge", "Software\BraveSoftware\Brave-Browser")) {
    Remove-Item -Path "HKCU:\$key\NativeMessagingHosts\$hostName" -Recurse -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path ([Environment]::GetFolderPath("Programs")) "VESPER.lnk") -ErrorAction SilentlyContinue
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Remove-Item "$target.previous" -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    $answer = Read-Host "Delete VESPER's history, settings and recovery data in $data ? Partial downloads' recovery info is lost. Type DELETE to confirm"
    if ($answer -ceq "DELETE") { Remove-Item $data -Recurse -Force; Write-Host "Data removed." }
    else { Write-Host "Data kept." }
} else {
    Write-Host "Your data in $data was kept. Re-run with -RemoveData to delete it."
}
Write-Host "VESPER was uninstalled. Downloaded files were not touched." -ForegroundColor Green
