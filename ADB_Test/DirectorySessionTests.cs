using ADB_Explorer.Models;
using ADB_Explorer.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Test;

[TestClass]
public class DirectorySessionTests
{
    [STATestMethod]
    public void CompletionTracksTheActiveNavigationGeneration()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new DirectorySession(scheduler, ReadDirectoryAsync);

        session.Navigate("first");
        var firstCompletion = session.Completion;
        PumpUntil(() => firstStarted.Task.IsCompleted, TimeSpan.FromSeconds(2));

        session.Navigate("second");
        var secondCompletion = session.Completion;
        Assert.IsTrue(firstCompletion.IsCompleted);
        Assert.AreNotSame(firstCompletion, secondCompletion);
        PumpUntil(() => secondCompletion.IsCompleted, TimeSpan.FromSeconds(2));

        Assert.AreEqual("second", session.CurrentPath);
        Assert.HasCount(1, session.FileList);
        Assert.AreEqual("second.txt", session.FileList[0].FullName);

        async Task ReadDirectoryAsync(
            string path,
            ChannelWriter<FileClass> writer,
            CancellationToken cancellationToken)
        {
            if (path == "first")
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            await writer.WriteAsync(new FileClass("second.txt", "/second.txt", FileType.File), cancellationToken);
        }
    }

    [STATestMethod]
    public void SupersededNavigationCannotWriteIntoCurrentList()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var session = new DirectorySession(scheduler, ReadDirectoryAsync);

        session.Navigate("old");
        session.Navigate("new");
        PumpUntil(() => !session.InProgress, TimeSpan.FromSeconds(5));

        Assert.HasCount(1, session.FileList);
        Assert.AreEqual("new", session.FileList.Single().FullName);
        Assert.AreEqual("new", session.CurrentPath);

        static async Task ReadDirectoryAsync(
            string path,
            ChannelWriter<FileClass> writer,
            CancellationToken cancellationToken)
        {
            try
            {
                if (path == "old")
                    await Task.Delay(100, cancellationToken);

                await writer.WriteAsync(
                    new FileClass(path, $"/{path}", FileType.Folder, loadIcon: false),
                    cancellationToken);
            }
            finally
            {
                writer.TryComplete();
            }
        }
    }

    [STATestMethod]
    public void TenThousandRowsAreConsumedWithoutResetOrLoss()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var session = new DirectorySession(scheduler, ReadDirectoryAsync);
        int resetCount = 0;
        int visibleResetCount = 0;
        HashSet<object> subscribedVisibleLists = [];

        session.Navigate("large");
        SubscribeVisibleList();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DirectorySession.VisibleList))
                SubscribeVisibleList();
        };
        ((INotifyCollectionChanged)session.FileList).CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Reset)
                resetCount++;
        };
        PumpUntil(() => !session.InProgress, TimeSpan.FromSeconds(20));

        Assert.HasCount(10_000, session.FileList);
        Assert.HasCount(10_000, session.VisibleList);
        Assert.AreEqual(0, resetCount);
        Assert.AreEqual(0, visibleResetCount);

        void SubscribeVisibleList()
        {
            if (!subscribedVisibleLists.Add(session.VisibleList))
                return;

            ((INotifyCollectionChanged)session.VisibleList).CollectionChanged += (_, e) =>
            {
                if (e.Action is NotifyCollectionChangedAction.Reset)
                    visibleResetCount++;
            };
        }

        static async Task ReadDirectoryAsync(
            string path,
            ChannelWriter<FileClass> writer,
            CancellationToken cancellationToken)
        {
            try
            {
                for (int index = 0; index < 10_000; index++)
                {
                    await writer.WriteAsync(
                        new FileClass($"file-{index:D5}.bin", $"/{path}/file-{index:D5}.bin", FileType.File, size: index, loadIcon: false),
                        cancellationToken);
                }
            }
            finally
            {
                writer.TryComplete();
            }
        }
    }

    [STATestMethod]
    public void FirstRowIsPublishedWithinTheDirectoryLatencyBudget()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var session = new DirectorySession(scheduler, ReadDirectoryAsync);
        var firstRow = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        session.Navigate("first-row");
        ((INotifyCollectionChanged)session.FileList).CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Add)
                firstRow.TrySetResult(stopwatch.Elapsed);
        };
        PumpUntil(() => firstRow.Task.IsCompleted, TimeSpan.FromSeconds(2));
        session.Stop();

        Assert.IsLessThanOrEqualTo(300d, firstRow.Task.Result.TotalMilliseconds);

        static async Task ReadDirectoryAsync(
            string path,
            ChannelWriter<FileClass> writer,
            CancellationToken cancellationToken)
        {
            try
            {
                await writer.WriteAsync(
                    new FileClass("first.bin", $"/{path}/first.bin", FileType.File, loadIcon: false),
                    cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                writer.TryComplete();
            }
        }
    }

    [STATestMethod]
    public void FilterAndSortRebuildsKeepConcurrentContentChanges()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var session = new DirectorySession(scheduler, ReadDirectoryAsync);
        List<int> publishedReplacementSizes = [];

        session.Navigate("filter");
        PumpUntil(() => !session.InProgress, TimeSpan.FromSeconds(5));
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DirectorySession.VisibleList))
                publishedReplacementSizes.Add(session.VisibleList.Count);
        };
        session.ApplyFilter(showHidden: true, "alpha");
        PumpUntil(() => session.VisibleList.Count == 2, TimeSpan.FromSeconds(5));

        session.Sort(nameof(FileClass.Size), ListSortDirection.Descending);
        var addedDuringSort = new FileClass("alpha-new.bin", "/filter/alpha-new.bin", FileType.File, size: 100, loadIcon: false);
        session.AddItem(addedDuringSort);
        PumpUntil(
            () => session.FileList.Contains(addedDuringSort)
                && session.VisibleList.Contains(addedDuringSort)
                && session.FileList.First().Size == 100,
            TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(
            new[] { "alpha-new.bin", "alpha-two.bin", "alpha-one.bin" },
            session.VisibleList.Select(file => file.FullName).ToArray());
        Assert.IsTrue(publishedReplacementSizes.All(count => count == 0));

        static async Task ReadDirectoryAsync(
            string path,
            ChannelWriter<FileClass> writer,
            CancellationToken cancellationToken)
        {
            try
            {
                FileClass[] files =
                [
                    new("alpha-one.bin", $"/{path}/alpha-one.bin", FileType.File, size: 1, loadIcon: false),
                    new("beta.bin", $"/{path}/beta.bin", FileType.File, size: 3, loadIcon: false),
                    new("alpha-two.bin", $"/{path}/alpha-two.bin", FileType.File, size: 2, loadIcon: false),
                ];
                foreach (var file in files)
                    await writer.WriteAsync(file, cancellationToken);
            }
            finally
            {
                writer.TryComplete();
            }
        }
    }

    [STATestMethod]
    public void ContentChangeDuringProgressiveSortRestartsTheVisiblePublication()
    {
        using var scheduler = new PausingUiScheduler(Dispatcher.CurrentDispatcher);
        var session = new DirectorySession(scheduler, ReadDirectoryAsync);

        session.Navigate("sort-race");
        PumpUntil(() => !session.InProgress, TimeSpan.FromSeconds(5));
        scheduler.PauseSortPublication();
        session.Sort(nameof(FileClass.Size), ListSortDirection.Descending);
        PumpUntil(
            () => scheduler.FirstSortRowReached.IsCompleted
                && session.VisibleList.Count == 300
                && session.VisibleList[0].Size == 299,
            TimeSpan.FromSeconds(5));

        var newest = new FileClass(
            "newest.bin",
            "/sort-race/newest.bin",
            FileType.File,
            size: 1_000,
            loadIcon: false);
        session.AddItem(newest);
        scheduler.ResumeSortPublication();
        PumpUntil(
            () => session.VisibleList.Count == 301
                && ReferenceEquals(session.VisibleList[0], newest),
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(301, session.VisibleList.Distinct().Count());

        static async Task ReadDirectoryAsync(
            string path,
            ChannelWriter<FileClass> writer,
            CancellationToken cancellationToken)
        {
            try
            {
                for (int index = 0; index < 300; index++)
                {
                    await writer.WriteAsync(
                        new FileClass(
                            $"file-{index:D3}.bin",
                            $"/{path}/file-{index:D3}.bin",
                            FileType.File,
                            size: index,
                            loadIcon: false),
                        cancellationToken);
                }
            }
            finally
            {
                writer.TryComplete();
            }
        }
    }

    [STATestMethod]
    public void TwentyRapidNavigationsOnlyPublishTheLatestGeneration()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var session = new DirectorySession(scheduler, ReadDirectoryAsync);

        for (int index = 0; index < 20; index++)
            session.Navigate($"path-{index}");

        PumpUntil(() => !session.InProgress, TimeSpan.FromSeconds(10));

        Assert.HasCount(1, session.FileList);
        Assert.AreEqual("path-19", session.FileList.Single().FullName);

        static async Task ReadDirectoryAsync(
            string path,
            ChannelWriter<FileClass> writer,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(10, cancellationToken);
                await writer.WriteAsync(
                    new FileClass(path, $"/{path}", FileType.Folder, loadIcon: false),
                    cancellationToken);
            }
            finally
            {
                writer.TryComplete();
            }
        }
    }

    [STATestMethod]
    public void RapidSortRequestsOnlyPublishTheLatestOrder()
    {
        using var scheduler = new UiWorkScheduler(Dispatcher.CurrentDispatcher);
        var session = new DirectorySession(scheduler, ReadDirectoryAsync);

        session.Navigate("rapid-sort");
        PumpUntil(() => !session.InProgress, TimeSpan.FromSeconds(10));

        for (int index = 0; index < 20; index++)
        {
            session.Sort(
                nameof(FileClass.Size),
                index % 2 == 0
                    ? ListSortDirection.Ascending
                    : ListSortDirection.Descending);
        }

        PumpUntil(
            () => session.VisibleList.Count == 2_000
                && session.VisibleList[0].Size == 1_999
                && session.VisibleList[^1].Size == 0,
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(2_000, session.VisibleList.Distinct().Count());

        static async Task ReadDirectoryAsync(
            string path,
            ChannelWriter<FileClass> writer,
            CancellationToken cancellationToken)
        {
            try
            {
                for (int index = 0; index < 2_000; index++)
                {
                    await writer.WriteAsync(
                        new FileClass(
                            $"file-{index:D4}.bin",
                            $"/{path}/file-{index:D4}.bin",
                            FileType.File,
                            size: index,
                            loadIcon: false),
                        cancellationToken);
                }
            }
            finally
            {
                writer.TryComplete();
            }
        }
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

        Assert.IsTrue(condition(), "The directory session did not finish before the timeout.");
    }

    private sealed class PausingUiScheduler : IUiWorkScheduler
    {
        private readonly UiWorkScheduler scheduler;
        private TaskCompletionSource resumeSortRows = CompletedSignal();
        private TaskCompletionSource firstSortRowReached = CompletedSignal();
        private int pauseSortRows;
        private int sortRowCount;

        public bool CheckAccess => scheduler.CheckAccess;
        public Task FirstSortRowReached => firstSortRowReached.Task;

        public PausingUiScheduler(Dispatcher dispatcher)
        {
            scheduler = new(dispatcher);
        }

        public void PauseSortPublication()
        {
            Interlocked.Exchange(ref sortRowCount, 0);
            resumeSortRows = new(TaskCreationOptions.RunContinuationsAsynchronously);
            firstSortRowReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref pauseSortRows, 1);
        }

        public void ResumeSortPublication()
        {
            Interlocked.Exchange(ref pauseSortRows, 0);
            resumeSortRows.TrySetResult();
        }

        public ValueTask EnqueueAsync(
            string workName,
            Action action,
            CancellationToken cancellationToken = default) =>
            new(EnqueueCoreAsync(workName, action, cancellationToken));

        private async Task EnqueueCoreAsync(
            string workName,
            Action action,
            CancellationToken cancellationToken)
        {
            if (workName == "directory.sort-row" && Volatile.Read(ref pauseSortRows) != 0)
            {
                int row = Interlocked.Increment(ref sortRowCount);
                if (row == 1)
                {
                    firstSortRowReached.TrySetResult();
                }
                else
                {
                    await resumeSortRows.Task.WaitAsync(cancellationToken);
                }
            }

            await scheduler.EnqueueAsync(workName, action, cancellationToken);
        }

        public void EnqueueLatest(string key, string workName, Action action) =>
            scheduler.EnqueueLatest(key, workName, action);

        public void Dispose() => scheduler.Dispose();

        private static TaskCompletionSource CompletedSignal()
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.SetResult();
            return signal;
        }
    }
}
