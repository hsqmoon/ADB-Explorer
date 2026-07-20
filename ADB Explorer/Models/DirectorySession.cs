using ADB_Explorer.Helpers;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;
using System.Threading.Channels;
using static ADB_Explorer.Models.AbstractFile;
using static ADB_Explorer.Models.AdbExplorerConst;

namespace ADB_Explorer.Models;

internal sealed class DirectorySession : ViewModelBase
{
    private const int CHANNEL_CAPACITY = 512;
    private const int MAX_PENDING_UI_ITEMS = 256;

    private readonly IUiWorkScheduler uiScheduler;
    private readonly Func<FileClass, FileClass> fileManipulator;
    private readonly Func<string, ChannelWriter<FileClass>, CancellationToken, Task> directoryReader;
    private readonly SemaphoreSlim viewComputeGate = new(1, 1);
    private CancellationTokenSource currentCancellation;
    private CancellationTokenSource viewCancellation;
    private long generation;
    private long sortVersion;
    private long filterVersion;
    private long contentVersion;
    private int viewRebuildRequired;
    private FileSortSpec sort = new(nameof(FileClass.FullName), ListSortDirection.Ascending);
    private bool showHiddenItems;
    private string filterText = "";
    private TaskCompletionSource navigationCompletion;

    public Task Completion { get; private set; } = Task.CompletedTask;

    public ADBService.AdbDevice Device { get; }

    private ObservableList<FileClass> fileList;
    public ReadOnlyObservableCollection<FileClass> FileList { get; private set; }

    private ObservableList<FileClass> visibleList;
    public ReadOnlyObservableCollection<FileClass> VisibleList { get; private set; }

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

    private bool isProgressVisible;
    public bool IsProgressVisible
    {
        get => isProgressVisible;
        private set => Set(ref isProgressVisible, value);
    }

    private bool isLinkListingFinished;
    public bool IsLinkListingFinished
    {
        get => isLinkListingFinished;
        private set => Set(ref isLinkListingFinished, value);
    }

    private Exception error;
    public Exception Error
    {
        get => error;
        private set => Set(ref error, value);
    }

    public DirectorySession(
        IUiWorkScheduler uiScheduler,
        ADBService.AdbDevice adbDevice,
        Func<FileClass, FileClass> fileManipulator = null)
    {
        this.uiScheduler = uiScheduler;
        Device = adbDevice;
        this.fileManipulator = fileManipulator;
        directoryReader = Device.ListDirectoryAsync;
        ReplaceFileList([]);
        ReplaceVisibleList([]);
    }

    internal DirectorySession(
        IUiWorkScheduler uiScheduler,
        Func<string, ChannelWriter<FileClass>, CancellationToken, Task> directoryReader)
    {
        this.uiScheduler = uiScheduler;
        this.directoryReader = directoryReader;
        ReplaceFileList([]);
        ReplaceVisibleList([]);
    }

    public void Navigate(string path)
    {
        var currentGeneration = Interlocked.Increment(ref generation);
        var previousCancellation = currentCancellation;
        currentCancellation = new();
        var previousViewCancellation = viewCancellation;
        viewCancellation = null;
        RetireCancellation(previousCancellation);
        RetireCancellation(previousViewCancellation);
        Interlocked.Exchange(ref viewRebuildRequired, 0);
        Interlocked.Increment(ref sortVersion);
        sort = new(nameof(FileClass.FullName), ListSortDirection.Ascending);

        ReplaceFileList([]);
        CurrentPath = path;
        Error = null;
        IsLinkListingFinished = false;
        IsProgressVisible = false;
        InProgress = true;

        navigationCompletion?.TrySetResult();
        navigationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = navigationCompletion.Task;

        _ = ObserveSessionAsync(
            Task.Run(
                () => RunNavigationAsync(path, currentGeneration, currentCancellation.Token),
                currentCancellation.Token),
            navigationCompletion);
        _ = ShowProgressAsync(currentGeneration, currentCancellation.Token);
    }

