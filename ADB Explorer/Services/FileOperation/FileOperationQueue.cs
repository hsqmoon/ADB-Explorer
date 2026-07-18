using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

public class FileOperationQueue : ViewModelBase
{
    private static readonly TimeSpan PROGRESS_REFRESH_INTERVAL = TimeSpan.FromMilliseconds(100);
    private const int MAX_PAST_OPERATIONS = 500;

    #region Full properties

    private bool isActive;
    public bool IsActive
    {
        get => isActive;
        set
        {
            if (Set(ref isActive, value))
                FileOpRingVisibility();
        }
    }

    private bool isAutoPlayOn = true;
    public bool IsAutoPlayOn
    {
        get => isAutoPlayOn;
        set => Set(ref isAutoPlayOn, value);
    }

    private double progress = 0.0;
    public double Progress
    {
        get => progress;
        set
        {
            if (Set(ref progress, value))
                FileOpRingVisibility();
        }
    }

    #endregion

    #region Read only properties

    public ObservableList<FileOperation> Operations { get; } = [];

    public bool CurrentChanged { get => false; set => OnPropertyChanged(); }

    public static readonly string[] NotifyProperties = [nameof(IsActive), nameof(AnyFailedOperations), nameof(Progress)];

    public bool HasIncompleteOperations => pendingCount + runningCount > 0;

    public bool HasRunningSyncOperations => runningSyncCount > 0;

    public int TotalCount => totalCount;

    public string StringProgress => $"{completedCount} / {TotalCount}";

    public bool AnyFailedOperations => failedCount > 0;

    #endregion

    private readonly object operationLock = new();
    private int progressRefreshScheduled;
    private readonly HashSet<(FileOperation.OperationType? Type, string DeviceId)> pendingGroupKeys = [];
    private readonly Dictionary<(FileOperation.OperationType? Type, string DeviceId), int> nextPendingIndexes = [];
    private readonly Dictionary<(FileOperation.OperationType? Type, string DeviceId), int> runningGroupCounts = [];
    private readonly Dictionary<(string DeviceId, string TargetPath), int> pendingPushTargetCounts = [];
    private readonly HashSet<(string DeviceId, string TargetPath)> scheduledMediaScans = [];
    private int totalCount;
    private int completedCount;
    private int failedCount;
    private int pendingCount;
    private int runningCount;
    private int runningSyncCount;
    private double runningProgress;

    public FileOperationQueue()
    {
        Operations.CollectionChanged += Operations_CollectionChanged;
    }

    public void AddOperation(FileOperation fileOp)
    {
        try
        {
            Monitor.Enter(operationLock);

            if (!IsActive && !HasIncompleteOperations)
                MoveOperationsToPast();

            Operations.Add(fileOp);
            OnPropertyChanged(nameof(HasIncompleteOperations));

            Start();
        } 
        finally
        {
            Monitor.Exit(operationLock);
        }
    }

    public void AddOperations(IEnumerable<FileOperation> operations)
    {
        try
        {
            Monitor.Enter(operationLock);

            if (!IsActive && !HasIncompleteOperations)
                MoveOperationsToPast();

            Operations.AddRange(operations);
            OnPropertyChanged(nameof(HasIncompleteOperations));

            Start();
        }
        finally
        {
            Monitor.Exit(operationLock);
        }
    }

    public void RemoveOperation(FileOperation fileOp)
    {
        try
        {
            Monitor.Enter(operationLock);

            if (fileOp.Status is FileOperation.OperationStatus.InProgress)
            {
                fileOp.Cancel();
                return;
            }

            Operations.Remove(fileOp);
        }
        finally
        {
            Monitor.Exit(operationLock);
        }
    }

    public void MoveOperationsToPast(bool includeAll = false, DeviceViewModel device = null)
    {
        try
        {
            Monitor.Enter(operationLock);

            Func<FileOperation, bool> predicate = op => {
                if (device is not null && op.Device.ID != device.ID)
                    return false;

                return includeAll || op.Status
                    is not FileOperation.OperationStatus.Waiting
                    and not FileOperation.OperationStatus.InProgress;
            };

            var operationsToMove = Operations.Where(predicate).ToList();
            var operationsToMoveSet = operationsToMove.ToHashSet();
            var retainedPastOperations = Operations
                .Where(op => op.IsPastOp)
                .Concat(operationsToMove)
                .Distinct()
                .OrderByDescending(op => op.TimeStamp)
                .Take(MAX_PAST_OPERATIONS)
                .ToHashSet();
            var operationsToRemove = Operations
                .Where(op => (op.IsPastOp || operationsToMoveSet.Contains(op))
                    && !retainedPastOperations.Contains(op))
                .ToList();

            if (operationsToRemove.Count > 0)
                Operations.RemoveAll(operationsToRemove);

            foreach (var op in operationsToMove.Where(retainedPastOperations.Contains))
                op.IsPastOp = true;

            RebuildOperationCounts();
            NotifyOperationCounts();
            ScheduleProgressRefresh(immediate: true);
        }
        finally
        {
            Monitor.Exit(operationLock);
        }
    }

