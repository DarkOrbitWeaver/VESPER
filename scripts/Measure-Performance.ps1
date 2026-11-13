<#
.SYNOPSIS  Compares release startup, UI responsiveness, transfer throughput, memory and media processing against a Git baseline.
.EXAMPLE   .\scripts\Measure-Performance.ps1 -BaselineRef 07d6143
.NOTES     Runs only on Windows. Builds both revisions and stores raw samples under release\performance-*.
#>
param(
    [string]$BaselineRef = "07d6143",
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
[StructLayout(LayoutKind.Sequential)]
public struct VesperPerformanceInfo {
    public uint Size;
    public UIntPtr CommitTotal, CommitLimit, CommitPeak;
    public UIntPtr PhysicalTotal, PhysicalAvailable, SystemCache;
    public UIntPtr KernelTotal, KernelPaged, KernelNonpaged, PageSize;
    public uint HandleCount, ProcessCount, ThreadCount;
}
public static class VesperPerformanceNative {
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetPerformanceInfo(out VesperPerformanceInfo info, uint size);
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

function Copy-RestoreAssets([string]$source, [string]$destination) {
    $projects = @(
        [pscustomobject]@{ Directory = ''; Project = 'VESPER.csproj' },
        [pscustomobject]@{ Directory = 'src\Vesper.Engine'; Project = 'Vesper.Engine.csproj' },
        [pscustomobject]@{ Directory = 'src\Vesper.MediaResolver'; Project = 'Vesper.MediaResolver.csproj' },
        [pscustomobject]@{ Directory = 'src\Vesper.NativeHost'; Project = 'Vesper.NativeHost.csproj' },
        [pscustomobject]@{ Directory = 'tests\Vesper.Tests'; Project = 'Vesper.Tests.csproj' }
    )

    foreach ($project in $projects) {
        $projectSource = if ($project.Directory) { Join-Path $source $project.Directory } else { $source }
        $projectDestination = if ($project.Directory) { Join-Path $destination $project.Directory } else { $destination }
        $sourceObj = Join-Path $projectSource 'obj'
        $destinationObj = Join-Path $projectDestination 'obj'
        [IO.Directory]::CreateDirectory($destinationObj) | Out-Null
        $restoreFiles = @(
            'project.assets.json',
            'project.nuget.cache',
            "$($project.Project).nuget.g.props",
            "$($project.Project).nuget.g.targets"
        )
        foreach ($file in $restoreFiles) {
            $sourceFile = Join-Path $sourceObj $file
            if (-not (Test-Path -LiteralPath $sourceFile)) {
                throw "Cached restore asset is missing: $sourceFile"
            }
            Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $destinationObj $file) -Force
        }
    }
}

function Build-Version([string]$label, [string]$source, [string]$build) {
    $app = Join-Path $source 'VESPER.csproj'
    $tests = Join-Path $source 'tests\Vesper.Tests\Vesper.Tests.csproj'
    $publish = Join-Path $build 'app'
    [IO.Directory]::CreateDirectory($build) | Out-Null
    [IO.Directory]::CreateDirectory($publish) | Out-Null

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
                if ($row.items -eq 2000 -and $row.itemsPerSecond -gt 0) { $rows += $row; continue }
                if ($row.sizeBytes -eq 16384 -and $row.samples -gt 0 -and $row.medianMs -gt 0) { $rows += $row; continue }
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
    if (@($rows | Where-Object connections -eq 1).Count -lt 1 -or @($rows | Where-Object connections -eq 4).Count -lt 1) {
        throw "Could not read throughput samples for both connection counts from $log."
    }
    if ((@($rows | Where-Object sizeBytes -eq 16384).Count -lt 1) -or
        (@($rows | Where-Object itemsPerSecond -gt 0).Count -lt 3)) {
        throw "Could not read small-transfer and 2,000-item queue samples from $log."
    }
    return $rows | ForEach-Object { $_ | Add-Member -NotePropertyName run -NotePropertyValue $iteration -PassThru }
}

function Parse-MediaBenchmarkLog([string]$path) {
    $rows = @()
    $pattern = 'remux:\s+container=(?<container>mp4|webm|mkv);\s+duration=(?<duration>[0-9.]+)s;\s+samples=(?<samples>\d+);\s+bytes=(?<bytes>\d+);\s+merge-p50-ms=(?<merge>[0-9.]+);\s+validate-p50-ms=(?<validate>[0-9.]+);\s+merge-MiB-s=(?<throughput>[0-9.]+);\s+merge-allocated-MiB=(?<mergeAllocated>[0-9.]+);\s+validate-allocated-MiB=(?<validateAllocated>[0-9.]+);\s+peak-process-working-set-growth-MiB=(?<workingSet>[0-9.]+)'
    foreach ($line in Get-Content -LiteralPath $path) {
        $text = $line.Trim()
        if ($text -notmatch $pattern) { continue }
        $rows += [pscustomobject]@{
            container = $Matches.container
            durationSeconds = [double]$Matches.duration
            samples = [int]$Matches.samples
            outputBytes = [long]$Matches.bytes
            mergeMedianMs = [double]$Matches.merge
            validationMedianMs = [double]$Matches.validate
            mergeMiBPerSecond = [double]$Matches.throughput
            mergeAllocatedMiB = [double]$Matches.mergeAllocated
            validationAllocatedMiB = [double]$Matches.validateAllocated
            peakWorkingSetGrowthMiB = [double]$Matches.workingSet
        }
    }
    return $rows
}

function Measure-MediaBenchmarks([string]$label, $version) {
    $log = Join-Path $results "$label-media.log"
    $trx = Join-Path $results "$label-media.trx"
    Invoke-DotNet $version.Source @('test', $version.Tests, '-c', 'Release', '--no-build', '--no-restore', '--nologo',
        '--filter', 'Category=MediaBenchmark', '--logger', 'console;verbosity=detailed', '--logger', "trx;LogFileName=$label-media.trx",
        '--results-directory', $results) $log
    $rows = @(Parse-MediaBenchmarkLog $log)
    foreach ($container in 'mp4', 'webm', 'mkv') {
        if (@($rows | Where-Object container -eq $container).Count -ne 1) {
            throw "Could not read one $container remux result from $log."
        }
    }
    return $rows
}

function Parse-SegmentedBenchmarkLog([string]$path) {
    $rows = @()
    $pattern = 'segmented-median:\s+protocol=(?<protocol>hls|dash);\s+samples=(?<samples>\d+) after one warm-up;\s+elapsed-ms=(?<elapsed>[0-9.]+);\s+media-bytes=(?<mediaBytes>\d+);\s+entity-bytes=(?<entityBytes>\d+);\s+output-bytes=(?<outputBytes>\d+);\s+managed-allocated-MiB=(?<allocated>[0-9.]+);\s+validation-ms=(?<validation>[0-9.]+);\s+requests=(?<requests>\d+)'
    foreach ($line in Get-Content -LiteralPath $path) {
        $text = $line.Trim()
        if ($text -notmatch $pattern) { continue }
        $rows += [pscustomobject]@{
            protocol = $Matches.protocol
            samples = [int]$Matches.samples
            elapsedMs = [double]$Matches.elapsed
            mediaBytes = [long]$Matches.mediaBytes
            entityBytes = [long]$Matches.entityBytes
            outputBytes = [long]$Matches.outputBytes
            allocatedMiB = [double]$Matches.allocated
            validationMs = [double]$Matches.validation
            requests = [int]$Matches.requests
        }
    }
    return $rows
}

function Measure-SegmentedBenchmarks([string]$label, $version) {
    $log = Join-Path $results "$label-segmented-media.log"
    $trx = Join-Path $results "$label-segmented-media.trx"
    Invoke-DotNet $version.Source @('test', $version.Tests, '-c', 'Release', '--no-build', '--no-restore', '--nologo',
        '--filter', 'Category=SegmentedMergeBenchmark', '--logger', 'console;verbosity=detailed', '--logger', "trx;LogFileName=$label-segmented-media.trx",
        '--results-directory', $results) $log
    $rows = @(Parse-SegmentedBenchmarkLog $log)
    foreach ($protocol in 'hls', 'dash') {
        if (@($rows | Where-Object protocol -eq $protocol).Count -ne 1) {
            throw "Could not read one $protocol segmented merge result from $log."
        }
    }
    return $rows
}

function Get-MachineInfo {
    $processorName = 'Unavailable'
    $ramGiB = $null
    $osCaption = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    try {
        $registryProcessor = Get-ItemPropertyValue -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' -Name ProcessorNameString -ErrorAction Stop
        if ($registryProcessor) { $processorName = $registryProcessor.Trim() }
    }
    catch { }

    try {
        $info = [VesperPerformanceInfo]::new()
        $size = [Runtime.InteropServices.Marshal]::SizeOf($info)
        if ([VesperPerformanceNative]::GetPerformanceInfo([ref]$info, [uint32]$size)) {
            $ramGiB = [Math]::Round(($info.PhysicalTotal.ToUInt64() * $info.PageSize.ToUInt64()) / 1GB, 1)
        }
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

    # Keep the measurements identical while the product code stays at the selected baseline revision.
    Copy-Item -LiteralPath (Join-Path $root 'BotanicalPreview.cs') -Destination (Join-Path $baselineSource 'BotanicalPreview.cs') -Force
    Copy-Item -LiteralPath (Join-Path $root 'tests\Vesper.Tests\StressAndBenchmarkTests.cs') `
        -Destination (Join-Path $baselineSource 'tests\Vesper.Tests\StressAndBenchmarkTests.cs') -Force
    Copy-Item -LiteralPath (Join-Path $root 'tests\Vesper.Tests\MediaMergeTests.cs') `
        -Destination (Join-Path $baselineSource 'tests\Vesper.Tests\MediaMergeTests.cs') -Force
    $baselineMediaTestsPath = Join-Path $baselineSource 'tests\Vesper.Tests\MediaMergeTests.cs'
    $baselineMediaTests = [IO.File]::ReadAllText($baselineMediaTestsPath)
    $baselineMediaTests = $baselineMediaTests.Replace('Assert.Equal("One.en.srt", segmentedJob.FileName);',
        'Assert.Equal("One.en.vtt", segmentedJob.FileName);')
    $baselineMediaTests = [regex]::Replace($baselineMediaTests,
        '        Assert\.True\(HlsWebVttPlan\.TryParse\(segmentedJob\.MediaPlanJson, out HlsWebVttPlan subtitlePlan\)\);\r?\n        Assert\.Equal\("srt", subtitlePlan\.OutputFormat\);',
        '        Assert.True(HlsWebVttPlan.TryParse(segmentedJob.MediaPlanJson, out _));')
    if ($baselineMediaTests.Contains('subtitlePlan.OutputFormat')) {
        throw 'The temporary baseline media benchmark source still requires the newer subtitle plan.'
    }
    [IO.File]::WriteAllText($baselineMediaTestsPath, $baselineMediaTests, [Text.UTF8Encoding]::new($false))
    Copy-RestoreAssets $root $baselineSource

    $baselineEngineHost = Join-Path $baselineSource 'EngineHost.cs'
    $baselineText = [IO.File]::ReadAllText($baselineEngineHost)
    if ($baselineText -notmatch 'GetEnvironmentVariable\("VESPER_TEST_DATA_DIRECTORY"\)') {
        throw 'The selected baseline cannot isolate benchmark data through VESPER_TEST_DATA_DIRECTORY.'
    }

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

    $baselineMedia = Measure-MediaBenchmarks 'baseline' $baseline
    $currentMedia = Measure-MediaBenchmarks 'current' $current
    $baselineSegmentedMedia = Measure-SegmentedBenchmarks 'baseline' $baseline
    $currentSegmentedMedia = Measure-SegmentedBenchmarks 'current' $current

    $machine = Get-MachineInfo
    $baselineResult = [pscustomobject]@{
        revision = $baselineCommit
        runtimeMode = 'Framework-dependent apphost using the installed .NET 10 runtime.'
        benchmarkDataDirectoryHook = 'Baseline and current processes used VESPER_TEST_DATA_DIRECTORY under the temporary benchmark directory.'
        machine = $machine
        runs = $Runs
        startupSamples = $baselineStartup
        idle = $baselineIdle
        grid = $baselineGrid
        throughputSamples = $baselineThroughput
        queueSamples = @($baselineThroughput | Where-Object itemsPerSecond -gt 0)
        mediaSamples = $baselineMedia
        segmentedMergeSamples = $baselineSegmentedMedia
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
        queueSamples = @($currentThroughput | Where-Object itemsPerSecond -gt 0)
        mediaSamples = $currentMedia
        segmentedMergeSamples = $currentSegmentedMedia
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
        baselineScrollP95Ms = $baselineGrid.GridScrollP95Ms
        currentScrollP95Ms = $currentGrid.GridScrollP95Ms
        baselineProgressBatchP95Ms = $baselineGrid.ProgressBatchP95Ms
        currentProgressBatchP95Ms = $currentGrid.ProgressBatchP95Ms
        maximumMs = 16.7
        gdiChangeAfter20MainWindows = $currentGrid.GdiChangeAfter20MainWindows
        passed = ($currentGrid.Rows -eq 10000 -and $currentGrid.GridDrawP95Ms -le 16.7 -and
            $currentGrid.GridScrollP95Ms -le 16.7 -and $currentGrid.ProgressBatchP95Ms -le 16.7)
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
    $baselineSmallSamples = @($baselineThroughput | Where-Object sizeBytes -eq 16384)
    $currentSmallSamples = @($currentThroughput | Where-Object sizeBytes -eq 16384)
    $smallTransfer = $null
    if ($baselineSmallSamples.Count -gt 0 -and $currentSmallSamples.Count -gt 0) {
        $baselineSmallMedian = Get-Median ([double[]]@($baselineSmallSamples | ForEach-Object medianMs))
        $currentSmallMedian = Get-Median ([double[]]@($currentSmallSamples | ForEach-Object medianMs))
        $smallTransfer = [pscustomobject]@{
            sizeBytes = 16384
            baselineRuns = $baselineSmallSamples.Count
            currentRuns = $currentSmallSamples.Count
            baselineSamplesPerRun = $baselineSmallSamples[0].samples
            currentSamplesPerRun = $currentSmallSamples[0].samples
            baselineMedianMs = [Math]::Round($baselineSmallMedian, 2)
            currentMedianMs = [Math]::Round($currentSmallMedian, 2)
            ratio = [Math]::Round($currentSmallMedian / $baselineSmallMedian, 4)
            maximumRegressionRatio = 1.05
            passed = $currentSmallMedian -le $baselineSmallMedian * 1.05
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
    $baselineQueueSamples = @($baselineThroughput | Where-Object itemsPerSecond -gt 0)
    $currentQueueSamples = @($currentThroughput | Where-Object itemsPerSecond -gt 0)
    $queue = $null
    if ($baselineQueueSamples.Count -gt 0 -and $currentQueueSamples.Count -gt 0) {
        $baselineQueueMedian = Get-Median ([double[]]@($baselineQueueSamples | ForEach-Object elapsedMs))
        $currentQueueMedian = Get-Median ([double[]]@($currentQueueSamples | ForEach-Object elapsedMs))
        $baselineQueueAllocationMedian = Get-Median ([double[]]@($baselineQueueSamples | ForEach-Object allocatedMiB))
        $currentQueueAllocationMedian = Get-Median ([double[]]@($currentQueueSamples | ForEach-Object allocatedMiB))
        $queue = [pscustomobject]@{
            items = 2000
            baselineSamples = $baselineQueueSamples.Count
            currentSamples = $currentQueueSamples.Count
            baselineMedianMs = [Math]::Round($baselineQueueMedian, 2)
            currentMedianMs = [Math]::Round($currentQueueMedian, 2)
            elapsedRatio = [Math]::Round($currentQueueMedian / $baselineQueueMedian, 4)
            baselineAllocatedMiB = [Math]::Round($baselineQueueAllocationMedian, 2)
            currentAllocatedMiB = [Math]::Round($currentQueueAllocationMedian, 2)
            allocationRatio = [Math]::Round($currentQueueAllocationMedian / $baselineQueueAllocationMedian, 4)
            maximumRegressionRatio = 1.05
            passed = ($currentQueueMedian -le $baselineQueueMedian * 1.05 -and
                $currentQueueAllocationMedian -le $baselineQueueAllocationMedian * 1.05)
        }
    }
    $mediaProcessing = foreach ($container in 'mp4', 'webm', 'mkv') {
        $base = $baselineMedia | Where-Object container -eq $container
        $currentResultForContainer = $currentMedia | Where-Object container -eq $container
        [pscustomobject]@{
            container = $container
            durationSeconds = $base.durationSeconds
            outputBytes = $base.outputBytes
            baselineMergeMedianMs = $base.mergeMedianMs
            currentMergeMedianMs = $currentResultForContainer.mergeMedianMs
            mergeTimeRatio = [Math]::Round($currentResultForContainer.mergeMedianMs / $base.mergeMedianMs, 4)
            baselineMiBPerSecond = $base.mergeMiBPerSecond
            currentMiBPerSecond = $currentResultForContainer.mergeMiBPerSecond
            baselineAllocatedMiB = $base.mergeAllocatedMiB
            currentAllocatedMiB = $currentResultForContainer.mergeAllocatedMiB
            baselinePeakWorkingSetGrowthMiB = $base.peakWorkingSetGrowthMiB
            currentPeakWorkingSetGrowthMiB = $currentResultForContainer.peakWorkingSetGrowthMiB
            validationMedianMs = $currentResultForContainer.validationMedianMs
        }
    }
    $segmentedMerges = foreach ($protocol in 'hls', 'dash') {
        $base = $baselineSegmentedMedia | Where-Object protocol -eq $protocol
        $currentResultForProtocol = $currentSegmentedMedia | Where-Object protocol -eq $protocol
        [pscustomobject]@{
            protocol = $protocol
            mediaBytes = $base.mediaBytes
            outputBytes = $base.outputBytes
            requests = $base.requests
            baselineMedianMs = $base.elapsedMs
            currentMedianMs = $currentResultForProtocol.elapsedMs
            elapsedRatio = [Math]::Round($currentResultForProtocol.elapsedMs / $base.elapsedMs, 4)
            baselineAllocatedMiB = $base.allocatedMiB
            currentAllocatedMiB = $currentResultForProtocol.allocatedMiB
            baselineValidationMs = $base.validationMs
            currentValidationMs = $currentResultForProtocol.validationMs
        }
    }
    $comparison = [pscustomobject]@{
        baselineRevision = $baselineCommit
        currentRevision = $currentCommit
        runtimeMode = 'Framework-dependent apphost using the installed .NET 10 runtime.'
        currentWorkingTreeDirty = $currentDirty
        machine = $machine
        thresholds = [pscustomobject]@{
            startup = 'Current median <= baseline median + max(5% of baseline, 30 ms).'
            gridPaint = '10,000 rows; draw, scroll and 3-row progress-batch p95 <= 16.7 ms.'
            throughput = 'Current median >= 95% of baseline for 1 and 4 connections.'
            smallDirectLatency = '16 KiB direct download median <= 105% of baseline.'
            queue = '2,000 queued items; elapsed time and allocations <= 105% of baseline.'
            privateMemory = 'Current idle median <= baseline median + 12 MiB.'
        }
        gates = [pscustomobject]@{
            startup = $startup
            gridPaint = $grid
            throughput = @($throughput)
            smallDirectLatency = $smallTransfer
            queue = $queue
            privateMemory = $memory
            mediaProcessing = @($mediaProcessing)
            segmentedMerges = @($segmentedMerges)
        }
        passed = $startup.passed -and $grid.passed -and $memory.passed -and $smallTransfer.passed -and $queue.passed `
            -and @($throughput | Where-Object { -not $_.passed }).Count -eq 0
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
