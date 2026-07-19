param(
    [Parameter(Mandatory = $true)]
    [int]$TargetProcessId,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$resourcePath = Join-Path $OutputDirectory "process-timeline.csv"
$monitorPath = Join-Path $OutputDirectory "monitor.log"
$dumpPath = Join-Path $OutputDirectory "drag-freeze.dmp"
$dumpLogPath = Join-Path $OutputDirectory "dump-capture.log"
$process = Get-Process -Id $TargetProcessId
$writer = [System.IO.StreamWriter]::new($resourcePath, $false, [System.Text.UTF8Encoding]::new($true))
$writer.AutoFlush = $true
$writer.WriteLine("UtcTime,CpuPercent,WorkingSetMB,PrivateMemoryMB,Threads,Handles,Responding")
$lastCpu = $process.TotalProcessorTime.TotalMilliseconds
$lastSample = [System.Diagnostics.Stopwatch]::StartNew()
$dumpAttempted = $false

try {
    while (-not $process.HasExited) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        $elapsed = $lastSample.Elapsed.TotalMilliseconds
        $cpu = $process.TotalProcessorTime.TotalMilliseconds
        $cpuPercent = if ($elapsed -gt 0) {
            (($cpu - $lastCpu) / $elapsed / [Environment]::ProcessorCount) * 100
        } else {
            0
        }
        $lastCpu = $cpu
        $lastSample.Restart()
        $responding = $process.Responding
        $writer.WriteLine((
            "{0},{1:F2},{2:F2},{3:F2},{4},{5},{6}" -f
            [DateTime]::UtcNow.ToString("O"),
            $cpuPercent,
            ($process.WorkingSet64 / 1MB),
            ($process.PrivateMemorySize64 / 1MB),
            $process.Threads.Count,
            $process.HandleCount,
            $responding
        ))

        if (-not $responding -and -not $dumpAttempted) {
            $dumpAttempted = $true
            "$(Get-Date -Format O) Window stopped responding; collecting full dump." |
                Out-File -FilePath $monitorPath -Append -Encoding utf8
            & dotnet-dump collect --process-id $TargetProcessId --type Full --output $dumpPath 2>&1 |
                Out-File -FilePath $dumpLogPath -Encoding utf8
            "$(Get-Date -Format O) Dump collection finished." |
                Out-File -FilePath $monitorPath -Append -Encoding utf8
        }
    }
} catch {
    "$(Get-Date -Format O) $($_.Exception.ToString())" |
        Out-File -FilePath $monitorPath -Append -Encoding utf8
} finally {
    $writer.Dispose()
    "$(Get-Date -Format O) Monitor stopped." |
        Out-File -FilePath $monitorPath -Append -Encoding utf8
}
