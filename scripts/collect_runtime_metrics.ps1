param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [int]$DurationSeconds = 30,
    [int]$IntervalMilliseconds = 100,
    [int]$WaitForProcessSeconds = 15
)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class RuntimeMetricsNativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessIoCounters(IntPtr processHandle, out IO_COUNTERS counters);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}
'@

$executablePath = [IO.Path]::GetFullPath($ExecutablePath)
$waitDeadline = [DateTime]::UtcNow.AddSeconds($WaitForProcessSeconds)
$process = $null

do {
    $process = [Diagnostics.Process]::GetProcesses() | Where-Object {
        try {
            [string]::Equals($_.MainModule.FileName, $executablePath, [StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    } | Select-Object -First 1

    if ($null -eq $process) {
        Start-Sleep -Milliseconds 50
    }
} while ($null -eq $process -and [DateTime]::UtcNow -lt $waitDeadline)

if ($null -eq $process) {
    throw "Process did not start within $WaitForProcessSeconds seconds: $executablePath"
}

$rows = [Collections.Generic.List[object]]::new()
$logicalProcessorCount = [Environment]::ProcessorCount
$started = [Diagnostics.Stopwatch]::StartNew()
$previousSampleMilliseconds = 0.0
$previousAppCpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
$previousReadBytes = [UInt64]0
$previousWriteBytes = [UInt64]0
$previousAdbCpu = @{}
$ioCounters = [RuntimeMetricsNativeMethods+IO_COUNTERS]::new()

if ([RuntimeMetricsNativeMethods]::GetProcessIoCounters($process.Handle, [ref]$ioCounters)) {
    $previousReadBytes = $ioCounters.ReadTransferCount
    $previousWriteBytes = $ioCounters.WriteTransferCount
}

while ($started.Elapsed.TotalSeconds -lt $DurationSeconds -and -not $process.HasExited) {
    $sampleStarted = [Diagnostics.Stopwatch]::StartNew()
    $process.Refresh()

    $elapsedMilliseconds = [Math]::Max(1.0, $started.Elapsed.TotalMilliseconds - $previousSampleMilliseconds)
    $appCpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
    $appCpuPercent = ($appCpuMilliseconds - $previousAppCpuMilliseconds) / $elapsedMilliseconds / $logicalProcessorCount * 100.0

    $readBytesPerSecond = 0.0
    $writeBytesPerSecond = 0.0
    if ([RuntimeMetricsNativeMethods]::GetProcessIoCounters($process.Handle, [ref]$ioCounters)) {
        $readBytesPerSecond = ($ioCounters.ReadTransferCount - $previousReadBytes) * 1000.0 / $elapsedMilliseconds
        $writeBytesPerSecond = ($ioCounters.WriteTransferCount - $previousWriteBytes) * 1000.0 / $elapsedMilliseconds
        $previousReadBytes = $ioCounters.ReadTransferCount
        $previousWriteBytes = $ioCounters.WriteTransferCount
    }

    $adbCpuMilliseconds = 0.0
    $nextAdbCpu = @{}
    $adbProcesses = [Diagnostics.Process]::GetProcessesByName('adb')
    foreach ($adbProcess in $adbProcesses) {
        try {
            $cpuMilliseconds = $adbProcess.TotalProcessorTime.TotalMilliseconds
            $previousCpuMilliseconds = if ($previousAdbCpu.ContainsKey($adbProcess.Id)) { $previousAdbCpu[$adbProcess.Id] } else { $cpuMilliseconds }
            $adbCpuMilliseconds += [Math]::Max(0.0, $cpuMilliseconds - $previousCpuMilliseconds)
            $nextAdbCpu[$adbProcess.Id] = $cpuMilliseconds
        }
        catch { }
        finally {
            $adbProcess.Dispose()
        }
    }
    $previousAdbCpu = $nextAdbCpu

    $uiResponseMilliseconds = $null
    $uiTimedOut = $false
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
        $uiProbe = [Diagnostics.Stopwatch]::StartNew()
        $messageResult = [IntPtr]::Zero
        $sendResult = [RuntimeMetricsNativeMethods]::SendMessageTimeout(
            $process.MainWindowHandle,
            0,
            [IntPtr]::Zero,
            [IntPtr]::Zero,
            2,
            50,
            [ref]$messageResult)
        $uiProbe.Stop()
        $uiResponseMilliseconds = $uiProbe.Elapsed.TotalMilliseconds
        $uiTimedOut = $sendResult -eq [IntPtr]::Zero
    }

    $rows.Add([pscustomobject]@{
        Timestamp = [DateTime]::UtcNow.ToString('O')
        ElapsedMilliseconds = [Math]::Round($started.Elapsed.TotalMilliseconds, 1)
        AppCpuPercent = [Math]::Round($appCpuPercent, 2)
        AdbCpuPercent = [Math]::Round($adbCpuMilliseconds / $elapsedMilliseconds / $logicalProcessorCount * 100.0, 2)
        WorkingSetMB = [Math]::Round($process.WorkingSet64 / 1MB, 2)
        PrivateMemoryMB = [Math]::Round($process.PrivateMemorySize64 / 1MB, 2)
        ThreadCount = $process.Threads.Count
        HandleCount = $process.HandleCount
        ReadMBps = [Math]::Round($readBytesPerSecond / 1MB, 3)
        WriteMBps = [Math]::Round($writeBytesPerSecond / 1MB, 3)
        UiResponseMilliseconds = if ($null -eq $uiResponseMilliseconds) { $null } else { [Math]::Round($uiResponseMilliseconds, 2) }
        UiTimedOut = $uiTimedOut
        AdbProcessCount = $adbProcesses.Count
    })

    $previousSampleMilliseconds = $started.Elapsed.TotalMilliseconds
    $previousAppCpuMilliseconds = $appCpuMilliseconds
    $remainingDelay = $IntervalMilliseconds - $sampleStarted.ElapsedMilliseconds
    if ($remainingDelay -gt 0) {
        Start-Sleep -Milliseconds $remainingDelay
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrEmpty($outputDirectory)) {
    [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}
$rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding UTF8