    private async Task RunNavigationAsync(
        string path,
        long currentGeneration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            bool removed = false;
            await uiScheduler.EnqueueAsync(
                "directory.clear-row",
                () =>
                {
                    if (currentGeneration == Volatile.Read(ref generation)
                        && visibleList.Count > 0)
                    {
                        visibleList.RemoveAt(visibleList.Count - 1);
                        removed = true;
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (!removed)
                break;
        }

        await RunAsync(path, currentGeneration, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveSessionAsync(Task sessionTask, TaskCompletionSource completion)
    {
        try
        {
            await sessionTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "directory.session");
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref generation);
        var previousCancellation = currentCancellation;
        currentCancellation = null;
        var previousViewCancellation = viewCancellation;
        viewCancellation = null;
        RetireCancellation(previousCancellation);
        RetireCancellation(previousViewCancellation);
        InProgress = false;
        IsProgressVisible = false;
        IsLinkListingFinished = true;
        navigationCompletion?.TrySetResult();
    }

    internal void AddItem(FileClass item)
    {
        VerifyUiAccess();
        fileList.Add(item);
    }

    internal void InsertItem(int index, FileClass item)
    {
        VerifyUiAccess();
        fileList.Insert(index, item);
    }

    internal bool RemoveItem(FileClass item)
    {
        VerifyUiAccess();
        return fileList.Remove(item);
    }

    internal void RemoveItems(IEnumerable<FileClass> items)
    {
        VerifyUiAccess();
        foreach (var item in items.ToArray())
            fileList.Remove(item);
    }

    internal void RefreshItem(FileClass item)
    {
        VerifyUiAccess();
        int index = fileList.IndexOf(item);
        if (index < 0)
            return;

        fileList.RemoveAt(index);
        fileList.Insert(index, item);
    }

    private void ReplaceFileList(ObservableList<FileClass> value)
    {
        if (fileList is not null)
            fileList.CollectionChanged -= FileList_CollectionChanged;

        fileList = value;
        fileList.CollectionChanged += FileList_CollectionChanged;
        FileList = new(fileList);
        OnPropertyChanged(nameof(FileList));
    }

    private void ReplaceVisibleList(ObservableList<FileClass> value)
    {
        visibleList = value;
        VisibleList = new(visibleList);
        OnPropertyChanged(nameof(VisibleList));
    }

    private void VerifyUiAccess()
    {
        if (!uiScheduler.CheckAccess)
            throw new InvalidOperationException("Directory collections can only be changed by the UI scheduler.");
    }

    private async Task RunAsync(
        string path,
        long currentGeneration,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<FileClass>(new BoundedChannelOptions(CHANNEL_CAPACITY)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var producer = ProduceAsync();

        async Task ProduceAsync()
        {
            try
            {
                await directoryReader(path, channel.Writer, cancellationToken).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }
        var pendingUiItems = new List<Task>(MAX_PENDING_UI_ITEMS);
        var allItems = new List<FileClass>();
        bool firstRow = true;
        long listStart = Stopwatch.GetTimestamp();

        try
        {
            await foreach (var rawItem in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (currentGeneration != Volatile.Read(ref generation))
                    break;

                var item = fileManipulator?.Invoke(rawItem) ?? rawItem;
                allItems.Add(item);
                bool reportFirstRow = firstRow;
                firstRow = false;
                pendingUiItems.Add(uiScheduler.EnqueueAsync(
                    "directory.add-row",
                    () =>
                    {
                        if (currentGeneration != Volatile.Read(ref generation))
                            return;

                        fileList.Add(item);
                        if (reportFirstRow)
                        {
                            PerformanceTrace.Log.DirectoryFirstRow(
                                currentGeneration,
                                Stopwatch.GetElapsedTime(listStart).Ticks / 10);
                        }
                    },
                    cancellationToken).AsTask());

                if (pendingUiItems.Count >= MAX_PENDING_UI_ITEMS)
                {
                    await Task.WhenAll(pendingUiItems).ConfigureAwait(false);
                    pendingUiItems.Clear();
                }
            }

            await producer.ConfigureAwait(false);
            if (pendingUiItems.Count > 0)
                await Task.WhenAll(pendingUiItems).ConfigureAwait(false);

            await ResolveLinksAsync(allItems, currentGeneration, cancellationToken).ConfigureAwait(false);
            FileClass[] reorderSnapshot = allItems.ToArray();
            while (currentGeneration == Volatile.Read(ref generation))
            {
                var requestedSort = sort;
                long requestedSortVersion = Volatile.Read(ref sortVersion);
                long requestedFilterVersion = Volatile.Read(ref filterVersion);
                long requestedContentVersion = Volatile.Read(ref contentVersion);
                await ReorderAsync(
                    reorderSnapshot,
                    requestedSort,
                    currentGeneration,
                    requestedSortVersion,
                    requestedFilterVersion,
                    requestedContentVersion,
                    showHiddenItems,
                    filterText,
                    cancellationToken).ConfigureAwait(false);

                if (requestedSortVersion == Volatile.Read(ref sortVersion)
                    && requestedFilterVersion == Volatile.Read(ref filterVersion))
                {
                    break;
                }

                reorderSnapshot = null;
                await uiScheduler.EnqueueAsync(
                    "directory.rebuild-snapshot",
                    () =>
                    {
                        if (currentGeneration == Volatile.Read(ref generation))
                            reorderSnapshot = fileList.ToArray();
                    },
                    cancellationToken).ConfigureAwait(false);
                if (reorderSnapshot is null)
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            await uiScheduler.EnqueueAsync(
                "directory.error",
                () =>
                {
                    if (currentGeneration == Volatile.Read(ref generation))
                        Error = ex;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await uiScheduler.EnqueueAsync(
                "directory.complete",
                () =>
                {
                    if (currentGeneration != Volatile.Read(ref generation))
                        return;

                    InProgress = false;
                    IsProgressVisible = false;
                    IsLinkListingFinished = true;
                    if (Interlocked.Exchange(ref viewRebuildRequired, 0) != 0)
                        ScheduleFilterRebuild();
                },
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void Sort(string propertyName, ListSortDirection direction)
    {
        var currentGeneration = Volatile.Read(ref generation);
        var currentSortVersion = Interlocked.Increment(ref sortVersion);
        var requestedSort = new FileSortSpec(propertyName, direction);
        sort = requestedSort;
        var previousViewCancellation = viewCancellation;
        if (InProgress)
        {
            viewCancellation = null;
            RetireCancellation(previousViewCancellation);
            return;
        }

        viewCancellation = currentCancellation is null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(currentCancellation.Token);
        RetireCancellation(previousViewCancellation);
        var snapshot = fileList.ToArray();
        _ = ReorderAsync(
            snapshot,
            requestedSort,
            currentGeneration,
            currentSortVersion,
            Volatile.Read(ref filterVersion),
            Volatile.Read(ref contentVersion),
            showHiddenItems,
            filterText,
            viewCancellation.Token);
    }

    private async Task ReorderAsync(
        FileClass[] snapshot,
        FileSortSpec requestedSort,
        long currentGeneration,
        long currentSortVersion,
        long currentFilterVersion,
        long currentContentVersion,
        bool requestedShowHidden,
        string requestedFilterText,
        CancellationToken cancellationToken)
    {
        try
        {
            (ObservableList<FileClass> All, FileClass[] Visible, bool OrderChanged) result;
            await viewComputeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                result = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var originalOrder = (FileClass[])snapshot.Clone();
                    Array.Sort(snapshot, (left, right) => Compare(left, right, requestedSort));
                    List<FileClass> visibleItems = [];
                    foreach (var file in snapshot)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsVisible(file, requestedShowHidden, requestedFilterText))
                            visibleItems.Add(file);
                    }

                    return (
                        All: new ObservableList<FileClass>(snapshot),
                        Visible: visibleItems.ToArray(),
                        OrderChanged: !originalOrder.SequenceEqual(snapshot));
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                viewComputeGate.Release();
            }

            FileClass[] retrySnapshot = null;
            long retryContentVersion = 0;
            bool publishRows = false;
            await uiScheduler.EnqueueAsync(
                "directory.sort-begin",
                () =>
                {
                    if (currentGeneration != Volatile.Read(ref generation)
                        || currentSortVersion != Volatile.Read(ref sortVersion)
                        || currentFilterVersion != Volatile.Read(ref filterVersion))
                    {
                        return;
                    }

                    if (currentContentVersion != Volatile.Read(ref contentVersion))
                    {
                        retrySnapshot = fileList.ToArray();
                        retryContentVersion = Volatile.Read(ref contentVersion);
                        return;
                    }

                    bool rebuildView = Interlocked.Exchange(ref viewRebuildRequired, 0) != 0;
                    if (!result.OrderChanged && !rebuildView)
                        return;

                    ReplaceFileList(result.All);
                    publishRows = true;
                },
                cancellationToken).ConfigureAwait(false);

            if (retrySnapshot is not null)
            {
                await ReorderAsync(
                    retrySnapshot,
                    requestedSort,
                    currentGeneration,
                    currentSortVersion,
                    currentFilterVersion,
                    retryContentVersion,
                    requestedShowHidden,
                    requestedFilterText,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!publishRows)
                return;

            for (int targetIndex = 0; targetIndex < result.Visible.Length; targetIndex++)
            {
                var file = result.Visible[targetIndex];
                int desiredIndex = targetIndex;
                bool rowPublished = false;
                retrySnapshot = null;
                await uiScheduler.EnqueueAsync(
                    "directory.sort-row",
                    () =>
                    {
                        if (currentGeneration != Volatile.Read(ref generation)
                            || currentSortVersion != Volatile.Read(ref sortVersion)
                            || currentFilterVersion != Volatile.Read(ref filterVersion))
                        {
                            return;
                        }

                        if (currentContentVersion != Volatile.Read(ref contentVersion))
                        {
                            retrySnapshot = fileList.ToArray();
                            retryContentVersion = Volatile.Read(ref contentVersion);
                            return;
                        }

                        int currentIndex = visibleList.IndexOf(file);
                        if (currentIndex < 0)
                            visibleList.Insert(Math.Min(desiredIndex, visibleList.Count), file);
                        else if (currentIndex != desiredIndex)
                            visibleList.Move(currentIndex, desiredIndex);
                        rowPublished = true;
                    },
                    cancellationToken).ConfigureAwait(false);

                if (rowPublished)
                    continue;

                if (retrySnapshot is not null)
                {
                    Interlocked.Exchange(ref viewRebuildRequired, 1);
                    await ReorderAsync(
                        retrySnapshot,
                        requestedSort,
                        currentGeneration,
                        currentSortVersion,
                        currentFilterVersion,
                        retryContentVersion,
                        requestedShowHidden,
                        requestedFilterText,
                        cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            while (true)
            {
                bool rowRemoved = false;
                bool reconciliationComplete = false;
                retrySnapshot = null;
                await uiScheduler.EnqueueAsync(
                    "directory.sort-remove",
                    () =>
                    {
                        if (currentGeneration != Volatile.Read(ref generation)
                            || currentSortVersion != Volatile.Read(ref sortVersion)
                            || currentFilterVersion != Volatile.Read(ref filterVersion))
                        {
                            return;
                        }

                        if (currentContentVersion != Volatile.Read(ref contentVersion))
                        {
                            retrySnapshot = fileList.ToArray();
                            retryContentVersion = Volatile.Read(ref contentVersion);
                            return;
                        }

                        if (visibleList.Count > result.Visible.Length)
                        {
                            visibleList.RemoveAt(visibleList.Count - 1);
                            rowRemoved = true;
                        }
                        else
                        {
                            reconciliationComplete = true;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                if (rowRemoved)
                    continue;
                if (reconciliationComplete)
                    break;

                if (retrySnapshot is not null)
                {
                    Interlocked.Exchange(ref viewRebuildRequired, 1);
                    await ReorderAsync(
                        retrySnapshot,
                        requestedSort,
                        currentGeneration,
                        currentSortVersion,
                        currentFilterVersion,
                        retryContentVersion,
                        requestedShowHidden,
                        requestedFilterText,
                        cancellationToken).ConfigureAwait(false);
                }
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "directory.sort");
        }
    }

    private static int Compare(FileClass left, FileClass right, FileSortSpec requestedSort)
    {
        int result = right.IsTemp.CompareTo(left.IsTemp);
        if (result != 0)
            return result;

        result = left.IsDirectory.CompareTo(right.IsDirectory);
        result = requestedSort.Direction is ListSortDirection.Ascending ? -result : result;
        if (result != 0)
            return result;

        result = requestedSort.PropertyName switch
        {
            nameof(FileClass.ModifiedTime) => Nullable.Compare(left.ModifiedTime, right.ModifiedTime),
            nameof(FileClass.TypeName) => string.Compare(left.TypeName, right.TypeName, StringComparison.CurrentCulture),
            nameof(FileClass.Size) => Nullable.Compare(left.Size, right.Size),
            _ => 0,
        };
        if (requestedSort.Direction is ListSortDirection.Descending)
            result = -result;
        if (result != 0)
            return result;

        result = left.SortName.CompareTo(right.SortName);
        return requestedSort.Direction is ListSortDirection.Descending ? -result : result;
    }

    private async Task ResolveLinksAsync(
        IReadOnlyList<FileClass> items,
        long currentGeneration,
        CancellationToken cancellationToken)
    {
        var links = items.Where(file => file.IsLink && file.Type is FileType.Unknown).ToArray();
        if (links.Length == 0)
            return;

        var result = await Device.GetLinkTypeAsync(
            links.Select(file => file.FullPath),
            cancellationToken).ConfigureAwait(false);

        for (int i = 0; i < links.Length && i < result.Count; i++)
        {
            var file = links[i];
            var link = result[i];
            await uiScheduler.EnqueueAsync(
                "directory.resolve-link",
                () =>
                {
                    if (currentGeneration != Volatile.Read(ref generation))
                        return;

                    file.LinkTarget = link.Item1;
                    file.Type = link.Item2;
                    file.UpdateType();
                },
                cancellationToken).ConfigureAwait(false);
        }

    }

    public void ApplyFilter(bool showHidden, string text)
    {
        text ??= "";
        if (showHiddenItems == showHidden && filterText == text)
            return;

        showHiddenItems = showHidden;
        filterText = text;
        if (InProgress)
        {
            Interlocked.Increment(ref filterVersion);
            Interlocked.Exchange(ref viewRebuildRequired, 1);
            var previousViewCancellation = viewCancellation;
            viewCancellation = null;
            RetireCancellation(previousViewCancellation);
            return;
        }

        ScheduleFilterRebuild();
    }

    public void RefreshDisplayNames()
    {
        var currentGeneration = Volatile.Read(ref generation);
        _ = RefreshDisplayNamesAsync(
            fileList.ToArray(),
            currentGeneration,
            currentCancellation?.Token ?? CancellationToken.None);
    }

    private async Task RefreshDisplayNamesAsync(
        FileClass[] files,
        long currentGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var batch in files.Chunk(32))
            {
                await uiScheduler.EnqueueAsync(
                    "directory.display-name",
                    () =>
                    {
                        if (currentGeneration != Volatile.Read(ref generation))
                            return;

                        foreach (var file in batch)
                            file.RefreshDisplayName();
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "directory.display-name");
        }
    }

    private void FileList_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        Interlocked.Increment(ref contentVersion);
        if (e.Action is NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (FileClass file in e.NewItems)
            {
                file.SetIconCancellationToken(currentCancellation?.Token ?? CancellationToken.None);
                if (IsVisible(file, showHiddenItems, filterText))
                    visibleList.Add(file);
            }
        }
        else if (e.Action is NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (FileClass file in e.OldItems)
                visibleList.Remove(file);
        }
        else
        {
            ScheduleFilterRebuild();
        }
    }

    private void ScheduleFilterRebuild()
    {
        var currentFilterVersion = Interlocked.Increment(ref filterVersion);
        var previousViewCancellation = viewCancellation;
        viewCancellation = currentCancellation is null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(currentCancellation.Token);
        RetireCancellation(previousViewCancellation);
        var snapshot = fileList.ToArray();
        _ = RebuildVisibleListAsync(
            snapshot,
            Volatile.Read(ref generation),
            currentFilterVersion,
            Volatile.Read(ref sortVersion),
            Volatile.Read(ref contentVersion),
            showHiddenItems,
            filterText,
            viewCancellation.Token);
    }

    private async Task RebuildVisibleListAsync(
        FileClass[] snapshot,
        long currentGeneration,
        long currentFilterVersion,
        long currentSortVersion,
        long currentContentVersion,
        bool showHidden,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            FileClass[] filtered;
            await viewComputeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                filtered = await Task.Run(() =>
                {
                    List<FileClass> result = [];
                    foreach (var file in snapshot)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsVisible(file, showHidden, text))
                            result.Add(file);
                    }

                    return result.ToArray();
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                viewComputeGate.Release();
            }

            bool publishRows = false;
            await uiScheduler.EnqueueAsync(
                "directory.filter-begin",
                () =>
                {
                    if (currentGeneration == Volatile.Read(ref generation)
                        && currentFilterVersion == Volatile.Read(ref filterVersion)
                        && currentSortVersion == Volatile.Read(ref sortVersion)
                        && currentContentVersion == Volatile.Read(ref contentVersion))
                    {
                        publishRows = true;
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (!publishRows)
                return;

            for (int targetIndex = 0; targetIndex < filtered.Length; targetIndex++)
            {
                var file = filtered[targetIndex];
                int desiredIndex = targetIndex;
                bool rowPublished = false;
                bool rebuild = false;
                await uiScheduler.EnqueueAsync(
                    "directory.filter-row",
                    () =>
                    {
                        if (currentGeneration != Volatile.Read(ref generation)
                            || currentFilterVersion != Volatile.Read(ref filterVersion)
                            || currentSortVersion != Volatile.Read(ref sortVersion))
                        {
                            return;
                        }

                        if (currentContentVersion != Volatile.Read(ref contentVersion))
                        {
                            rebuild = true;
                            return;
                        }

                        int currentIndex = visibleList.IndexOf(file);
                        if (currentIndex < 0)
                            visibleList.Insert(Math.Min(desiredIndex, visibleList.Count), file);
                        else if (currentIndex != desiredIndex)
                            visibleList.Move(currentIndex, desiredIndex);
                        rowPublished = true;
                    },
                    cancellationToken).ConfigureAwait(false);

                if (rowPublished)
                    continue;

                if (rebuild)
                {
                    await uiScheduler.EnqueueAsync(
                        "directory.filter-restart",
                        ScheduleFilterRebuild,
                        cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            while (true)
            {
                bool rowRemoved = false;
                bool reconciliationComplete = false;
                bool rebuild = false;
                await uiScheduler.EnqueueAsync(
                    "directory.filter-remove",
                    () =>
                    {
                        if (currentGeneration != Volatile.Read(ref generation)
                            || currentFilterVersion != Volatile.Read(ref filterVersion)
                            || currentSortVersion != Volatile.Read(ref sortVersion))
                        {
                            return;
                        }

                        if (currentContentVersion != Volatile.Read(ref contentVersion))
                        {
                            rebuild = true;
                            return;
                        }

                        if (visibleList.Count > filtered.Length)
                        {
                            visibleList.RemoveAt(visibleList.Count - 1);
                            rowRemoved = true;
                        }
                        else
                        {
                            reconciliationComplete = true;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                if (rowRemoved)
                    continue;
                if (reconciliationComplete)
                    break;

                if (rebuild)
                {
                    await uiScheduler.EnqueueAsync(
                        "directory.filter-restart",
                        ScheduleFilterRebuild,
                        cancellationToken).ConfigureAwait(false);
                }
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "directory.filter");
        }
    }

    private static bool IsVisible(FileClass file, bool showHidden, string text)
    {
        if (!showHidden && file.IsHidden)
            return false;
        if (POSSIBLE_RECYCLE_PATHS.Contains(file.FullPath)
            || file.Extension == RECYCLE_INDEX_SUFFIX)
        {
            return false;
        }

        return string.IsNullOrEmpty(text)
            || file.ToString().Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ShowProgressAsync(long currentGeneration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DIR_LIST_VISIBLE_PROGRESS_DELAY, cancellationToken).ConfigureAwait(false);
            uiScheduler.EnqueueLatest(
                $"directory.progress.{GetHashCode()}",
                "directory.progress",
                () =>
                {
                    if (currentGeneration == Volatile.Read(ref generation))
                        IsProgressVisible = InProgress;
                });
        }
        catch (OperationCanceledException)
        { }
    }

    private static void RetireCancellation(CancellationTokenSource cancellation)
    {
        if (cancellation is not null)
            _ = RetireCancellationAsync(cancellation);
    }

    private static async Task RetireCancellationAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "directory.cancel");
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private readonly record struct FileSortSpec(string PropertyName, ListSortDirection Direction);
}
