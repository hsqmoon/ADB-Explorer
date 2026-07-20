using ADB_Explorer.Models;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

namespace ADB_Explorer.Services;

internal sealed class AdbDeviceMonitorService
{
    private static readonly TimeSpan MIN_RETRY_DELAY = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MAX_RETRY_DELAY = TimeSpan.FromSeconds(30);

    private readonly DeviceRepository repository = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim probeGate = new(1, 1);
    private readonly object publishLock = new();
    private readonly Func<Action<IEnumerable<DeviceData>>, CancellationToken, Task> runMonitorSession;
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay;
    private long generation;

    public event Action<DeviceDelta> SnapshotChanged;

    public AdbDeviceMonitorService()
    {
        runMonitorSession = RunMonitorSessionAsync;
        retryDelay = Task.Delay;
    }

    internal AdbDeviceMonitorService(
        Func<Action<IEnumerable<DeviceData>>, CancellationToken, Task> runMonitorSession,
        Func<TimeSpan, CancellationToken, Task> retryDelay)
    {
        this.runMonitorSession = runMonitorSession;
        this.retryDelay = retryDelay;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var retryDelay = MIN_RETRY_DELAY;

        while (!cancellationToken.IsCancellationRequested)
        {
            int sessionHealthy = 0;
            long currentGeneration;
            lock (publishLock)
                currentGeneration = ++generation;

            try
            {
                await runMonitorSession(
                    devices =>
                    {
                        Volatile.Write(ref sessionHealthy, 1);
                        Publish(devices, currentGeneration);
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                    throw new InvalidOperationException("The ADB device monitor stopped unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                App.ReportBackgroundFailure(ex, nameof(AdbDeviceMonitorService));
                if (Volatile.Read(ref sessionHealthy) != 0)
                    retryDelay = MIN_RETRY_DELAY;
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
                await this.retryDelay(retryDelay + jitter, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, MAX_RETRY_DELAY.TotalMilliseconds));
            }
        }
    }

    private static async Task RunMonitorSessionAsync(
        Action<IEnumerable<DeviceData>> publish,
        CancellationToken cancellationToken)
    {
        if (!await ADBService.EnsureAdbServerAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Unable to start the ADB server.");

        using var socket = new AdbSocket(ADBService.AdbServerEndPoint);
        await socket.SendAdbRequestAsync("host:track-devices", cancellationToken).ConfigureAwait(false);
        await socket.ReadAdbResponseAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            var devices = await ReadDeviceSnapshotAsync(socket, cancellationToken).ConfigureAwait(false);
            App.RuntimeSettings.LastServerResponse = DateTime.Now;
            publish(devices);
        }
    }

    private static async Task<DeviceData[]> ReadDeviceSnapshotAsync(
        AdbSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[4];
        if (await socket.ReadAsync(lengthBuffer, cancellationToken).ConfigureAwait(false) != lengthBuffer.Length)
            throw new EndOfStreamException("The ADB device monitor connection closed.");

        int payloadLength = Convert.ToInt32(AdbClient.Encoding.GetString(lengthBuffer), 16);
        if (payloadLength == 0)
            return [];

        byte[] payload = new byte[payloadLength];
        if (await socket.ReadAsync(payload, cancellationToken).ConfigureAwait(false) != payload.Length)
            throw new EndOfStreamException("The ADB device monitor snapshot was incomplete.");

        return AdbClient.Encoding.GetString(payload)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new DeviceData(line.TrimEnd('\r')))
            .ToArray();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
        => await RefreshAsync(
            Volatile.Read(ref generation),
            cancellationToken).ConfigureAwait(false);

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        if (!await probeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AdbExplorerConst.ADB_POLL_COMMAND_TIMEOUT);
            using var socket = new AdbSocket(ADBService.AdbServerEndPoint);
            await socket.SendAdbRequestAsync("host:version", timeout.Token).ConfigureAwait(false);
            await socket.ReadAdbResponseAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(await socket.ReadStringAsync(timeout.Token).ConfigureAwait(false)))
                return;

            App.RuntimeSettings.LastServerResponse = DateTime.Now;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        catch
        { }
        finally
        {
            probeGate.Release();
        }
    }

    private async Task RefreshAsync(long currentGeneration, CancellationToken cancellationToken)
    {
        if (currentGeneration != Volatile.Read(ref generation))
            return;

        if (!await refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var devices = await new AdbClient(ADBService.AdbServerEndPoint)
                .GetDevicesAsync(cancellationToken)
                .ConfigureAwait(false);
            Publish(devices, currentGeneration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "device-monitor.refresh");
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public void Reset()
    {
        DeviceDelta delta;
        lock (publishLock)
        {
            generation++;
            delta = repository.Apply([]);
        }

        if (delta is not null)
            SnapshotChanged?.Invoke(delta);
    }

    private void Publish(IEnumerable<DeviceData> devices, long currentGeneration)
    {
        DeviceDelta delta;
        lock (publishLock)
        {
            if (currentGeneration != generation)
                return;

            delta = repository.Apply(devices);
        }

        if (delta is not null && currentGeneration == Volatile.Read(ref generation))
            SnapshotChanged?.Invoke(delta);
    }
}
