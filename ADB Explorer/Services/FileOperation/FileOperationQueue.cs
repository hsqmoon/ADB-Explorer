using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

public class FileOperationQueue : ViewModelBase
{
    private const int MAX_PAST_OPERATIONS = 500;
    private const int MAX_CONCURRENT_SHELL_OPERATIONS = 4;

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

    public ObservableList<FileOperation> VisibleOperations { get; } = [];

    public bool CurrentChanged { get => false; set => OnPropertyChanged(); }

    public static readonly string[] NotifyProperties = [nameof(IsActive), nameof(AnyFailedOperations), nameof(Progress)];

    public bool HasIncompleteOperations => pendingCount + runningCount > 0;

    public bool HasRunningSyncOperations => runningSyncCount > 0;

    public int TotalCount => totalCount;

    public string StringProgress => $"{completedCount} / {TotalCount}";

    public bool AnyFailedOperations => failedCount > 0;

    #endregion

    private readonly object operationLock = new();
    private readonly IUiWorkScheduler uiScheduler;
    private readonly Action<bool> setRingVisibility;
    private readonly Func<bool> allowMultiOperation;
    private readonly Func<bool> rescanOnPush;
    private readonly Lazy<AdbCommandClient> commandClient = new(
        () => new(),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly HashSet<FileOperation> operationSet = [];
    private readonly Dictionary<(FileOperation.OperationType? Type, string DeviceId), Queue<FileOperation>> pendingGroups = [];
    private readonly Dictionary<(FileOperation.OperationType? Type, string DeviceId), int> runningGroupCounts = [];
    private readonly Dictionary<(string DeviceId, string TargetPath), int> pendingPushTargetCounts = [];
    private readonly HashSet<(string DeviceId, string TargetPath)> scheduledMediaScans = [];
    private readonly Dictionary<FileOperation, bool> pendingPastCleanup = [];
    private readonly HashSet<FileOpFilter.FilterType> visibleFilters = Enum
        .GetValues<FileOpFilter.FilterType>()
        .Where(filter => filter is not FileOpFilter.FilterType.Running)
        .ToHashSet();
    private readonly SemaphoreSlim mediaScanGate = new(1, 1);
    private int totalCount;
    private int completedCount;
    private int failedCount;
    private int pendingCount;
    private int runningCount;
    private volatile int runningSyncCount;
    private double runningProgress;
    private long viewGeneration;
    private FileOperation[] pendingVisibleOperations = [];
    private long applyingViewGeneration = -1;
    private int visibleOperationIndex;
    private int pastCleanupActive;
    private int bulkRemovalActive;
    private int incrementalAddActive;

    internal FileOperationQueue(
        IUiWorkScheduler uiScheduler,
        Action<bool> setRingVisibility = null,
        Func<bool> allowMultiOperation = null,
        Func<bool> rescanOnPush = null)
    {
        this.uiScheduler = uiScheduler ?? throw new ArgumentNullException(nameof(uiScheduler));
        this.setRingVisibility = setRingVisibility;
        this.allowMultiOperation = allowMultiOperation ?? (() => false);
        this.rescanOnPush = rescanOnPush ?? (() => false);
        Operations.CollectionChanged += Operations_CollectionChanged;
    }

    public void AddOperation(FileOperation fileOp)
    {
        if (!uiScheduler.CheckAccess)
        {
            _ = AddOperationsAsync([fileOp]);
            return;
        }

        try
        {
            Monitor.Enter(operationLock);

            if (!IsActive && !HasIncompleteOperations)
                MoveOperationsToPast();

            Operations.Add(fileOp);
            OnPropertyChanged(nameof(HasIncompleteOperations));

            Start();
            RefreshView();
        } 
        finally
        {
            Monitor.Exit(operationLock);
        }
    }

    public Task AddOperationsAsync(
        IEnumerable<FileOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var operationList = operations.ToList();
        if (operationList.Count == 0)
            return Task.CompletedTask;

        cancellationToken.ThrowIfCancellationRequested();

        if (operationList.Count <= 16 && uiScheduler.CheckAccess)
        {
            AddOperationsCore(operationList);
            return Task.CompletedTask;
        }

        return AddOperationsIncrementallyAsync(operationList, cancellationToken);
    }

    private async Task AddOperationsIncrementallyAsync(
        IReadOnlyList<FileOperation> operations,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref incrementalAddActive);
        try
        {
            foreach (var operation in operations)
            {
                await uiScheduler.EnqueueAsync(
                    "file-operations.add",
                    () => AddOperationsCore([operation], finalize: false),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "file-operations.add");
            throw;
        }
        finally
        {
            await uiScheduler.EnqueueAsync("file-operations.add-complete", () =>
            {
                bool lastBatch = Interlocked.Decrement(ref incrementalAddActive) == 0;
                OnPropertyChanged(nameof(HasIncompleteOperations));
                if (lastBatch)
                {
                    NotifyOperationCounts();
                    UpdateProgress();
                }

                Start();
                RefreshView();
            }).ConfigureAwait(false);
        }
    }

    private void AddOperationsCore(
        IEnumerable<FileOperation> operations,
        bool finalize = true)
    {
        try
        {
            Monitor.Enter(operationLock);

            if (!IsActive && !HasIncompleteOperations)
                MoveOperationsToPast();

            foreach (var operation in operations)
                Operations.Add(operation);
            if (finalize)
            {
                OnPropertyChanged(nameof(HasIncompleteOperations));
                Start();
                RefreshView();
            }
        }
        finally
        {
            Monitor.Exit(operationLock);
        }
    }

    public void RefreshView() => uiScheduler.EnqueueLatest(
        "file-operations.view.prepare",
        "file-operations.view.prepare",
        RebuildVisibleOperations);

    public void SetVisibleFilters(IEnumerable<FileOpFilter.FilterType> filters)
    {
        visibleFilters.Clear();
        visibleFilters.UnionWith(filters);
        RefreshView();
    }

    private void RebuildVisibleOperations()
    {
        pendingVisibleOperations = [.. Operations
            .Where(operation => !pendingPastCleanup.ContainsKey(operation))
            .Where(operation => IsActive
                ? operation.Filter is FileOpFilter.FilterType.Running
                : visibleFilters.Contains(operation.Filter))
            .OrderBy(operation => operation.Filter)];
        Interlocked.Increment(ref viewGeneration);
        uiScheduler.EnqueueLatest(
            "file-operations.view",
            "file-operations.view",
            ApplyNextVisibleOperation);
    }

    private void ApplyNextVisibleOperation()
    {
        long generation = Volatile.Read(ref viewGeneration);
        if (applyingViewGeneration != generation)
        {
            applyingViewGeneration = generation;
            visibleOperationIndex = 0;
        }

        if (visibleOperationIndex < pendingVisibleOperations.Length)
        {
            var expected = pendingVisibleOperations[visibleOperationIndex];
            if (visibleOperationIndex >= VisibleOperations.Count
                || !ReferenceEquals(VisibleOperations[visibleOperationIndex], expected))
            {
                int existingIndex = VisibleOperations.IndexOf(expected);
                if (existingIndex < 0)
                    VisibleOperations.Insert(visibleOperationIndex, expected);
                else
                    VisibleOperations.Move(existingIndex, visibleOperationIndex);
            }

            visibleOperationIndex++;
        }
        else if (VisibleOperations.Count > pendingVisibleOperations.Length)
        {
            VisibleOperations.RemoveAt(VisibleOperations.Count - 1);
        }
        else
        {
            return;
        }

        uiScheduler.EnqueueLatest(
            "file-operations.view",
            "file-operations.view",
            ApplyNextVisibleOperation);
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
            RefreshView();
        }
        finally
        {
            Monitor.Exit(operationLock);
        }
    }

