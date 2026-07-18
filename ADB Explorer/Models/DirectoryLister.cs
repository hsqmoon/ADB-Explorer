using ADB_Explorer.Helpers;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;
using static ADB_Explorer.Models.AbstractFile;
using static ADB_Explorer.Models.AdbExplorerConst;

namespace ADB_Explorer.Models;

public class DirectoryLister(Dispatcher dispatcher, ADBService.AdbDevice adbDevice, Func<FileClass, FileClass> fileManipulator = null) : ViewModelBase
{
    public ADBService.AdbDevice Device { get; } = adbDevice;
    public ObservableList<FileClass> FileList { get; } = [];

    private string currentPath;
    public string CurrentPath
    {
        get => currentPath;
        private set => Set(ref currentPath, value);
    }

    private bool inProgress;
    public bool InProgress
    { 
        get => inProgress;
        private set => Set(ref inProgress, value);
    }

    private bool isProgressVisible = false;
    public bool IsProgressVisible
    {
        get => isProgressVisible;
        private set => Set(ref isProgressVisible, value);
    }

    private bool isLinkListingFinished = false;
    public bool IsLinkListingFinished
    {
        get => isLinkListingFinished;
        private set => Set(ref isLinkListingFinished, value);
    }

    private Dispatcher Dispatcher { get; } = dispatcher;
    private Task UpdateTask { get; set; }
    private TimeSpan UpdateInterval { get; set; }
    private int MinUpdateThreshold { get; set; }
    private Task ReadTask { get; set; } = null;
    private CancellationTokenSource CurrentCancellationToken { get; set; }
    private CancellationTokenSource LinkListCancellation { get; set; }
    private Func<FileClass, FileClass> FileManipulator { get; } = fileManipulator;

    private ConcurrentQueue<FileClass> currentFileQueue;
    private int currentListVersion;

    public void Navigate(string path)
    {
        StartDirectoryList(path);
    }

    public void Stop()
    {
        Interlocked.Increment(ref currentListVersion);
        LinkListCancellation?.Cancel();
        StopDirectoryList(false);
        IsLinkListingFinished = true;
    }

