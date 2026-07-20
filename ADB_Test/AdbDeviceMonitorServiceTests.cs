using ADB_Explorer.Services;
using AdvancedSharpAdbClient.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ADB_Test;

[TestClass]
public class AdbDeviceMonitorServiceTests
{
    [TestMethod]
    public async Task DuplicateEventsAreIgnoredAndFailedSessionRestarts()
    {
        int attempts = 0;
        var secondSession = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AdbDeviceMonitorService(
            async (publish, cancellationToken) =>
            {
                int attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    var online = new DeviceData { Serial = "device-1", State = DeviceState.Online };
                    publish([online]);
                    publish([online]);
                    throw new InvalidOperationException("simulated monitor disconnect");
                }

                publish([new DeviceData { Serial = "device-1", State = DeviceState.Offline }]);
                secondSession.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            (_, _) => Task.CompletedTask);
        List<DeviceDelta> deltas = [];
        service.SnapshotChanged += deltas.Add;
        using var cancellation = new CancellationTokenSource();

        Task run = service.RunAsync(cancellation.Token);
        await secondSession.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, attempts);
        Assert.HasCount(2, deltas);
        Assert.HasCount(1, deltas[0].Added);
        Assert.HasCount(1, deltas[1].Updated);
        Assert.AreEqual(DeviceState.Offline, deltas[1].Updated[0].DeviceData.State);
    }
}
