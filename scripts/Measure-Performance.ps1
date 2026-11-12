<#
.SYNOPSIS  Compares release startup, UI paint, transfer throughput and memory against a Git baseline.
.EXAMPLE   .\scripts\Measure-Performance.ps1 -BaselineRef 98ee2db
.NOTES     Runs only on Windows. Builds both revisions and stores raw samples under release\performance-*.
#>
param(
    [string]$BaselineRef = "98ee2db",
    [ValidateRange(5, 100)][int]$Runs = 20,
    [ValidateRange(10, 600)][int]$IdleSeconds = 60,
    [ValidateRange(3, 20)][int]$ThroughputRuns = 5
)
$ErrorActionPreference = 'Stop'
if (-not [OperatingSystem]::IsWindows()) { throw 'Performance comparison requires Windows.' }

if (Get-Process -Name VESPER -ErrorAction SilentlyContinue) {
    throw 'Close VESPER before running the performance comparison.'
}

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$baselineCommit = (& git rev-parse --verify "$BaselineRef^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $baselineCommit.Length -ne 40) { throw "Could not resolve baseline '$BaselineRef'." }
$currentCommit = (& git rev-parse HEAD).Trim()
$currentDirty = [bool](& git status --porcelain)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runId = [guid]::NewGuid().ToString('N')
$results = Join-Path $root "release\performance-$stamp"
$tempRoot = Join-Path $env:TEMP "vesper-performance-$runId"
$baselineSource = Join-Path $tempRoot 'baseline-source'
$baselineBuild = Join-Path $tempRoot 'baseline-build'
$currentBuild = Join-Path $tempRoot 'current-build'
[IO.Directory]::CreateDirectory($results) | Out-Null
[IO.Directory]::CreateDirectory($baselineSource) | Out-Null

if (-not ('VesperPerformanceNative' -as [type])) { Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class VesperPerformanceNative {
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr process, uint flags);
}
"@ }

function Invoke-DotNet([string]$workingDirectory, [string[]]$arguments, [string]$logPath) {
    Push-Location $workingDirectory
    try {
        & dotnet @arguments *>&1 | Tee-Object -FilePath $logPath | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments[0]) failed with exit code $LASTEXITCODE. See $logPath" }
    }
    finally { Pop-Location }
}

function Build-Version([string]$label, [string]$source, [string]$build) {
    $app = Join-Path $source 'VESPER.csproj'
    $tests = Join-Path $source 'tests\Vesper.Tests\Vesper.Tests.csproj'
    $publish = Join-Path $build 'app'
    [IO.Directory]::CreateDirectory($build) | Out-Null
    [IO.Directory]::CreateDirectory($publish) | Out-Null

    Invoke-DotNet $source @('restore', $app, '--ignore-failed-sources', '-p:NuGetAudit=false') `
        (Join-Path $results "$label-app-restore.log")
    Invoke-DotNet $source @('restore', $tests, '--ignore-failed-sources', '-p:NuGetAudit=false') `
        (Join-Path $results "$label-tests-restore.log")
    Invoke-DotNet $source @('publish', $app, '-c', 'Release', '--self-contained', 'false',
        '--no-restore', '--nologo', '-p:NuGetAudit=false', '-o', $publish) `
        (Join-Path $results "$label-publish.log")
    Invoke-DotNet $source @('build', $tests, '-c', 'Release', '--no-restore', '--nologo', '-p:NuGetAudit=false') `
        (Join-Path $results "$label-tests-build.log")
    return [pscustomobject]@{ Source = $source; App = Join-Path $publish 'VESPER.exe'; Tests = $tests }
}

function Start-VesperWindow([string]$exe, [string]$dataDirectory) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $exe
    $info.WorkingDirectory = Split-Path -Parent $exe
    $info.UseShellExecute = $false
    $info.Environment['VESPER_TEST_DATA_DIRECTORY'] = $dataDirectory
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw "Could not start $exe." }
    try {
        while ($clock.Elapsed.TotalSeconds -lt 45) {
            if ($process.HasExited) { throw "VESPER exited before showing its window (code $($process.ExitCode))." }
            $process.Refresh()
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                $clock.Stop()
                return [pscustomobject]@{ Process = $process; StartupMs = $clock.Elapsed.TotalMilliseconds }
            }
            Start-Sleep -Milliseconds 25
        }
        throw 'VESPER did not show a window within 45 seconds.'
    }
    catch {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
        throw
    }
}

function Stop-Vesper([System.Diagnostics.Process]$process) {
    try {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    }
    finally { $process.Dispose() }
}

function Measure-Startup([string]$label, $version, [int]$iteration) {
    $data = Join-Path $tempRoot "$label-data-$iteration"
    $started = Start-VesperWindow $version.App $data
    Stop-Vesper $started.Process
    [pscustomobject]@{ run = $iteration; mode = if ($iteration -eq 1) { 'first process' } else { 'subsequent process' }; startupMs = $started.StartupMs }
}