    public async Task RemoveOperationsAsync(IEnumerable<FileOperation> operations)
    {
        var operationList = operations.Distinct().ToArray();
        if (operationList.Length == 0)
            return;

        Interlocked.Increment(ref viewGeneration);
        try
        {
            foreach (var operation in operationList)
            {
                await uiScheduler.EnqueueAsync("file-operations.remove", () =>
                {
                    lock (operationLock)
                    {
                        if (operation.Status is FileOperation.OperationStatus.InProgress)
                        {
                            operation.Cancel();
                            return;
                        }

                        Interlocked.Exchange(ref bulkRemovalActive, 1);
                        try
                        {
                            Operations.Remove(operation);
                            VisibleOperations.Remove(operation);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref bulkRemovalActive, 0);
                        }
                    }
                }).ConfigureAwait(false);
            }

            await uiScheduler.EnqueueAsync("file-operations.remove-complete", () =>
            {
                lock (operationLock)
                {
                    RebuildOperationCounts();
                    NotifyOperationCounts();
                    UpdateProgress();
                    RefreshView();
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { }
        catch (ObjectDisposedException)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "file-operations.remove");
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
            var pastOperations = Operations
                .Where(op => op.IsPastOp || pendingPastCleanup.ContainsKey(op))
                .Concat(operationsToMove)
                .Distinct()
                .OrderByDescending(op => op.TimeStamp)
                .ToArray();
            var retainedPastOperations = pastOperations
                .Take(MAX_PAST_OPERATIONS)
                .ToHashSet();

            foreach (var operation in pastOperations)
            {
                if (pendingPastCleanup.ContainsKey(operation)
                    || operationsToMoveSet.Contains(operation)
                    || !retainedPastOperations.Contains(operation))
                {
                    pendingPastCleanup[operation] = retainedPastOperations.Contains(operation);
                }
            }

            RebuildOperationCounts();
            NotifyOperationCounts();
            UpdateProgress();
            RefreshView();
            StartPastCleanup();
        }
        finally
        {
            Monitor.Exit(operationLock);
        }
    }

    private void StartPastCleanup()
    {
        if (pendingPastCleanup.Count == 0
            || Interlocked.Exchange(ref pastCleanupActive, 1) != 0)
        {
            return;
        }

        _ = RunPastCleanupAsync();
    }

    private async Task RunPastCleanupAsync()
    {
        try
        {
            while (true)
            {
                bool hasMore = false;
                await uiScheduler.EnqueueAsync("file-operations.archive", () =>
                {
                    lock (operationLock)
                    {
                        if (pendingPastCleanup.Count == 0)
                        {
                            Interlocked.Exchange(ref pastCleanupActive, 0);
                            return;
                        }

                        var operation = pendingPastCleanup.First();
                        if (operation.Value)
                            operation.Key.IsPastOp = true;
                        else
                            Operations.Remove(operation.Key);

                        pendingPastCleanup.Remove(operation.Key);
                        hasMore = pendingPastCleanup.Count > 0;
                        if (!hasMore)
                        {
                            Interlocked.Exchange(ref pastCleanupActive, 0);
                            CurrentChanged = true;
                        }
                    }
                }).ConfigureAwait(false);

                if (!hasMore)
                    return;
            }
        }
        catch (OperationCanceledException)
        { }
        catch (ObjectDisposedException)
        { }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref pastCleanupActive, 0);
            App.ReportBackgroundFailure(ex, "file-operations.archive");
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

    public void Start()
    {
        if (TotalCount < 1 || !IsAutoPlayOn)
            return;

        if (!IsActive)
            IsActive = true;

        MoveToNextOperation();

        UpdateProgress();
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
            (Application.Current as App)?.RequestUi(UiCommand.RefreshLocation);
    }

    private void MoveToCompleted(FileOperation op)
    {
        try
        {
            Monitor.Enter(operationLock);

            op.PropertyChanged -= CurrentOperation_PropertyChanged;
            UpdateProgress();

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

            foreach (var groupKey in pendingGroups.Keys.ToList())
            {
                while (pendingGroups.TryGetValue(groupKey, out var pending))
                {
                    runningGroupCounts.TryGetValue(groupKey, out int runningInGroup);
                    if (ShouldWaitForRunningGroup(allowMultiOperation(), groupKey.Type, runningInGroup))
                        break;

                    FileOperation nextOperation = null;
                    while (pending.TryDequeue(out var candidate))
                    {
                        if (operationSet.Contains(candidate)
                            && !candidate.IsPastOp
                            && candidate.Status is FileOperation.OperationStatus.Waiting
                            && GetOperationGroup(candidate) == groupKey)
                        {
                            nextOperation = candidate;
                            break;
                        }
                    }

                    if (pending.Count == 0)
                        pendingGroups.Remove(groupKey);
                    if (nextOperation is null)
                        break;

                    nextOperation.PropertyChanged += CurrentOperation_PropertyChanged;
                    nextOperation.Start();
                    CurrentChanged = true;
                }
            }

            if (pendingGroups.Count == 0 && runningCount == 0)
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
            UpdateProgress();
        }
    }

    private void FileOpRingVisibility()
    {
        setRingVisibility?.Invoke(IsActive && !AnyFailedOperations && Progress > 0 && TotalCount > 0);
    }

    private void Operations_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (FileOperation operation in e.NewItems)
                operationSet.Add(operation);
        }
        else if (e.Action is NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (FileOperation operation in e.OldItems)
                operationSet.Remove(operation);
        }
        else if (e.Action is NotifyCollectionChangedAction.Reset)
        {
            operationSet.Clear();
            operationSet.UnionWith(Operations);
        }

