param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [int]$DurationSeconds = 600,

    [int]$SampleIntervalMs = 100,

    [switch]$Append
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$processName = [System.IO.Path]::GetFileNameWithoutExtension($ExecutablePath)
$deadline = [DateTime]::UtcNow.AddSeconds(60)
$process = $null

while ([DateTime]::UtcNow -lt $deadline -and $null -eq $process) {
    $process = Get-Process -Name $processName -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $ExecutablePath } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1

    if ($null -eq $process) {
        Start-Sleep -Milliseconds 50
    }
}

if ($null -eq $process) {
    throw "ADB Explorer did not start within 60 seconds."
}

$process.Id | Set-Content -LiteralPath (Join-Path $OutputDirectory 'pid.txt')
$timelinePath = Join-Path $OutputDirectory 'process-timeline.csv'
$stallPath = Join-Path $OutputDirectory 'ui-stalls.log'
$appendTimeline = $Append -and (Test-Path -LiteralPath $timelinePath)
$writer = [System.IO.StreamWriter]::new($timelinePath, $appendTimeline, [System.Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
if (-not $appendTimeline) {
    $writer.WriteLine('UtcTime,SinceProcessStartMs,CpuPercent,WorkingSetMB,PrivateMemoryMB,Threads,Handles,Responding,ResponseProbeMs,MainWindowHandle')
}

$processorCount = [Environment]::ProcessorCount
$processStartUtc = $process.StartTime.ToUniversalTime()
$endTime = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
$previousTime = [DateTime]::UtcNow
$previousCpu = $process.TotalProcessorTime
$lastMetadataRefresh = [DateTime]::MinValue
$threadCount = 0
$handleCount = 0

try {
    while ([DateTime]::UtcNow -lt $endTime -and -not $process.HasExited) {
        $loopTimer = [System.Diagnostics.Stopwatch]::StartNew()
        $now = [DateTime]::UtcNow
        $process.Refresh()

        $currentCpu = $process.TotalProcessorTime
        $elapsedMs = ($now - $previousTime).TotalMilliseconds
        $cpuPercent = if ($elapsedMs -gt 0) {
            100.0 * ($currentCpu - $previousCpu).TotalMilliseconds / $elapsedMs / $processorCount
        } else {
            0.0
        }

        $responseTimer = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $responding = $process.Responding
        } catch {
            $responding = $false
        }
        $responseTimer.Stop()

        if (($now - $lastMetadataRefresh).TotalSeconds -ge 1) {
            $threadCount = $process.Threads.Count
            $handleCount = $process.HandleCount
            $lastMetadataRefresh = $now
        }

        $line = [string]::Format(
            $culture,
            '{0:o},{1:F1},{2:F2},{3:F2},{4:F2},{5},{6},{7},{8:F1},{9}',
            $now,
            ($now - $processStartUtc).TotalMilliseconds,
            $cpuPercent,
            $process.WorkingSet64 / 1MB,
            $process.PrivateMemorySize64 / 1MB,
            $threadCount,
            $handleCount,
            $responding,
            $responseTimer.Elapsed.TotalMilliseconds,
            $process.MainWindowHandle.ToInt64())
        $writer.WriteLine($line)

        if (-not $responding -or $responseTimer.ElapsedMilliseconds -ge 100) {
            [string]::Format(
                $culture,
                '{0:o} responding={1} probeMs={2:F1} cpu={3:F2}%',
                $now,
                $responding,
                $responseTimer.Elapsed.TotalMilliseconds,
                $cpuPercent) | Add-Content -LiteralPath $stallPath
        }

        $previousTime = $now
        $previousCpu = $currentCpu
        $remainingDelay = $SampleIntervalMs - $loopTimer.ElapsedMilliseconds
        if ($remainingDelay -gt 0) {
            Start-Sleep -Milliseconds $remainingDelay
        }
    }
} finally {
    $writer.Dispose()
}
