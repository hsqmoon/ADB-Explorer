using ADB_Explorer.Models;
using ADB_Explorer.Services;
using AdvancedSharpAdbClient;

namespace ADB_Explorer.Helpers;

internal static class AdbHelper
{
    public static async Task<bool> CheckAdbVersion(CancellationToken cancellationToken)
    {
        string adbPath = string.IsNullOrEmpty(App.Settings.ManualAdbPath)
            ? AdbExplorerConst.ADB_PROCESS
            : App.Settings.ManualAdbPath;

        await ADBService.VerifyAdbVersionAsync(adbPath, cancellationToken).ConfigureAwait(false);

        return App.RuntimeSettings.AdbVersion >= AdbExplorerConst.MIN_ADB_VERSION;
    }

    public static void MdnsCheck()
    {
        _ = MdnsCheckAsync();
    }

    private static async Task MdnsCheckAsync()
    {
        try
        {
            var checkTask = Task.Run(ADBService.CheckMDNS);
            while (!checkTask.IsCompleted)
            {
                if (Application.Current is App app)
                    app.EnqueueUiLatest("mdns.progress", "mdns.progress", App.MdnsService.UpdateProgress);

                await Task.Delay(AdbExplorerConst.MDNS_STATUS_UPDATE_INTERVAL).ConfigureAwait(false);
            }

            bool running = await checkTask.ConfigureAwait(false);
            if (Application.Current is App currentApp)
            {
                await currentApp.EnqueueUiAsync(
                    "mdns.state",
                    () => App.MdnsService.State = running ? MDNS.MdnsState.Running : MDNS.MdnsState.NotRunning);
            }
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "mdns.check");
        }
    }

    public static void InitializeMdns()
    {
        ADBService.IsMdnsEnabled = App.Settings.EnableMdns;
        App.QrClass = App.Settings.EnableMdns ? new() : null;

        if (!App.Settings.EnableMdns)
            App.MdnsService.State = MDNS.MdnsState.Disabled;
    }

    public static void EnableMdns() => _ = EnableMdnsAsync();

    private static async Task EnableMdnsAsync()
    {
        try
        {
            ADBService.IsMdnsEnabled = App.Settings.EnableMdns;
            if (App.Settings.EnableMdns)
            {
                App.QrClass = new();
                return;
            }

            if (App.MdnsService.State is MDNS.MdnsState.Running)
            {
                var result = await DialogService.ShowConfirmation(
                    Strings.Resources.S_DISABLE_MDNS,
                    Strings.Resources.S_DISABLE_MDNS_TITLE,
                    Strings.Resources.S_RESTART_ADB_NOW,
                    cancelText: Strings.Resources.S_RESTART_LATER,
                    icon: DialogService.DialogIcon.Informational);

                if (result.Item1 is ContentDialogResult.Primary)
                    await Task.Run(() => ADBService.KillAdbServer());
            }

            App.QrClass = null;
            App.MdnsService.State = MDNS.MdnsState.Disabled;
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "mdns.enable");
        }
    }

    public static string ReadFile(ADBService.AdbDevice device, string path)
    {
        using MemoryStream stream = new();
        using SyncService service = new(ADBService.AdbServerEndPoint, device.Device.DeviceData);

        service.Pull(path, stream);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static void WriteFile(ADBService.AdbDevice device, string path, string content)
    {
        using MemoryStream stream = new();
        using StreamWriter writer = new(stream);
        writer.Write(content);
        writer.Flush();
        stream.Position = 0;

        using SyncService service = new(ADBService.AdbServerEndPoint, device.Device.DeviceData);
        service.Push(stream, path, (UnixFileMode)0x1ED, DateTime.Now); // 0x1ED = 0777 in octal
    }
}
