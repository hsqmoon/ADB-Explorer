using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Receivers;

namespace ADB_Explorer.Services;

internal sealed class AdbCommandClient
{
    private readonly Lazy<AdbClient> client = new(
        () => new(ADBService.AdbServerEndPoint),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<string> ExecuteShellAsync(
        LogicalDeviceViewModel device,
        string command,
        CancellationToken cancellationToken)
    {
        var receiver = new ConsoleOutputReceiver();
        await client.Value.ExecuteRemoteCommandAsync(
            command,
            device.DeviceData,
            receiver,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        App.RuntimeSettings.LastServerResponse = DateTime.Now;
        return receiver.ToString();
    }
}