function Get-Median([double[]]$values) {
    if ($values.Length -eq 0) { throw 'Cannot calculate a median from no samples.' }
    [Array]::Sort($values)
    if ($values.Length % 2) { return $values[[int]($values.Length / 2)] }
    return ($values[$values.Length / 2 - 1] + $values[$values.Length / 2]) / 2
}

function Measure-Idle([string]$label, $version) {
    $started = Start-VesperWindow $version.App (Join-Path $tempRoot "$label-idle-data")
    $process = $started.Process
    try {
        Start-Sleep -Seconds 5
        $process.Refresh()
        $cpuStart = $process.TotalProcessorTime.TotalSeconds
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $private = [Collections.Generic.List[double]]::new()
        $working = [Collections.Generic.List[double]]::new()
        $gdi = [Collections.Generic.List[int]]::new()
        $user = [Collections.Generic.List[int]]::new()
        while ($clock.Elapsed.TotalSeconds -lt $IdleSeconds) {
            $process.Refresh()
            $private.Add($process.PrivateMemorySize64 / 1MB)
            $working.Add($process.WorkingSet64 / 1MB)
            $gdi.Add([int][VesperPerformanceNative]::GetGuiResources($process.Handle, 0))
            $user.Add([int][VesperPerformanceNative]::GetGuiResources($process.Handle, 1))
            Start-Sleep -Seconds 1
        }
        $clock.Stop()
        $process.Refresh()
        $cpuPercent = (($process.TotalProcessorTime.TotalSeconds - $cpuStart) / $clock.Elapsed.TotalSeconds / [Environment]::ProcessorCount) * 100
        return [pscustomobject]@{
            idleSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
            idleCpuPercent = [Math]::Round($cpuPercent, 3)
            privateMemoryMiBMedian = [Math]::Round((Get-Median $private.ToArray()), 2)
            workingSetMiBMedian = [Math]::Round((Get-Median $working.ToArray()), 2)
            privateMemorySamplesMiB = $private.ToArray()
            gdiStart = $gdi[0]
            gdiEnd = $gdi[$gdi.Count - 1]
            gdiRange = (($gdi | Measure-Object -Minimum -Maximum).Maximum - ($gdi | Measure-Object -Minimum -Maximum).Minimum)
            userStart = $user[0]
            userEnd = $user[$user.Count - 1]
            handlesEnd = $process.HandleCount
            threadsEnd = $process.Threads.Count
        }
    }
    finally { Stop-Vesper $process }
}

function Measure-Grid([string]$label, $version) {
    $capture = Join-Path $tempRoot "$label-grid-capture"
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $version.App
    $info.WorkingDirectory = Split-Path -Parent $version.App
    $info.UseShellExecute = $false
    $info.Environment['VESPER_TEST_DATA_DIRECTORY'] = Join-Path $tempRoot "$label-grid-data"
    $info.ArgumentList.Add('--ui-preview')
    $info.ArgumentList.Add('--capture')
    $info.ArgumentList.Add($capture)
    $process = [Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw "Could not run the $label UI paint capture." }
    $process.WaitForExit(120000) | Out-Null
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(); throw "$label UI paint capture timed out." }
    if ($process.ExitCode -ne 0) { throw "$label UI paint capture failed with exit code $($process.ExitCode)." }
    $metricsPath = Join-Path $capture 'preview-metrics.json'
    if (-not (Test-Path -LiteralPath $metricsPath)) { throw "$label UI paint capture did not write preview-metrics.json." }
    return Get-Content -Raw -LiteralPath $metricsPath | ConvertFrom-Json
}

function Parse-BenchmarkLog([string]$path) {
    $rows = @()
    foreach ($line in Get-Content -LiteralPath $path) {
        $text = $line.Trim()
        if ($text.StartsWith('{')) {
            try {
                $row = $text | ConvertFrom-Json
                if ($row.connections -in 1, 4 -and $row.mibPerSecond -gt 0) { $rows += $row; continue }
            }
            catch { }
        }
        if ($text -match 'connections<=([14]):\s*([0-9.]+)\s+MB/s over ([0-9.]+) s') {
            $rows += [pscustomobject]@{
                connections = [int]$Matches[1]
                elapsedMs = [double]$Matches[3] * 1000
                mibPerSecond = [double]$Matches[2]
            }
        }
    }
    return $rows
}

