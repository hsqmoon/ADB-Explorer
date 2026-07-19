param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [int]$DurationSeconds = 360,

    [int]$SampleIntervalMs = 20
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WindowMotionNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out RECT rect);
}
'@

$processName = [System.IO.Path]::GetFileNameWithoutExtension($ExecutablePath)
$deadline = [DateTime]::UtcNow.AddSeconds(60)
$process = $null

while ([DateTime]::UtcNow -lt $deadline -and $null -eq $process) {
    $process = Get-Process -Name $processName -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $ExecutablePath } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1

    if ($null -eq $process) {
        Start-Sleep -Milliseconds 20
    }
}

if ($null -eq $process) {
    throw "ADB Explorer did not start within 60 seconds."
}

$processStartUtc = $process.StartTime.ToUniversalTime()
$endTime = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
$outputPath = Join-Path $OutputDirectory 'window-motion.csv'
$writer = [System.IO.StreamWriter]::new($outputPath, $false, [System.Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
$writer.WriteLine('UtcTime,SinceProcessStartMs,CursorX,CursorY,WindowLeft,WindowTop,WindowRight,WindowBottom')

try {
    while ([DateTime]::UtcNow -lt $endTime -and -not $process.HasExited) {
        $loopTimer = [System.Diagnostics.Stopwatch]::StartNew()
        $now = [DateTime]::UtcNow
        $process.Refresh()
        $window = $process.MainWindowHandle

        if ($window -ne [IntPtr]::Zero) {
            $point = New-Object WindowMotionNative+POINT
            $rect = New-Object WindowMotionNative+RECT
            if ([WindowMotionNative]::GetCursorPos([ref]$point) -and
                [WindowMotionNative]::GetWindowRect($window, [ref]$rect)) {
                $writer.WriteLine([string]::Format(
                    $culture,
                    '{0:o},{1:F1},{2},{3},{4},{5},{6},{7}',
                    $now,
                    ($now - $processStartUtc).TotalMilliseconds,
                    $point.X,
                    $point.Y,
                    $rect.Left,
                    $rect.Top,
                    $rect.Right,
                    $rect.Bottom))
            }
        }

        $remainingDelay = $SampleIntervalMs - $loopTimer.ElapsedMilliseconds
        if ($remainingDelay -gt 0) {
            Start-Sleep -Milliseconds $remainingDelay
        }
    }
} finally {
    $writer.Dispose()
}
