using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using System.Threading.Channels;
using static ADB_Explorer.Models.AbstractDevice;

namespace ADB_Explorer.Services;

internal sealed class DeviceMetadataService : IDisposable
{
    private const int QUEUE_CAPACITY = 128;
    private const int WORKER_COUNT = 4;

    private readonly IUiWorkScheduler uiScheduler;
    private readonly Channel<MetadataRequest> requests;
    private readonly ConcurrentDictionary<string, long> deviceSequences = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string DeviceId, long Sequence), CancellationTokenSource> deviceLifetimes = [];
    private readonly ConcurrentDictionary<(string DeviceId, long Sequence, MetadataKind Kind), byte> queuedRequests = [];
    private readonly ConcurrentDictionary<(string DeviceId, long Sequence, MetadataKind Kind), byte> activeRequests = [];
    private readonly ConcurrentDictionary<(string DeviceId, long Sequence, MetadataKind Kind), byte> unsupportedRequests = [];
    private readonly ConcurrentDictionary<(string DeviceId, long Sequence), byte> pendingCommandScans = [];
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken lifetimeToken;
    private readonly Task[] workers;
    private readonly Func<LogicalDeviceViewModel, long, Func<bool>, CancellationToken, Task> commandScanner;
    private readonly Func<LogicalDeviceViewModel, bool, CancellationToken, Task<RootStatus?>> rootReader;
    private readonly Func<LogicalDeviceViewModel, CancellationToken, Task<string>> ipReader;
    private readonly Func<LogicalDeviceViewModel, CancellationToken, Task<Dictionary<string, string>>> batteryReader;
    private int disposed;

    public DeviceMetadataService(IUiWorkScheduler uiScheduler, CancellationToken cancellationToken)
        : this(uiScheduler, cancellationToken, null, null, null, null)
    { }

    internal DeviceMetadataService(
        IUiWorkScheduler uiScheduler,
        CancellationToken cancellationToken,
        Func<LogicalDeviceViewModel, long, Func<bool>, CancellationToken, Task> commandScanner,
        Func<LogicalDeviceViewModel, bool, CancellationToken, Task<RootStatus?>> rootReader,
        Func<LogicalDeviceViewModel, CancellationToken, Task<string>> ipReader,
        Func<LogicalDeviceViewModel, CancellationToken, Task<Dictionary<string, string>>> batteryReader)
    {
        this.uiScheduler = uiScheduler;
        var commandClient = new AdbCommandClient();
        this.commandScanner = commandScanner ?? ((device, sequence, isCurrent, token) =>
            ShellCommands.FindCommandsAsync(device, sequence, isCurrent, commandClient, token));
        this.rootReader = rootReader ?? ((device, autoRoot, token) =>
            ReadRootAsync(device, autoRoot, commandClient, token));
        this.ipReader = ipReader ?? ((device, token) =>
            ReadIpAddressAsync(device, commandClient, token));
        this.batteryReader = batteryReader ?? ((device, token) =>
            ReadBatteryAsync(device, commandClient, token));
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetimeToken = lifetime.Token;
        requests = Channel.CreateBounded<MetadataRequest>(new BoundedChannelOptions(QUEUE_CAPACITY)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        workers = Enumerable.Range(0, WORKER_COUNT)
            .Select(_ => RunWorkerAsync(lifetime.Token))
            .ToArray();
    }

    public void ApplySnapshot(
        DeviceDelta delta,
        IEnumerable<LogicalDeviceViewModel> devices,
        bool autoRoot,
        bool pollBattery)
    {
        var currentIds = delta.DeviceSequences.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var id in deviceSequences.Keys.Where(id => !currentIds.Contains(id)))
        {
            deviceSequences.TryRemove(id, out _);
            CancelDeviceLifetimes(id);
            foreach (var key in pendingCommandScans.Keys.Where(key => key.DeviceId == id))
                pendingCommandScans.TryRemove(key, out _);
            foreach (var key in unsupportedRequests.Keys.Where(key => key.DeviceId == id))
                unsupportedRequests.TryRemove(key, out _);
            ShellCommands.RemoveDevice(id);
        }

        foreach (var device in devices)
        {
            if (!delta.DeviceSequences.TryGetValue(device.ID, out long sequence)
                || deviceSequences.TryGetValue(device.ID, out long previousSequence)
                    && previousSequence == sequence)
            {
                continue;
            }

            deviceSequences[device.ID] = sequence;
            CancelDeviceLifetimes(device.ID, sequence);
            var deviceLifetime = deviceLifetimes.GetOrAdd(
                (device.ID, sequence),
                _ => CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken));
            foreach (var key in pendingCommandScans.Keys.Where(key => key.DeviceId == device.ID))
                pendingCommandScans.TryRemove(key, out _);
            foreach (var key in unsupportedRequests.Keys.Where(key => key.DeviceId == device.ID))
                unsupportedRequests.TryRemove(key, out _);
            ShellCommands.RemoveDevice(device.ID);
            if (device.Status is not DeviceStatus.Ok)
                continue;

            pendingCommandScans[(device.ID, sequence)] = 0;
            QueueMetadata(
                device,
                sequence,
                autoRoot,
                pollBattery,
                includeCommandScan: true,
                cancellationToken: deviceLifetime.Token);
        }
    }

    public void Refresh(
        IEnumerable<LogicalDeviceViewModel> devices,
        bool autoRoot,
        bool pollBattery)
    {
        foreach (var device in devices.Where(device => device.Status is DeviceStatus.Ok))
        {
            if (!deviceSequences.TryGetValue(device.ID, out long sequence))
                continue;

            var deviceLifetime = deviceLifetimes.GetOrAdd(
                (device.ID, sequence),
                _ => CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken));
            QueueMetadata(
                device,
                sequence,
                autoRoot,
                pollBattery,
                pendingCommandScans.ContainsKey((device.ID, sequence)),
                deviceLifetime.Token);
        }
    }

    private void QueueMetadata(
        LogicalDeviceViewModel device,
        long sequence,
        bool autoRoot,
        bool pollBattery,
        bool includeCommandScan,
        CancellationToken cancellationToken)
    {
        if (includeCommandScan)
            Queue(new(device, sequence, autoRoot, MetadataKind.Commands, cancellationToken));
        if (device.Root is RootStatus.Unchecked)
            Queue(new(device, sequence, autoRoot, MetadataKind.Root, cancellationToken));
        if (device.Type is DeviceType.Service or DeviceType.Local && !device.IsIpAddressValid)
            Queue(new(device, sequence, autoRoot, MetadataKind.IpAddress, cancellationToken));
        if (pollBattery)
            Queue(new(device, sequence, autoRoot, MetadataKind.Battery, cancellationToken));
    }

    private void CancelDeviceLifetimes(string deviceId, long? exceptSequence = null)
    {
        foreach (var entry in deviceLifetimes.Where(entry =>
            entry.Key.DeviceId == deviceId && entry.Key.Sequence != exceptSequence))
        {
            if (!deviceLifetimes.TryRemove(entry.Key, out var cancellation))
                continue;

            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void Queue(MetadataRequest request)
    {
        var key = (request.Device.ID, request.Sequence, request.Kind);
        if (unsupportedRequests.ContainsKey(key)
            || activeRequests.ContainsKey(key)
            || !queuedRequests.TryAdd(key, 0))
            return;

        if (!requests.Writer.TryWrite(request))
            queuedRequests.TryRemove(key, out _);
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var requestKey = (request.Device.ID, request.Sequence, request.Kind);
                queuedRequests.TryRemove(requestKey, out _);
                if (unsupportedRequests.ContainsKey(requestKey)
                    || !IsCurrent(request)
                    || !activeRequests.TryAdd(requestKey, 0))
                    continue;

                try
                {
                    switch (request.Kind)
                    {
                    case MetadataKind.Commands:
                        await commandScanner(
                            request.Device,
                            request.Sequence,
                            () => IsCurrent(request),
                            request.CancellationToken).ConfigureAwait(false);
                        if (IsCurrent(request))
                            pendingCommandScans.TryRemove((request.Device.ID, request.Sequence), out _);
                        break;

                    case MetadataKind.Root when request.Device.Root is RootStatus.Unchecked:
                        RootStatus? rootStatus = await rootReader(
                            request.Device,
                            request.AutoRoot,
                            request.CancellationToken).ConfigureAwait(false);
                        if (rootStatus is null)
                        {
                            unsupportedRequests[requestKey] = 0;
                        }
                        else if (IsCurrent(request))
                        {
                            uiScheduler.EnqueueLatest(
                                $"device.metadata.root.{request.Device.ID}",
                                "device.metadata.root",
                                () =>
                                {
                                    if (IsCurrent(request))
                                        request.Device.SetRootStatus(rootStatus.Value);
                                });
                        }
                        break;

                    case MetadataKind.IpAddress
                        when request.Device.Type is DeviceType.Service or DeviceType.Local
                            && !request.Device.IsIpAddressValid:
                        string ipAddress = await ipReader(
                            request.Device,
                            request.CancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(ipAddress))
                        {
                            unsupportedRequests[requestKey] = 0;
                        }
                        else if (IsCurrent(request))
                        {
                            uiScheduler.EnqueueLatest(
                                $"device.metadata.ip.{request.Device.ID}",
                                "device.metadata.ip",
                                () =>
                                {
                                    if (IsCurrent(request))
                                        request.Device.SetIpAddress(ipAddress);
                                });
                        }
                        break;

                    case MetadataKind.Battery:
                        var batteryInfo = await batteryReader(
                            request.Device,
                            request.CancellationToken).ConfigureAwait(false);
                        if (batteryInfo is null || batteryInfo.Count == 0)
                        {
                            unsupportedRequests[requestKey] = 0;
                        }
                        else if (IsCurrent(request))
                        {
                            uiScheduler.EnqueueLatest(
                                $"device.metadata.battery.{request.Device.ID}",
                                "device.metadata.battery",
                                () =>
                                {
                                    if (IsCurrent(request))
                                        request.Device.ApplyBatteryInfo(batteryInfo);
                                });
                        }
                        break;
                    }
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested || request.CancellationToken.IsCancellationRequested)
                { }
                catch (Exception ex)
                {
                    App.ReportBackgroundFailure(ex, $"device-metadata.{request.Device.ID}");
                }
                finally
                {
                    activeRequests.TryRemove(requestKey, out _);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "device-metadata.worker");
        }
    }

    private bool IsCurrent(MetadataRequest request) =>
        !lifetimeToken.IsCancellationRequested
        && !request.CancellationToken.IsCancellationRequested
        && request.Device.Status is DeviceStatus.Ok
        && deviceSequences.TryGetValue(request.Device.ID, out long sequence)
        && sequence == request.Sequence;

    private static async Task<RootStatus?> ReadRootAsync(
        LogicalDeviceViewModel device,
        bool autoRoot,
        AdbCommandClient commandClient,
        CancellationToken cancellationToken)
    {
        if (autoRoot)
        {
            return await Task.Run(
                () => device.ChangeRoot(true, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        const string unsupported = "__ADB_EXPLORER_UNSUPPORTED__";
        string output = await commandClient.ExecuteShellAsync(
            device,
            $"id -u 2>/dev/null || echo {unsupported}",
            cancellationToken).ConfigureAwait(false);
        string identity = output.Trim();
        return identity.Length == 0 || identity.Contains(unsupported, StringComparison.Ordinal)
            ? null
            : identity is "0" or "root" ? RootStatus.Enabled : RootStatus.Disabled;
    }

    private static async Task<string> ReadIpAddressAsync(
        LogicalDeviceViewModel device,
        AdbCommandClient commandClient,
        CancellationToken cancellationToken)
    {
        string output = await commandClient.ExecuteShellAsync(
            device,
            "ip -f inet addr show wlan0 2>/dev/null || true",
            cancellationToken).ConfigureAwait(false);
        var match = AdbRegEx.RE_DEVICE_WLAN_INET().Match(output);
        return match.Success ? match.Groups["IP"].Value : null;
    }

    private static async Task<Dictionary<string, string>> ReadBatteryAsync(
        LogicalDeviceViewModel device,
        AdbCommandClient commandClient,
        CancellationToken cancellationToken)
    {
        const string unsupported = "__ADB_EXPLORER_UNSUPPORTED__";
        string output = await commandClient.ExecuteShellAsync(
            device,
            $"dumpsys battery 2>/dev/null || echo {unsupported}",
            cancellationToken).ConfigureAwait(false);
        if (output.Contains(unsupported, StringComparison.Ordinal))
            return null;

        return output.Split(ADBService.LINE_SEPARATORS, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0].Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.Ordinal);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        requests.Writer.TryComplete();
        lifetime.Cancel();
        foreach (var entry in deviceLifetimes)
        {
            if (!deviceLifetimes.TryRemove(entry.Key, out var cancellation))
                continue;

            cancellation.Cancel();
            cancellation.Dispose();
        }
        _ = FinishDisposeAsync();
    }

    private async Task FinishDisposeAsync()
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { }
        finally
        {
            lifetime.Dispose();
        }
    }

    private sealed record MetadataRequest(
        LogicalDeviceViewModel Device,
        long Sequence,
        bool AutoRoot,
        MetadataKind Kind,
        CancellationToken CancellationToken);

    private enum MetadataKind
    {
        Commands,
        Root,
        IpAddress,
        Battery,
    }
}
