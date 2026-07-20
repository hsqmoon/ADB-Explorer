param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot '..\ADB Explorer\bin\Release\net9.0-windows10.0.22621.0\ADB Explorer.exe'),

    [string]$OutputDirectory,

    [int]$DurationSeconds = 1800
)

$ErrorActionPreference = 'Stop'
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot ("acceptance-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

function Start-HiddenProcess([string]$fileName, [string]$arguments) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $fileName
    $startInfo.Arguments = $arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true

    return [System.Diagnostics.Process]::Start($startInfo)
}

$powershell = Join-Path $PSHOME 'powershell.exe'
function Start-Monitor([string]$scriptName, [bool]$includeExecutable) {
    $scriptPath = (Join-Path $PSScriptRoot $scriptName).Replace("'", "''")
    $outputPath = $OutputDirectory.Replace("'", "''")
    $command = "& '$scriptPath' -OutputDirectory '$outputPath' -DurationSeconds $DurationSeconds"
    if ($includeExecutable) {
        $executable = $ExecutablePath.Replace("'", "''")
        $command += " -ExecutablePath '$executable'"
    }

    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    return Start-HiddenProcess $powershell "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedCommand"
}

$processMonitor = Start-Monitor 'startup-reconnect-monitor.ps1' $true
$motionMonitor = Start-Monitor 'window-motion-monitor.ps1' $true
$systemMonitor = Start-Monitor 'system-performance-monitor.ps1' $false

$tracePath = Join-Path $OutputDirectory 'ui-events.nettrace'
$traceArguments = 'collect --providers ADBExplorer-Performance --output "{0}" -- "{1}"' -f $tracePath, $ExecutablePath
$trace = Start-HiddenProcess (Get-Command dotnet-trace).Source $traceArguments

Write-Output $OutputDirectory
try {
    $trace.WaitForExit()
    if ($trace.ExitCode -ne 0) {
        throw "dotnet-trace exited with code $($trace.ExitCode)."
    }
} finally {
    foreach ($monitor in @($processMonitor, $motionMonitor, $systemMonitor)) {
        if ($null -ne $monitor -and -not $monitor.HasExited) {
            $monitor.Kill()
            $monitor.WaitForExit()
        }
        if ($null -ne $monitor) {
            $monitor.Dispose()
        }
    }
    $trace.Dispose()
}