    private void UpdateProgress()
    {
        if (totalCount == 0)
        {
            Progress = 0;
            FileOpRingVisibility();
            return;
        }

        double done = totalCount - pendingCount - runningCount;
        double current = runningProgress / 100.0;

        Progress = (done + current) / totalCount;

        FileOpRingVisibility();
    }

    private void ScheduleProgressRefresh(bool immediate = false)
    {
        if (Interlocked.Exchange(ref progressRefreshScheduled, 1) == 1)
            return;

        void refresh()
        {
            Interlocked.Exchange(ref progressRefreshScheduled, 0);
            UpdateProgress();
        }

        void dispatchRefresh()
        {
            if (App.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
            {
                refresh();
                return;
            }

            if (dispatcher.CheckAccess())
            {
                refresh();
                return;
            }

            try
            {
                _ = dispatcher.BeginInvoke(new Action(refresh), DispatcherPriority.Background);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref progressRefreshScheduled, 0);
            }
        }

        if (immediate)
        {
            dispatchRefresh();
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(PROGRESS_REFRESH_INTERVAL);
            dispatchRefresh();
        });
    }

    public void Start()
    {
        if (TotalCount < 1 || !IsAutoPlayOn)
            return;

        if (!IsActive)
            IsActive = true;

        MoveToNextOperation();

        ScheduleProgressRefresh(immediate: true);
    }

    public void Stop()
    {
        var runningOps = Operations.Where(op => op.Status is FileOperation.OperationStatus.InProgress);
        var isPush = runningOps.Any(op => op.OperationName is FileOperation.OperationType.Push);

        foreach (var item in runningOps)
        {
            item.Cancel();
        }
        IsActive = false;
        
        if (isPush && !App.Current.Dispatcher.HasShutdownStarted)
            Data.RuntimeSettings.Refresh = true;
    }

    private void MoveToCompleted(FileOperation op)
    {
        try
        {
            Monitor.Enter(operationLock);

            op.PropertyChanged -= CurrentOperation_PropertyChanged;
            ScheduleProgressRefresh(immediate: true);

            FileOpRingVisibility();
        }
        finally
        { 
            Monitor.Exit(operationLock);
        }
    }

    private void MoveToNextOperation()
    {
        try
        {
            Monitor.Enter(operationLock);

            foreach (var groupKey in pendingGroupKeys.ToList())
            {
                runningGroupCounts.TryGetValue(groupKey, out int runningInGroup);
                if (ShouldWaitForRunningGroup(Data.Settings.AllowMultiOp, groupKey.Type, runningInGroup))
                {
                    continue;
                }

                int startIndex = nextPendingIndexes.GetValueOrDefault(groupKey);
                FileOperation nextOperation = null;
                for (int i = startIndex; i < Operations.Count; i++)
                {
                    var candidate = Operations[i];
                    if (!candidate.IsPastOp
                        && candidate.Status is FileOperation.OperationStatus.Waiting
                        && GetOperationGroup(candidate) == groupKey)
                    {
                        nextOperation = candidate;
                        nextPendingIndexes[groupKey] = i + 1;
                        break;
                    }
                }

                if (nextOperation is null)
                {
                    pendingGroupKeys.Remove(groupKey);
                    nextPendingIndexes.Remove(groupKey);
                    continue;
                }

                nextOperation.PropertyChanged += CurrentOperation_PropertyChanged;
                nextOperation.Start();
                CurrentChanged = true;
            }

            if (pendingGroupKeys.Count == 0 && runningCount == 0)
            {
                IsActive = false;
                CurrentChanged = true;
            }
        }
        finally
        {
            Monitor.Exit(operationLock);
        }
    }

    private void CurrentOperation_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        var op = (FileOperation)sender;
        
        if (e.PropertyName is nameof(FileOperation.Status))
        {
            if (!op.IsPastOp)
                UpdateOperationStatusCounts(op);

            Data.RuntimeSettings.IsPollingStopped = Data.Settings.StopPollingOnSync && HasRunningSyncOperations;

            if (op.Status
                is not FileOperation.OperationStatus.Waiting
                and not FileOperation.OperationStatus.InProgress)
            {
                MoveToCompleted(op);

                if (IsAutoPlayOn)
                    MoveToNextOperation();
            }
        }

        if (e.PropertyName is nameof(FileOperation.StatusInfo)
            && op.Status is FileOperation.OperationStatus.InProgress
            && op.StatusInfo is InProgSyncProgressViewModel { TotalPercentage: double percentage })
        {
            runningProgress += percentage - op.LastProgress;
            op.LastProgress = percentage;
            ScheduleProgressRefresh();
        }
    }

    private void FileOpRingVisibility()
    {
        Data.FileActions.IsFileOpRingVisible = IsActive && !AnyFailedOperations && Progress > 0 && TotalCount > 0;
    }

    private void Operations_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (FileOperation item in e.NewItems)
            {
                item.BeginWaiting();
                AddOperationCounts(item);
            }
        }
        else
        {
            foreach (FileOperation item in Operations.Where(op => op.Status is FileOperation.OperationStatus.None))
                item.BeginWaiting();

            RebuildOperationCounts();
        }

        NotifyOperationCounts();
        ScheduleProgressRefresh(immediate: true);
    }

    private void AddOperationCounts(FileOperation op)
    {
        if (op.IsPastOp)
            return;

        totalCount++;
        switch (op.Status)
        {
            case FileOperation.OperationStatus.Waiting:
                pendingCount++;
                pendingGroupKeys.Add(GetOperationGroup(op));
                break;
            case FileOperation.OperationStatus.InProgress:
                runningCount++;
                runningProgress += op.LastProgress;
                if (op is FileSyncOperation)
                    runningSyncCount++;
                var runningKey = GetOperationGroup(op);
                runningGroupCounts[runningKey] = runningGroupCounts.GetValueOrDefault(runningKey) + 1;
                break;
            case FileOperation.OperationStatus.Completed:
                completedCount++;
                break;
            case FileOperation.OperationStatus.Failed:
                failedCount++;
                break;
        }

        if (op.OperationName is FileOperation.OperationType.Push
            && op.Status is FileOperation.OperationStatus.Waiting or FileOperation.OperationStatus.InProgress)
        {
            var key = (op.Device.ID, GetPushTargetDirectory(op));
            pendingPushTargetCounts[key] = pendingPushTargetCounts.GetValueOrDefault(key) + 1;
        }
    }

    private void RebuildOperationCounts()
    {
        totalCount = 0;
        completedCount = 0;
        failedCount = 0;
        pendingCount = 0;
        runningCount = 0;
        runningSyncCount = 0;
        runningProgress = 0;
        pendingGroupKeys.Clear();
        nextPendingIndexes.Clear();
        runningGroupCounts.Clear();
        pendingPushTargetCounts.Clear();

        foreach (var op in Operations)
            AddOperationCounts(op);
    }

    private void UpdateOperationStatusCounts(FileOperation op)
    {
        if (op.Status is FileOperation.OperationStatus.InProgress)
        {
            pendingCount = Math.Max(0, pendingCount - 1);
            runningCount++;
            if (op is FileSyncOperation)
                runningSyncCount++;

            var runningKey = GetOperationGroup(op);
            runningGroupCounts[runningKey] = runningGroupCounts.GetValueOrDefault(runningKey) + 1;
        }
        else if (op.Status is not FileOperation.OperationStatus.Waiting)
        {
            runningCount = Math.Max(0, runningCount - 1);
            runningProgress = Math.Max(0, runningProgress - op.LastProgress);
            if (op is FileSyncOperation)
                runningSyncCount = Math.Max(0, runningSyncCount - 1);

            var runningKey = GetOperationGroup(op);
            int runningInGroup = runningGroupCounts.GetValueOrDefault(runningKey) - 1;
            if (runningInGroup > 0)
                runningGroupCounts[runningKey] = runningInGroup;
            else
                runningGroupCounts.Remove(runningKey);

            if (op.Status is FileOperation.OperationStatus.Completed)
                completedCount++;
            else if (op.Status is FileOperation.OperationStatus.Failed)
                failedCount++;

            if (op.OperationName is FileOperation.OperationType.Push)
            {
                var key = (op.Device.ID, GetPushTargetDirectory(op));
                if (pendingPushTargetCounts.TryGetValue(key, out int pendingForTarget))
                {
                    pendingForTarget--;
                    if (pendingForTarget > 0)
                        pendingPushTargetCounts[key] = pendingForTarget;
                    else
                    {
                        pendingPushTargetCounts.Remove(key);
                        ScheduleMediaScan(op, key);
                    }
                }
            }
        }

        NotifyOperationCounts();
    }

    private static (FileOperation.OperationType? Type, string DeviceId) GetOperationGroup(FileOperation op)
        => (op is FileSyncOperation ? null : op.OperationName, op.Device.ID);

    private static string GetPushTargetDirectory(FileOperation op)
        => op is FileSyncOperation { IsBatch: true }
            ? op.TargetPath.FullPath
            : op.TargetPath.ParentPath;

    internal static bool ShouldWaitForRunningGroup(
        bool allowMultiOperation,
        FileOperation.OperationType? groupType,
        int runningInGroup)
        => runningInGroup > 0 && (groupType is null || !allowMultiOperation);

    private void NotifyOperationCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(StringProgress));
        OnPropertyChanged(nameof(AnyFailedOperations));
        OnPropertyChanged(nameof(HasIncompleteOperations));
        OnPropertyChanged(nameof(HasRunningSyncOperations));
    }

    private void ScheduleMediaScan(
        FileOperation op,
        (string DeviceId, string TargetPath) key)
    {
        if (!Data.Settings.RescanOnPush
            || op.Device.Device.AndroidVersion < AdbExplorerConst.MIN_MEDIA_SCAN_ANDROID_VER
            || !scheduledMediaScans.Add(key))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);

            if (App.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
                return;

            var stillPending = await dispatcher.InvokeAsync(() =>
            {
                scheduledMediaScans.Remove(key);
                return pendingPushTargetCounts.GetValueOrDefault(key) > 0;
            });
            if (!stillPending)
                ADBService.AdbDevice.ForceMediaScan(op.Device.Device);
        });
    }
}
