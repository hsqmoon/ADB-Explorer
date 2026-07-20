using ADB_Explorer.Models;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;
using AdvancedSharpAdbClient.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using static ADB_Explorer.Models.AbstractDevice;

namespace ADB_Test;

[TestClass]
public class DeviceMetadataServiceTests
{
    [STATestMethod]
    public void IndependentMetadataKindsCanRunConcurrently()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var entered = new ConcurrentDictionary<string, TaskCompletionSource>();
        foreach (string kind in new[] { "commands", "root", "ip", "battery" })
            entered[kind] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new DeviceMetadataService(
            scheduler,
            CancellationToken.None,
            async (_, _, _, _) =>
            {
                entered["commands"].TrySetResult();
                await release.Task;
            },
            async (_, _, _) =>
            {
                entered["root"].TrySetResult();
                await release.Task;
                return RootStatus.Disabled;
            },
            async (_, _) =>
            {
                entered["ip"].TrySetResult();
                await release.Task;
                return "192.168.0.2";
            },
            async (_, _) =>
            {
                entered["battery"].TrySetResult();
                await release.Task;
                return new Dictionary<string, string>();
            });
        var model = LogicalDevice.New(new DeviceData
        {
            Serial = "device-1",
            State = DeviceState.Online,
        });
        var device = new LogicalDeviceViewModel(model, false);

        service.ApplySnapshot(CreateSnapshot(1), [device], autoRoot: false, pollBattery: true);
        PumpUntil(
            () => entered.Values.All(signal => signal.Task.IsCompleted),
            TimeSpan.FromSeconds(5));

        release.TrySetResult();
    }

    [STATestMethod]
    public void SupersededMetadataCannotWriteIntoReconnectedDevice()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        using ManualResetEventSlim firstEntered = new();
        using ManualResetEventSlim secondEntered = new();
        using ManualResetEventSlim releaseFirst = new();
        int rootReadCount = 0;
        CancellationToken firstGenerationToken = default;
        using var service = new DeviceMetadataService(
            scheduler,
            CancellationToken.None,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, token) =>
            {
                if (Interlocked.Increment(ref rootReadCount) == 1)
                {
                    firstGenerationToken = token;
                    firstEntered.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(5));
                    return Task.FromResult<RootStatus?>(RootStatus.Enabled);
                }

                secondEntered.Set();
                return Task.FromResult<RootStatus?>(RootStatus.Disabled);
            },
            (_, _) => Task.FromResult<string>(null),
            (_, _) => Task.FromResult<Dictionary<string, string>>(null));
        var model = LogicalDevice.New(new DeviceData
        {
            Serial = "device-1",
            State = DeviceState.Online,
        });
        var device = new LogicalDeviceViewModel(model, false);
        List<RootStatus> observedRoots = [];
        device.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogicalDeviceViewModel.Root))
                observedRoots.Add(device.Root);
        };

        service.ApplySnapshot(CreateSnapshot(1), [device], autoRoot: false, pollBattery: false);
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(5)));

        service.ApplySnapshot(CreateSnapshot(2), [device], autoRoot: false, pollBattery: false);
        Assert.IsTrue(firstGenerationToken.IsCancellationRequested);
        Assert.IsTrue(secondEntered.Wait(TimeSpan.FromSeconds(5)));
        releaseFirst.Set();

        PumpUntil(() => device.Root is RootStatus.Disabled, TimeSpan.FromSeconds(5));

        CollectionAssert.DoesNotContain(observedRoots, RootStatus.Enabled);
        Assert.AreEqual(RootStatus.Disabled, device.Root);
    }

    [STATestMethod]
    public void UnsupportedMetadataIsProbedOncePerDeviceGeneration()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        int rootReads = 0;
        int ipReads = 0;
        int batteryReads = 0;
        using var service = new DeviceMetadataService(
            scheduler,
            CancellationToken.None,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) =>
            {
                Interlocked.Increment(ref rootReads);
                return Task.FromResult<RootStatus?>(null);
            },
            (_, _) =>
            {
                Interlocked.Increment(ref ipReads);
                return Task.FromResult<string>(null);
            },
            (_, _) =>
            {
                Interlocked.Increment(ref batteryReads);
                return Task.FromResult(new Dictionary<string, string>());
            });
        var device = new LogicalDeviceViewModel(LogicalDevice.New(new DeviceData
        {
            Serial = "device-1",
            State = DeviceState.Online,
        }), false);

        service.ApplySnapshot(CreateSnapshot(1), [device], autoRoot: false, pollBattery: true);
        PumpUntil(
            () => Volatile.Read(ref rootReads) == 1
                && Volatile.Read(ref ipReads) == 1
                && Volatile.Read(ref batteryReads) == 1,
            TimeSpan.FromSeconds(5));
        var settle = Task.Delay(100);
        PumpUntil(() => settle.IsCompleted, TimeSpan.FromSeconds(1));

        for (int index = 0; index < 20; index++)
            service.Refresh([device], autoRoot: false, pollBattery: true);
        var retryWindow = Task.Delay(100);
        PumpUntil(() => retryWindow.IsCompleted, TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, rootReads);
        Assert.AreEqual(1, ipReads);
        Assert.AreEqual(1, batteryReads);
    }

    private static DeviceDelta CreateSnapshot(long sequence) => new(
        sequence,
        [],
        [],
        [],
        [],
        new Dictionary<string, long>(StringComparer.Ordinal) { ["device-1"] = sequence },
        Stopwatch.GetTimestamp());

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        timer.Tick += (_, _) =>
        {
            if (condition() || DateTime.UtcNow >= deadline)
                frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();

        Assert.IsTrue(condition(), "The metadata update did not finish before the timeout.");
    }
}