function Measure-Throughput([string]$label, $version, [int]$iteration) {
    $log = Join-Path $results "$label-throughput-$iteration.log"
    $trx = Join-Path $results "$label-throughput-$iteration.trx"
    Invoke-DotNet $version.Source @('test', $version.Tests, '-c', 'Release', '--no-build', '--no-restore', '--nologo',
        '--filter', 'Category=Benchmark', '--logger', 'console;verbosity=detailed', '--logger', "trx;LogFileName=$label-$iteration.trx",
        '--results-directory', $results) $log
    $rows = @(Parse-BenchmarkLog $log)
    if (@($rows | Where-Object connections -eq 1).Count -ne 1 -or @($rows | Where-Object connections -eq 4).Count -ne 1) {
        throw "Could not read both throughput samples from $log."
    }
    return $rows | ForEach-Object { $_ | Add-Member -NotePropertyName run -NotePropertyValue $iteration -PassThru }
}

function Get-MachineInfo {
    $processorName = 'Unavailable'
    $ramGiB = $null
    $osCaption = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    try {
        $processor = Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop | Select-Object -First 1
        if ($processor.Name) { $processorName = $processor.Name }
        $computer = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
        if ($computer.TotalPhysicalMemory) { $ramGiB = [Math]::Round($computer.TotalPhysicalMemory / 1GB, 1) }
        $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        if ($operatingSystem.Caption) { $osCaption = $operatingSystem.Caption }
    }
    catch { }
    [pscustomobject]@{
        processor = $processorName
        logicalProcessors = [Environment]::ProcessorCount
        ramGiB = $ramGiB
        os = $osCaption
        osVersion = [Environment]::OSVersion.Version.ToString()
        dotnetSdk = (& dotnet --version).Trim()
        architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
}

function Write-Json([string]$path, $value) {
    $json = ConvertTo-Json -InputObject $value -Depth 12
    [IO.File]::WriteAllText($path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

try {
    $archive = Join-Path $tempRoot 'baseline.tar'
    [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    & git archive --format=tar --output=$archive $baselineCommit
    if ($LASTEXITCODE -ne 0) { throw 'Could not export the baseline source.' }
    & tar -xf $archive -C $baselineSource
    if ($LASTEXITCODE -ne 0) { throw 'Could not unpack the baseline source.' }

    $baselineEngineHost = Join-Path $baselineSource 'EngineHost.cs'
    $baselineText = [IO.File]::ReadAllText($baselineEngineHost)
    $dataDirectoryPattern = 'public static string DataDirectory\s*=>\s*Path\.Combine\(\s*Environment\.GetFolderPath\(Environment\.SpecialFolder\.LocalApplicationData\),\s*"VESPER"\);'
    $match = [regex]::Match($baselineText, $dataDirectoryPattern)
    if (-not $match.Success) { throw 'The baseline EngineHost data-directory code changed; cannot isolate its benchmark data.' }
    $isolatedDirectory = @'
public static string DataDirectory
    {
        get
        {
            string? testDirectory = Environment.GetEnvironmentVariable("VESPER_TEST_DATA_DIRECTORY");
            return string.IsNullOrWhiteSpace(testDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VESPER")
                : Path.GetFullPath(testDirectory);
        }
    }
'@
    $baselineText = $baselineText.Remove($match.Index, $match.Length).Insert($match.Index, $isolatedDirectory.Trim())
    [IO.File]::WriteAllText($baselineEngineHost, $baselineText, [Text.UTF8Encoding]::new($false))

    $baseline = Build-Version 'baseline' $baselineSource $baselineBuild
    $current = Build-Version 'current' $root $currentBuild
    if (-not (Test-Path -LiteralPath $baseline.App) -or -not (Test-Path -LiteralPath $current.App)) {
        throw 'A Release apphost was not produced.'
    }

    $baselineStartup = @()
    $currentStartup = @()
    for ($i = 1; $i -le $Runs; $i++) {
        if ($i % 2) {
            $baselineStartup += Measure-Startup 'baseline' $baseline $i
            $currentStartup += Measure-Startup 'current' $current $i
        }
        else {
            $currentStartup += Measure-Startup 'current' $current $i
            $baselineStartup += Measure-Startup 'baseline' $baseline $i
        }
    }

    $baselineIdle = Measure-Idle 'baseline' $baseline
    $currentIdle = Measure-Idle 'current' $current
    $baselineGrid = Measure-Grid 'baseline' $baseline
    $currentGrid = Measure-Grid 'current' $current

    $baselineThroughput = @()
    $currentThroughput = @()
    for ($i = 1; $i -le $ThroughputRuns; $i++) {
        if ($i % 2) {
            $baselineThroughput += Measure-Throughput 'baseline' $baseline $i
            $currentThroughput += Measure-Throughput 'current' $current $i
        }
        else {
            $currentThroughput += Measure-Throughput 'current' $current $i
            $baselineThroughput += Measure-Throughput 'baseline' $baseline $i
        }
    }

    $machine = Get-MachineInfo
    $baselineResult = [pscustomobject]@{
        revision = $baselineCommit
        runtimeMode = 'Framework-dependent apphost using the installed .NET 10 runtime.'
        benchmarkDataDirectoryHook = 'Applied only to this temporary baseline build so both revisions use empty isolated data.'
        machine = $machine
        runs = $Runs
        startupSamples = $baselineStartup
        idle = $baselineIdle
        grid = $baselineGrid
        throughputSamples = $baselineThroughput
    }
    $currentResult = [pscustomobject]@{
        revision = $currentCommit
        runtimeMode = 'Framework-dependent apphost using the installed .NET 10 runtime.'
        workingTreeDirty = $currentDirty
        machine = $machine
        runs = $Runs
        startupSamples = $currentStartup
        idle = $currentIdle
        grid = $currentGrid
        throughputSamples = $currentThroughput
    }
    Write-Json (Join-Path $results 'baseline.json') $baselineResult
    Write-Json (Join-Path $results 'current.json') $currentResult

    $baselineStartupMedian = Get-Median ([double[]]@($baselineStartup | ForEach-Object startupMs))
    $currentStartupMedian = Get-Median ([double[]]@($currentStartup | ForEach-Object startupMs))
    $startupAllowance = [Math]::Max(30, $baselineStartupMedian * 0.05)
    $startup = [pscustomobject]@{
        baselineMedianMs = [Math]::Round($baselineStartupMedian, 2)
        currentMedianMs = [Math]::Round($currentStartupMedian, 2)
        maximumAllowedRegressionMs = [Math]::Round($startupAllowance, 2)
        passed = $currentStartupMedian -le $baselineStartupMedian + $startupAllowance
    }
    $grid = [pscustomobject]@{
        rows = $currentGrid.Rows
        baselineP95Ms = $baselineGrid.GridDrawP95Ms
        currentP95Ms = $currentGrid.GridDrawP95Ms
        maximumMs = 16.7
        gdiChangeAfter20MainWindows = $currentGrid.GdiChangeAfter20MainWindows
        passed = $currentGrid.Rows -eq 10000 -and $currentGrid.GridDrawP95Ms -le 16.7
    }
    $throughput = foreach ($connections in 1, 4) {
        $baseMedian = Get-Median ([double[]]@($baselineThroughput | Where-Object connections -eq $connections | ForEach-Object mibPerSecond))
        $currentMedian = Get-Median ([double[]]@($currentThroughput | Where-Object connections -eq $connections | ForEach-Object mibPerSecond))
        [pscustomobject]@{
            connections = $connections
            baselineMedianMiBPerSecond = [Math]::Round($baseMedian, 2)
            currentMedianMiBPerSecond = [Math]::Round($currentMedian, 2)
            minimumRatio = 0.95
            ratio = [Math]::Round($currentMedian / $baseMedian, 4)
            passed = $currentMedian -ge $baseMedian * 0.95
        }
    }
    $memoryRegression = $currentIdle.privateMemoryMiBMedian - $baselineIdle.privateMemoryMiBMedian
    $memory = [pscustomobject]@{
        baselinePrivateMiBMedian = $baselineIdle.privateMemoryMiBMedian
        currentPrivateMiBMedian = $currentIdle.privateMemoryMiBMedian
        regressionMiB = [Math]::Round($memoryRegression, 2)
        maximumRegressionMiB = 12
        passed = $memoryRegression -le 12
    }
    $comparison = [pscustomobject]@{
        baselineRevision = $baselineCommit
        currentRevision = $currentCommit
        runtimeMode = 'Framework-dependent apphost using the installed .NET 10 runtime.'
        currentWorkingTreeDirty = $currentDirty
        machine = $machine
        thresholds = [pscustomobject]@{
            startup = 'Current median <= baseline median + max(5% of baseline, 30 ms).'
            gridPaint = '10,000 rows; p95 <= 16.7 ms.'
            throughput = 'Current median >= 95% of baseline for 1 and 4 connections.'
            privateMemory = 'Current idle median <= baseline median + 12 MiB.'
        }
        gates = [pscustomobject]@{
            startup = $startup
            gridPaint = $grid
            throughput = @($throughput)
            privateMemory = $memory
        }
        passed = $startup.passed -and $grid.passed -and $memory.passed -and @($throughput | Where-Object { -not $_.passed }).Count -eq 0
        rawResults = $results
    }
    Write-Json (Join-Path $results 'comparison.json') $comparison
    Write-Host (ConvertTo-Json -InputObject $comparison -Depth 8)
    Write-Host "Raw samples: $results"
    if (-not $comparison.passed) { exit 1 }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTemp = [IO.Path]::GetFullPath($tempRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $resolvedTempBase = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedTemp.StartsWith($resolvedTempBase, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedTemp) -ne "vesper-performance-$runId") {
            throw "Refusing to remove an unexpected benchmark temp directory: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