        if (Volatile.Read(ref bulkRemovalActive) != 0
            || e.Action is NotifyCollectionChangedAction.Remove
                && e.OldItems?.Cast<FileOperation>().All(pendingPastCleanup.ContainsKey) is true)
        {
            return;
        }

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

        if (Volatile.Read(ref incrementalAddActive) != 0)
            return;

        NotifyOperationCounts();
        UpdateProgress();
    }

    private void AddOperationCounts(FileOperation op)
    {
        if (op.IsPastOp || pendingPastCleanup.ContainsKey(op))
            return;

        totalCount++;
        switch (op.Status)
        {
            case FileOperation.OperationStatus.Waiting:
                pendingCount++;
                var pendingKey = GetOperationGroup(op);
                if (!pendingGroups.TryGetValue(pendingKey, out var pending))
                {
                    pending = new();
                    pendingGroups[pendingKey] = pending;
                }
                pending.Enqueue(op);
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
        pendingGroups.Clear();
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
        => runningInGroup > 0
            && (groupType is null
                || !allowMultiOperation
                || runningInGroup >= MAX_CONCURRENT_SHELL_OPERATIONS);

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
        if (!rescanOnPush()
            || op.Device.Device.AndroidVersion < AdbExplorerConst.MIN_MEDIA_SCAN_ANDROID_VER
            || !scheduledMediaScans.Add(key))
        {
            return;
        }

        _ = RunMediaScanAsync(op, key);
    }

    private async Task RunMediaScanAsync(
        FileOperation op,
        (string DeviceId, string TargetPath) key)
    {
        try
        {
            await Task.Delay(250).ConfigureAwait(false);

            if (Application.Current is not App app)
                return;

            bool stillPending = false;
            await app.EnqueueUiAsync("media-scan.prepare", () =>
            {
                scheduledMediaScans.Remove(key);
                stillPending = pendingPushTargetCounts.GetValueOrDefault(key) > 0;
            });

            if (stillPending)
                return;

            await mediaScanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await commandClient.Value.ExecuteShellAsync(
                    op.Device.Device,
                    "content call --method scan_volume --uri content://media --arg external_primary",
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                mediaScanGate.Release();
            }
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "media-scan");
        }
    }
}
