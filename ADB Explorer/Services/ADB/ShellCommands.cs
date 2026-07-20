using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

public static class ShellCommands
{
    private static readonly TimeSpan COMMAND_SCAN_TIMEOUT = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<(string DeviceId, long Sequence), byte> ActiveCommandScans = [];
    private static readonly SemaphoreSlim CommandScanGate = new(2, 2);

    public enum ShellCmd
    {
        am,
        cat,
        cp,
        df,
        dumpsys,
        echo,
        find,
        getprop,
        ip,
        mkdir,
        mv,
        pm,
        pwd,
        readlink,
        rm,
        stat,
        tar,
        touch,
        whoami,
    }

    public static string[] Commands => Enum.GetNames<ShellCmd>();

    public static ConcurrentDictionary<string, Dictionary<ShellCmd, string>> DeviceCommands { get; } = [];
    private static readonly ConcurrentDictionary<string, bool> DeviceFindPrintf = [];

    public static bool SupportsFindPrintf(string deviceID)
        => DeviceFindPrintf.GetValueOrDefault(deviceID);

    public static void RemoveDevice(string deviceID)
    {
        DeviceCommands.TryRemove(deviceID, out _);
        DeviceFindPrintf.TryRemove(deviceID, out _);
    }

    public static string TranslateCommand(string cmd) =>
        TranslateCommand(cmd, App.ActiveAdbDevice?.ID);

    public static string TranslateCommand(string cmd, string deviceId)
    {
        if (Enum.TryParse<ShellCmd>(cmd, out var enumCmd)
            && deviceId is not null
            && DeviceCommands.TryGetValue(deviceId, out var dict)
            && dict.TryGetValue(enumCmd, out var deviceCmd))
        {
            return deviceCmd;
        }

        return cmd;
    }

    internal static async Task FindCommandsAsync(
        LogicalDeviceViewModel device,
        long sequence,
        Func<bool> isCurrent,
        AdbCommandClient commandClient,
        CancellationToken cancellationToken)
    {
        string deviceID = device.ID;
        var scanKey = (deviceID, sequence);
        if (!ActiveCommandScans.TryAdd(scanKey, 0))
            return;

        bool gateAcquired = false;
        try
        {
            await CommandScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellation.CancelAfter(COMMAND_SCAN_TIMEOUT);

            string commandNames = string.Join(' ', Commands);
            string script = $"""
                for cmd in {commandNames}; do
                    resolved=$(command -v "$cmd" 2>/dev/null)
                    if [ -n "$resolved" ]; then
                        echo "$cmd=$resolved"
                    elif command -v busybox >/dev/null 2>&1 && busybox "$cmd" --help >/dev/null 2>&1; then
                        echo "$cmd=busybox $cmd"
                    fi
                done
                find_help=$(find --help 2>&1)
                case "$find_help" in
                    *'-printf FORMAT'*) echo '__FIND_PRINTF__=1' ;;
                esac
                """;
            string output = await commandClient.ExecuteShellAsync(
                device,
                script,
                cancellation.Token).ConfigureAwait(false);

            Dictionary<ShellCmd, string> deviceDict = [];
            bool findPrintf = false;
            foreach (string line in output.Split(ADBService.LINE_SEPARATORS, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('=', 2);
                if (parts.Length != 2)
                    continue;
                if (parts[0] == "__FIND_PRINTF__")
                {
                    findPrintf = parts[1] == "1";
                    continue;
                }
                if (Enum.TryParse(parts[0], true, out ShellCmd command))
                    deviceDict[command] = parts[1];
            }

            if (isCurrent())
            {
                DeviceFindPrintf[deviceID] = findPrintf;
                DeviceCommands[deviceID] = deviceDict;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "shell-commands.scan");
            throw;
        }
        finally
        {
            if (gateAcquired)
                CommandScanGate.Release();
            ActiveCommandScans.TryRemove(scanKey, out _);
        }
    }
}
