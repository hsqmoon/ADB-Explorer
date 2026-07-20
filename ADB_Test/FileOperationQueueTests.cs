using ADB_Explorer.Models;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;
using AdvancedSharpAdbClient.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ADB_Test;

[TestClass]
public class FileOperationQueueTests
{
    [STATestMethod]
    public void LargeBatchCompletesOnlyAfterEveryOperationIsPublishedWithoutReset()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var queue = new FileOperationQueue(scheduler) { IsAutoPlayOn = false };
        var model = LogicalDevice.New(new DeviceData
        {
            Serial = "queue-device",
            State = DeviceState.Online,
        });
        var device = new ADBService.AdbDevice(new LogicalDeviceViewModel(model, false));
        var operations = Enumerable.Range(0, 1_000)
            .Select(index => (FileOperation)new TestOperation(device, index))
            .ToArray();
        int resetCount = 0;
        queue.Operations.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Reset)
                resetCount++;
        };

        Task completion = queue.AddOperationsAsync(operations);

        Assert.IsFalse(completion.IsCompleted);
        Assert.IsEmpty(queue.Operations);
        PumpUntil(completion);

        Assert.HasCount(1_000, queue.Operations);
        Assert.AreEqual(0, resetCount);
    }

    [STATestMethod]
    public void ArchivingLargeHistoryIsProgressiveAndKeepsTheConfiguredLimit()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var queue = new FileOperationQueue(scheduler) { IsAutoPlayOn = false };
        var model = LogicalDevice.New(new DeviceData
        {
            Serial = "archive-device",
            State = DeviceState.Online,
        });
        var device = new ADBService.AdbDevice(new LogicalDeviceViewModel(model, false));
        var operations = Enumerable.Range(0, 1_000)
            .Select(index => (FileOperation)new TestOperation(device, index))
            .ToArray();
        int resetCount = 0;
        queue.Operations.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Reset)
                resetCount++;
        };

        PumpUntil(queue.AddOperationsAsync(operations));
        queue.MoveOperationsToPast(includeAll: true);
        PumpUntil(
            () => queue.Operations.Count == 500
                && queue.Operations.All(operation => operation.IsPastOp),
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, resetCount);
    }

    [STATestMethod]
    public void RemovingWaitingRowsNeverSkipsTheRemainingOperations()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var queue = new FileOperationQueue(
            scheduler,
            allowMultiOperation: () => true)
        {
            IsAutoPlayOn = false,
        };
        var model = LogicalDevice.New(new DeviceData
        {
            Serial = "queue-removal-device",
            State = DeviceState.Online,
        });
        var device = new ADBService.AdbDevice(new LogicalDeviceViewModel(model, false));
        List<TestOperation> started = [];
        var operations = Enumerable.Range(0, 12)
            .Select(index => new TestOperation(device, index, operation => started.Add(operation)))
            .ToArray();
        int[] removedIndices = [1, 3, 5];

        PumpUntil(queue.AddOperationsAsync(operations));
        PumpUntil(queue.RemoveOperationsAsync(removedIndices.Select(index => operations[index])));
        queue.IsAutoPlayOn = true;
        queue.Start();

        HashSet<TestOperation> completed = [];
        while (completed.Count < operations.Length - removedIndices.Length)
        {
            var running = started.Where(operation => !completed.Contains(operation)).ToArray();
            Assert.IsNotEmpty(running);
            foreach (var operation in running)
            {
                completed.Add(operation);
                operation.Complete();
            }
        }

        CollectionAssert.AreEqual(
            operations.Where(operation => !removedIndices.Contains(operation.Index)).Select(operation => operation.Index).ToArray(),
            started.Select(operation => operation.Index).ToArray());
        Assert.IsFalse(queue.HasIncompleteOperations);
        Assert.IsFalse(queue.IsActive);
    }

    private static void PumpUntil(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        Assert.IsTrue(task.IsCompletedSuccessfully);
    }

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

        Assert.IsTrue(condition(), "The file-operation archive did not finish before the timeout.");
    }

    private sealed class TestOperation : FileOperation
    {
        private readonly Action<TestOperation> onStart;

        public override SyncFile AndroidPath { get; }
        public int Index { get; }

        public TestOperation(
            ADBService.AdbDevice device,
            int index,
            Action<TestOperation> onStart = null)
            : base(new SyncFile($"/source/{index}"), device, Dispatcher.CurrentDispatcher)
        {
            this.onStart = onStart;
            Index = index;
            OperationName = OperationType.Delete;
            AndroidPath = new($"/target/{index}");
            TargetPath = AndroidPath;
        }

        public override void Start()
        {
            if (onStart is null)
                return;

            Status = OperationStatus.InProgress;
            onStart(this);
        }

        public void Complete() => Status = OperationStatus.Completed;

        public override void ClearChildren() => AndroidPath.ClearAll();

        public override void AddUpdates(IEnumerable<FileOpProgressInfo> newUpdates) =>
            AndroidPath.AddUpdates(newUpdates);

        public override void AddUpdates(params FileOpProgressInfo[] newUpdates) =>
            AndroidPath.AddUpdates(newUpdates);
    }
}
