using System.Net;

namespace ADB_Explorer.Services.Terminal;

internal static class AdbTerminalTransport
{
    private static readonly TimeSpan CONNECTION_TIMEOUT = TimeSpan.FromSeconds(10);

    public static async Task<ITerminalTransport> ConnectAsync(
        IPEndPoint serverEndPoint,
        string deviceId,
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CONNECTION_TIMEOUT);

        try
        {
            return await AdbShellV2Transport.ConnectAsync(
                serverEndPoint,
                deviceId,
                columns,
                rows,
                timeout.Token).ConfigureAwait(false);
        }
        catch (IOException) when (!timeout.IsCancellationRequested)
        {
            return await AdbLegacyShellTransport.ConnectAsync(
                serverEndPoint,
                deviceId,
                columns,
                rows,
                timeout.Token).ConfigureAwait(false);
        }
    }
}
