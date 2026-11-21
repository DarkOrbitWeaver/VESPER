<#
.SYNOPSIS Creates a source-only ZIP without overwriting an existing artifact.
.NOTES Packages tracked source files from the working tree. Requires Git.
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
    $tracked = @(git -C $root -c core.quotepath=false ls-files --cached --full-name)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) { throw 'Could not list tracked source files.' }
    $archive = [IO.Compression.ZipFile]::Open($temp, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($relative in $tracked) {
        if ($relative -match '(^|/)(\.git|\.vs|\.vesper-dev|\.scratchpad|bin|obj|node_modules|release|TestResults)(/|$)') { continue }
        if ($relative -match '(^|/)\.env($|\.)|\.(user|suo|db|sqlite|vpart|vmeta|zip|patch|diff)$') { continue }
        $path = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $file = Get-Item -LiteralPath $path -Force
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
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
