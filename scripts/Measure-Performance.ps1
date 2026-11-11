<#
.SYNOPSIS  Measures what can be measured: startup, idle CPU, memory and handle counts of a running VESPER.
.EXAMPLE   .\scripts\Measure-Performance.ps1 -Exe .\release\VESPER-1.0.0\VESPER.exe -IdleSeconds 60
.NOTES     Prints and saves exactly what was measured, with the machine description. It invents nothing.
           Throughput comparisons against FDM/IDM need a controlled server and are described in DOCUMENTATION\RELEASE.md.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [int]$IdleSeconds = 60,
    [switch]$Background
)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Gui { [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags); }
"@

if (Get-Process -Name VESPER -ErrorAction SilentlyContinue) { throw "Close VESPER first so the measurement starts cold." }
$launchArgs = if ($Background) { "--background" } else { "" }

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $Exe -ArgumentList $launchArgs -PassThru
if ($Background) { Start-Sleep -Seconds 2 } else { while ($process.MainWindowHandle -eq 0 -and $clock.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 20; $process.Refresh() } }
$startupMs = [int]$clock.Elapsed.TotalMilliseconds

Start-Sleep -Seconds 5                                     # let startup work finish before the idle window
$process.Refresh()
$cpu0 = $process.TotalProcessorTime.TotalSeconds; $t0 = Get-Date
Start-Sleep -Seconds $IdleSeconds
$process.Refresh()
$cpuPct = (($process.TotalProcessorTime.TotalSeconds - $cpu0) / ((Get-Date) - $t0).TotalSeconds / [Environment]::ProcessorCount) * 100

$lines = @(
    "VESPER measurement  $(Get-Date -Format s)",
    "Machine: $((Get-CimInstance Win32_Processor).Name), $([int]((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB)) GB RAM, $((Get-CimInstance Win32_OperatingSystem).Caption)",
    "Startup to first window: $startupMs ms (cold process start, includes engine start and database open)",
    ("Idle CPU over {0} s: {1:N2} % of total CPU" -f $IdleSeconds, $cpuPct),
    ("Working set: {0:N1} MB   Private bytes: {1:N1} MB" -f ($process.WorkingSet64/1MB), ($process.PrivateMemorySize64/1MB)),
    "Handles: $($process.HandleCount)   GDI objects: $([Gui]::GetGuiResources($process.Handle, 0))   USER objects: $([Gui]::GetGuiResources($process.Handle, 1))",
    "Threads: $($process.Threads.Count)"
)
$lines | ForEach-Object { Write-Host $_ }
$report = Join-Path (Split-Path -Parent $PSScriptRoot) ("release\perf-{0}.txt" -f (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path (Split-Path $report) -Force | Out-Null
$lines | Set-Content $report
Write-Host "Saved $report"
Write-Host "VESPER is still running; exit it from the tray when done."
