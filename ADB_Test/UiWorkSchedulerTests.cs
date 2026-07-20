using ADB_Explorer.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ADB_Test;

[TestClass]
public class UiWorkSchedulerTests
{
    [STATestMethod]
    public void FifoWorkPreservesOrder()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        List<int> actual = [];
        var work = Enumerable.Range(0, 32)
            .Select(index => scheduler.EnqueueAsync("test.fifo", () => actual.Add(index)).AsTask())
            .ToArray();

        PumpUntil(Task.WhenAll(work));

        CollectionAssert.AreEqual(Enumerable.Range(0, 32).ToArray(), actual);
    }

    [STATestMethod]
    public void LatestWorkCoalescesByKey()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        int actual = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        scheduler.EnqueueLatest("device-1", "test.latest", () => actual = 1);
        scheduler.EnqueueLatest("device-1", "test.latest", () => actual = 2);
        scheduler.EnqueueLatest("device-1", "test.latest", () =>
        {
            actual = 3;
            completed.SetResult();
        });

        PumpUntil(completed.Task);

        Assert.AreEqual(3, actual);
    }

    [STATestMethod]
    public void FailedWorkDoesNotStopFollowingWork()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        bool followingWorkRan = false;

        var failed = scheduler.EnqueueAsync("test.failure", () => throw new InvalidOperationException()).AsTask();
        var following = scheduler.EnqueueAsync("test.following", () => followingWorkRan = true).AsTask();

        PumpUntil(Task.WhenAll(failed.ContinueWith(_ => { }), following));

        Assert.IsTrue(failed.IsFaulted);
        Assert.IsTrue(followingWorkRan);
    }

    [STATestMethod]
    public void SchedulerYieldsToDispatcherBetweenTimeSlices()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        int executed = 0;
        int executedAtMarker = -1;
        var work = Enumerable.Range(0, 40)
            .Select(_ => scheduler.EnqueueAsync("test.slice", () =>
            {
                var start = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromMilliseconds(1))
                { }
                executed++;
            }).AsTask())
            .ToArray();
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            executedAtMarker = executed;
            marker.TrySetResult();
        }), DispatcherPriority.Input);

        PumpUntil(marker.Task);

        Assert.AreEqual(1, executedAtMarker);
        PumpUntil(Task.WhenAll(work));
    }

    [STATestMethod]
    public void BoundedFifoAppliesBackpressureWithoutDroppingWork()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        int executed = 0;
        var work = Enumerable.Range(0, 600)
            .Select(_ => scheduler.EnqueueAsync("test.backpressure", () => executed++).AsTask())
            .ToArray();

        Assert.IsTrue(work.Skip(512).Any(task => !task.IsCompleted));
        PumpUntil(Task.WhenAll(work));

        Assert.AreEqual(600, executed);
    }

    private static void PumpUntil(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);

        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
