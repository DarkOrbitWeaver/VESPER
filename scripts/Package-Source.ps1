<#
.SYNOPSIS Creates a source-only ZIP without overwriting an existing artifact.
.NOTES No .NET runtime, FFmpeg, browser runtime, user data, Git database or build output is bundled.
#>
param([string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'release\VESPER-media-engine-source.zip'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) { throw "Source ZIP already exists: $target. Nothing was overwritten." }
$parent = Split-Path -Parent $target
[IO.Directory]::CreateDirectory($parent) | Out-Null
$temp = Join-Path $parent ('.vesper-source-' + [guid]::NewGuid().ToString('N') + '.tmp')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = $null
try {
    $archive = [IO.Compression.ZipFile]::Open($temp, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(\.git|\.vs|bin|obj|node_modules|release|TestResults)(/|$)') { continue }
        if ($relative -match '(^|/)\.env($|\.)|\.(user|suo|db|sqlite|vpart|vmeta|zip)$') { continue }
        if ($file.FullName -eq $target -or $file.FullName -eq $temp) { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, ('VESPER/' + $relative), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $archive.Dispose(); $archive = $null
    [IO.File]::Move($temp, $target, $false)
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    Write-Host "$hash  $target"
} finally {
    if ($null -ne $archive) { $archive.Dispose() }
    # Only our uniquely named, unpublished temporary archive may be removed.
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
}