    private void StartDirectoryList(string path)
    {
        int listVersion = Interlocked.Increment(ref currentListVersion);

        void resetState()
        {
            IsLinkListingFinished = false;

            LinkListCancellation?.Cancel();
            StopDirectoryList(false);
            FileList.RemoveAll();

            InProgress = true;
            IsProgressVisible = false;
            CurrentPath = path;
        }

        if (Dispatcher.CheckAccess())
            resetState();
        else
            Dispatcher.Invoke(resetState);

        CurrentCancellationToken = new();
        LinkListCancellation = new();
        currentFileQueue = new ConcurrentQueue<FileClass>();

        var cancellation = CurrentCancellationToken;
        var queue = currentFileQueue;
        var readTask = Task.Run(
            () => Device.ListDirectory(path, queue, Dispatcher, cancellation.Token), cancellation.Token);
        ReadTask = readTask;
        readTask.ContinueWith((t) =>
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (listVersion == Volatile.Read(ref currentListVersion)
                    && ReferenceEquals(ReadTask, readTask))
                {
                    StopDirectoryList(true);
                }
            }));
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        Task.Delay(DIR_LIST_VISIBLE_PROGRESS_DELAY).ContinueWith((t) =>
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (listVersion == Volatile.Read(ref currentListVersion))
                    IsProgressVisible = InProgress;
            }));
        }, cancellation.Token);

        ScheduleUpdate(listVersion, cancellation.Token);
    }

    private void ScheduleUpdate(int listVersion, CancellationToken cancellationToken)
    {
        UpdateDelays(currentFileQueue.Count);

        UpdateTask = Task.Delay(UpdateInterval);
        UpdateTask.ContinueWith(
            (t) =>
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (listVersion == Volatile.Read(ref currentListVersion))
                        UpdateDirectoryList(!InProgress, listVersion, cancellationToken);
                }));
            },
            cancellationToken,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void UpdateDelays(int queueCount)
    {
        bool manyPendingFilesExist = queueCount >= DIR_LIST_UPDATE_THRESHOLD_MAX;
        bool isListingStarting = FileList.Count < DIR_LIST_START_COUNT;

        if (isListingStarting || manyPendingFilesExist)
        {
            UpdateInterval = DIR_LIST_UPDATE_START_INTERVAL;
            MinUpdateThreshold = DIR_LIST_UPDATE_START_THRESHOLD_MIN;
        }
        else
        {
            UpdateInterval = DIR_LIST_UPDATE_INTERVAL;
            MinUpdateThreshold = DIR_LIST_UPDATE_THRESHOLD_MIN;
        }
    }

    private void UpdateDirectoryList(bool finish, int listVersion, CancellationToken cancellationToken)
    {
        List<FileClass> itemsToAdd = [];

        if (finish || (currentFileQueue.Count >= MinUpdateThreshold))
        {
            for (int i = 0; finish || (i < DIR_LIST_UPDATE_THRESHOLD_MAX); i++)
            {
                if (!currentFileQueue.TryDequeue(out FileClass item))
                {
                    break;
                }

                if (FileManipulator is not null)
                {
                    item = FileManipulator(item);
                }

                itemsToAdd.Add(item);
            }
        }

        if (itemsToAdd.Count > 0)
            FileList.AddRange(itemsToAdd);

        if (!finish)
        {
            ScheduleUpdate(listVersion, cancellationToken);
        }
    }

    private void StopDirectoryList(bool applyPendingItems)
    {
        if (ReadTask == null)
        {
           return;
        }

        CurrentCancellationToken.Cancel();

        if (ReadTask.IsFaulted)
            _ = ReadTask.Exception;
        else if (!ReadTask.IsCompleted)
        {
            _ = ReadTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        int listVersion = Volatile.Read(ref currentListVersion);
        var linkCancellation = LinkListCancellation;
        if (applyPendingItems)
            UpdateDirectoryList(true, listVersion, CurrentCancellationToken.Token);

        InProgress = false;
        IsProgressVisible = false;
        ReadTask = null;
        CurrentCancellationToken = null;

        if (!applyPendingItems)
            return;

        if (currentFileQueue.IsEmpty && !FileList.Any())
        {
            IsLinkListingFinished = true;
            return;
        }

        var items = FileList.Where(f => f.IsLink && f.Type is FileType.Unknown).ToList();
        if (items.Count == 0)
        {
            IsLinkListingFinished = true;
            return;
        }

        if (linkCancellation is not null)
            _ = Task.Run(() => ListLinks(listVersion, linkCancellation.Token, items), linkCancellation.Token);
    }

    private void ListLinks(int listVersion, CancellationToken cancellationToken, IReadOnlyList<FileClass> items)
    {
        if (cancellationToken.IsCancellationRequested
            || listVersion != Volatile.Read(ref currentListVersion))
        {
            return;
        }

        List<(string, FileType)> result = null;
        try
        {
            result = [.. Device.GetLinkType(items.Select(f => f.FullPath), cancellationToken)];
        }
        catch (OperationCanceledException)
        { }
        catch (AggregateException e) when (e.InnerException is TaskCanceledException)
        { }

        if (result is null)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (listVersion == Volatile.Read(ref currentListVersion))
                    IsLinkListingFinished = true;
            }));
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (cancellationToken.IsCancellationRequested
                || listVersion != Volatile.Read(ref currentListVersion))
            {
                return;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var file = items[i];
                var target = result[i];

                file.LinkTarget = target.Item1;
                file.Type = target.Item2;
                file.UpdateType();
            }

            IsLinkListingFinished = true;

            Data.RuntimeSettings.RefreshExplorerSorting = true;
        }));
    }
}
