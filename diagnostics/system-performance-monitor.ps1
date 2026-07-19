param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [int]$DurationSeconds = 600,

    [int]$SampleIntervalMs = 500,

    [switch]$Append
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$outputPath = Join-Path $OutputDirectory 'system-timeline.csv'
$appendTimeline = $Append -and (Test-Path -LiteralPath $outputPath)
$writer = [System.IO.StreamWriter]::new($outputPath, $appendTimeline, [System.Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
if (-not $appendTimeline) {
    $writer.WriteLine('UtcTime,TotalCpuPercent,DpcPercent,InterruptPercent,InterruptsPerSec,ProcessorQueueLength,ContextSwitchesPerSec,AvailableMemoryMB,PageReadsPerSec')
}
$endTime = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
$cpu = [System.Diagnostics.PerformanceCounter]::new('Processor Information', '% Processor Utility', '_Total')
$dpc = [System.Diagnostics.PerformanceCounter]::new('Processor Information', '% DPC Time', '_Total')
$interrupt = [System.Diagnostics.PerformanceCounter]::new('Processor Information', '% Interrupt Time', '_Total')
$interruptRate = [System.Diagnostics.PerformanceCounter]::new('Processor Information', 'Interrupts/sec', '_Total')
$queue = [System.Diagnostics.PerformanceCounter]::new('System', 'Processor Queue Length')
$contextSwitches = [System.Diagnostics.PerformanceCounter]::new('System', 'Context Switches/sec')
$availableMemory = [System.Diagnostics.PerformanceCounter]::new('Memory', 'Available MBytes')
$pageReads = [System.Diagnostics.PerformanceCounter]::new('Memory', 'Page Reads/sec')

[void]$cpu.NextValue()
[void]$dpc.NextValue()
[void]$interrupt.NextValue()
[void]$interruptRate.NextValue()
[void]$queue.NextValue()
[void]$contextSwitches.NextValue()
[void]$availableMemory.NextValue()
[void]$pageReads.NextValue()
Start-Sleep -Seconds 1

try {
    while ([DateTime]::UtcNow -lt $endTime) {
        $sampleStart = [DateTime]::UtcNow

        $writer.WriteLine([string]::Format(
            $culture,
            '{0:o},{1},{2},{3},{4},{5},{6},{7},{8}',
            $sampleStart,
            $cpu.NextValue(),
            $dpc.NextValue(),
            $interrupt.NextValue(),
            $interruptRate.NextValue(),
            $queue.NextValue(),
            $contextSwitches.NextValue(),
            $availableMemory.NextValue(),
            $pageReads.NextValue()))

        $remainingDelay = $SampleIntervalMs - ([DateTime]::UtcNow - $sampleStart).TotalMilliseconds
        if ($remainingDelay -gt 0) {
            Start-Sleep -Milliseconds ([int]$remainingDelay)
        }
    }
} finally {
    $writer.Dispose()
    $cpu.Dispose()
    $dpc.Dispose()
    $interrupt.Dispose()
    $interruptRate.Dispose()
    $queue.Dispose()
    $contextSwitches.Dispose()
    $availableMemory.Dispose()
    $pageReads.Dispose()
}
