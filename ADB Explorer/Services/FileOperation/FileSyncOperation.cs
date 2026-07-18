using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using Vanara.Windows.Shell;

namespace ADB_Explorer.Services;

public class FileSyncOperation : FileOperation
{
    private const int PROGRESS_UPDATE_INTERVAL_MS = 100;
    private static readonly TimeSpan PROGRESS_UPDATE_INTERVAL = TimeSpan.FromMilliseconds(PROGRESS_UPDATE_INTERVAL_MS);
    private const int MAX_LOCAL_SYNC_PARALLELISM = 4;
    private const int MAX_REMOTE_SYNC_PARALLELISM = 2;

    private CancellationTokenSource cancelTokenSource;
    private readonly Dictionary<string, (SyncFile File, FileOpProgressInfo Update)> pendingProgressUpdates = [];
    private readonly object progressFlushLock = new();
    private int progressFlushScheduled = 0;
    private long progressTotalBytes;
    private long progressTransferredBytes;
    private int progressActiveCount;
    private double progressActivePercentage;
    private IReadOnlyList<SyncFile> files;
    private readonly TaskCompletionSource<bool> transferCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override SyncFile FilePath { get; }

    public override SyncFile AndroidPath => FilePath.PathType is AbstractFile.FilePathType.Android
        ? FilePath
        : TargetPath;

    public VirtualFileDataObject VFDO { get; set; } = null;

    private DragDropEffects dropEffects = DragDropEffects.None;
    public DragDropEffects DropEffects
    {
        get
        {
            return VFDO is null ? dropEffects : VFDO.CurrentEffect;
        }
        set
        {
            dropEffects = value;
        }
    }

    public ShellItem OriginalShellItem { get; set; }

    public bool IsBatch { get; set; }

    public DateTime TransferStart { get; private set; }
    public DateTime TransferEnd { get; private set; }

    private IReadOnlyList<SyncFile> Files => files ??= [FilePath, .. FilePath.AllChildren()];
    private long? TotalBytes => Files.Sum(f => f.Size);

    private bool isCanceled = false;

    public FileSyncOperation(OperationType operationName, FileDescriptor sourcePath, SyncFile targetPath, ADBService.AdbDevice adbDevice, FailedOpProgressViewModel status)
        : base(new FileClass(sourcePath), adbDevice, App.Current.Dispatcher)
    {
        OperationName = operationName;
        FilePath = new(new FileClass(sourcePath));
        TargetPath = targetPath;

        StatusInfo = status;
        Status = OperationStatus.Failed;
        AltSource = new(Navigation.SpecialLocation.Unknown);
    }

    public FileSyncOperation(
        OperationType operationName,
        SyncFile sourcePath,
        SyncFile targetPath,
        ADBService.AdbDevice adbDevice,
        Dispatcher dispatcher) : base(sourcePath, adbDevice, dispatcher)
    {
        OperationName = operationName;
        FilePath = sourcePath;
        TargetPath = targetPath;
    }

    public override void Start()
    {
        if (Status == OperationStatus.InProgress)
        {
            throw new Exception("Cannot start an already active operation!");
        }

        Status = OperationStatus.InProgress;
        StatusInfo = new InProgSyncProgressViewModel();
        cancelTokenSource = new CancellationTokenSource();
        isCanceled = false;

        if (OperationName is OperationType.Push &&
            !File.Exists(FilePath.FullPath) && !Directory.Exists(FilePath.FullPath))
        {
            try
            {
                StatusInfo = new FailedOpProgressViewModel(FileOpStatusConverter.StatusString(typeof(SyncErrorInfo), message: Strings.Resources.S_SYNC_FILE_NOT_FOUND, total: true));
                Status = OperationStatus.Failed;
            }
            finally
            {
                ReleaseTransferResources();
            }
            return;
        }

        UnixFileStatus fileMode = UnixFileStatus.AllPermissions | UnixFileStatus.Regular;

        var task = Task.Run(async () =>
        {
            TransferStart = DateTime.Now;

            if (OperationName is OperationType.Push)
            {
                var paths = FolderHelper.GetBottomMostFolders(Files)
                    .Select(f => FileHelper.ConcatPaths(TargetPath.FullPath, FileHelper.ExtractRelativePath(f.FullPath, FilePath.FullPath, false)))
                    .ToList();

                if (paths.Count > 0)
                    await ShellFileOperation.MakeDirs(Device, paths, cancelTokenSource.Token);
            }
            else
            {
                foreach (var dir in FolderHelper.GetBottomMostFolders(Files))
                {
                    var targetDirPath = FileHelper.ConcatPaths(TargetPath, FileHelper.ExtractRelativePath(dir.FullPath, FilePath.FullPath, false));
                    Directory.CreateDirectory(targetDirPath);
                }
            }

            var transferFiles = Files.Where(f => !f.IsDirectory).ToList();
            lock (progressFlushLock)
            {
                progressTotalBytes = transferFiles.Sum(f => f.Size ?? 0);
                progressTransferredBytes = 0;
                progressActiveCount = 0;
                progressActivePercentage = 0;
            }

            Data.AddCommandLog($"@AdvancedSharpAdbClient: {OperationName.ToString().ToLowerInvariant()} "
                + $"{FilePath.FullPath} -> {TargetPath.FullPath} ({transferFiles.Count} files)");

            ParallelOptions options = new()
            {
                MaxDegreeOfParallelism = GetMaxDegreeOfParallelism(),
                CancellationToken = cancelTokenSource.Token,
            };
            bool useV2 = Device.AndroidVersion >= 11;

            Parallel.ForEach(transferFiles, options, (item) =>
            {
                long transferredBytes = 0;
                double percentage = 0;
                long lastProgressUpdate = 0;
                void SyncProgressCallback(SyncProgressChangedEventArgs eventArgs)
                    => QueueProgressUpdate(
                        item,
                        eventArgs,
                        ref transferredBytes,
                        ref percentage,
                        ref lastProgressUpdate);

                // Open a new connection for each file to allow parallel transfers, maximizing throughput of the medium.
                // Connecting by both USB and WiFi at the same time causes instability and doesn't seem to improve the speed further.
                using SyncService service = new(ADBService.AdbServerEndPoint, Device.Device.DeviceData);
                var targetPath = ResolveTargetPath(FilePath, TargetPath, item);

                if (OperationName is OperationType.Push)
                {
                    var lastWriteTime = item.DateModified ?? DateTime.Now;

                    try
                    {
                        // target = [Android parent folder]\[relative path from Windows parent folder to current item]
                        using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        service.Push(stream, targetPath, fileMode, lastWriteTime, SyncProgressCallback, useV2, in isCanceled);
                    }
                    catch (Exception e)
                    {
                        QueueProgressUpdate(
                            item,
                            new SyncErrorInfo(item.FullPath, e.Message),
                            -transferredBytes,
                            percentage,
                            0);
                    }
                }
                else
                {
                    try
                    {
                        // target = [Windows parent folder]\[relative path from Android parent folder to current item]
                        using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                        service.Pull(item.FullPath, stream, SyncProgressCallback, useV2, in isCanceled);

                        if (item.DateModified is not null)
                            File.SetLastWriteTime(targetPath, item.DateModified.Value);
                    }
                    catch (Exception e)
                    {
                        QueueProgressUpdate(
                            item,
                            new SyncErrorInfo(item.FullPath, e.Message),
                            -transferredBytes,
                            percentage,
                            0);
                    }
                }
            });

            FlushPendingProgressUpdates(true);
            cancelTokenSource.Token.ThrowIfCancellationRequested();

        });

        task.ContinueWith((t) =>
        {
            try
            {
                FlushPendingProgressUpdates(true);

                var files = Files.Where(f => !f.IsDirectory);
                int filesCount = files.Count();
                var completed = files.Count(f => f.CurrentPercentage == 100);
                if (filesCount == 0 && Files.First().IsDirectory)
                {
                    filesCount = 1;
                    completed = 1;
                }

                if (filesCount == 1 && completed == 0)
                {
                    string message = FilePath.LastUpdate is SyncErrorInfo errorInfo
                        ? errorInfo.Message
                        : Strings.Resources.S_SYNC_FILE_NOT_FOUND;

                    StatusInfo = new FailedOpProgressViewModel(FileOpStatusConverter.StatusString(typeof(SyncErrorInfo), message: message, total: true));
                    Status = OperationStatus.Failed;
                }
                else
                {
                    var totalSeconds = TransferEnd.Subtract(TransferStart).TotalSeconds;
                    if (totalSeconds < 0)
                        totalSeconds = 0;

                    AdbSyncStatsInfo adbInfo = new(FilePath.FullPath, TotalBytes, totalSeconds, completed, filesCount - completed);
                    StatusInfo = new CompletedSyncProgressViewModel(adbInfo);
                    Status = OperationStatus.Completed;
                }
            }
            finally
            {
                ReleaseTransferResources();
            }
        }, TaskContinuationOptions.OnlyOnRanToCompletion);

        task.ContinueWith((t) =>
        {
            try
            {
                FlushPendingProgressUpdates(true);
                StatusInfo = new CanceledOpProgressViewModel();
                Status = OperationStatus.Canceled;
            }
            finally
            {
                ReleaseTransferResources();
            }
        }, TaskContinuationOptions.OnlyOnCanceled);

        task.ContinueWith((t) =>
        {
            try
            {
                FlushPendingProgressUpdates(true);

                string message = string.IsNullOrEmpty(t.Exception.InnerException.Message)
                    ? (FilePath.LastUpdate as SyncErrorInfo)?.Message ?? Strings.Resources.S_SYNC_FILE_NOT_FOUND
                    : t.Exception.InnerException.Message;

                StatusInfo = new FailedOpProgressViewModel(FileOpStatusConverter.StatusString(typeof(SyncErrorInfo), message: message, total: true));
                Status = OperationStatus.Failed;
            }
            finally
            {
                ReleaseTransferResources();
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    internal void WaitForCompletion() => transferCompletion.Task.GetAwaiter().GetResult();

    private int GetMaxDegreeOfParallelism()
        => CalculateMaxDegreeOfParallelism(
            Data.Settings.AllowMultiOp,
            Device.Type,
            Environment.ProcessorCount,
            Files.Count(f => !f.IsDirectory));

    internal static int CalculateMaxDegreeOfParallelism(
        bool allowMultiOp,
        AbstractDevice.DeviceType deviceType,
        int processorCount,
        int fileCount)
    {
        if (!allowMultiOp)
            return 1;

        int deviceLimit = deviceType switch
        {
            AbstractDevice.DeviceType.Remote or AbstractDevice.DeviceType.WSA or AbstractDevice.DeviceType.Emulator
                => MAX_REMOTE_SYNC_PARALLELISM,
            _ => MAX_LOCAL_SYNC_PARALLELISM,
        };

        int processorLimit = Math.Clamp(processorCount / 2, 2, deviceLimit);

        return Math.Min(Math.Max(1, fileCount), processorLimit);
    }

    internal static string ResolveTargetPath(SyncFile sourceRoot, SyncFile targetRoot, SyncFile item)
        => sourceRoot.IsDirectory
            ? FileHelper.ConcatPaths(
                targetRoot,
                FileHelper.ExtractRelativePath(item.FullPath, sourceRoot.FullPath))
            : targetRoot.FullPath;

    private void QueueProgressUpdate(
        SyncFile item,
        SyncProgressChangedEventArgs eventArgs,
        ref long previousTransferredBytes,
        ref double previousPercentage,
        ref long lastProgressUpdate)
    {
        long totalBytesIncrease = 0;
        if (item.Size is null)
        {
            item.Size = (long)eventArgs.TotalBytesToReceive;
            totalBytesIncrease = item.Size.Value;
        }

        long transferredBytes = (long)eventArgs.ReceivedBytesSize;
        double percentage = eventArgs.ProgressPercentage;
        long now = Environment.TickCount64;

        if (!ShouldReportProgress(now, percentage, ref lastProgressUpdate))
            return;

        QueueProgressUpdate(
            item,
            new AdbSyncProgressInfo(item.FullPath, null, percentage, transferredBytes),
            transferredBytes - previousTransferredBytes,
            previousPercentage,
            percentage,
            totalBytesIncrease);

        previousTransferredBytes = transferredBytes;
        previousPercentage = percentage;
    }

    internal static bool ShouldReportProgress(long timestamp, double percentage, ref long lastProgressUpdate)
    {
        if (percentage < 100 && timestamp - lastProgressUpdate < PROGRESS_UPDATE_INTERVAL_MS)
            return false;

        lastProgressUpdate = timestamp;
        return true;
    }

    private void QueueProgressUpdate(
        SyncFile item,
        FileOpProgressInfo update,
        long transferredBytesIncrease = 0,
        double previousPercentage = 0,
        double percentage = 0,
        long totalBytesIncrease = 0)
    {
        if (update is null)
            return;

        lock (progressFlushLock)
        {
            pendingProgressUpdates[item.FullPath] = (item, update);
            progressTotalBytes += totalBytesIncrease;
            (progressTransferredBytes, progressActiveCount, progressActivePercentage) =
                CalculateProgressTotals(
                    progressTransferredBytes,
                    progressActiveCount,
                    progressActivePercentage,
                    transferredBytesIncrease,
                    previousPercentage,
                    percentage);
        }

        TransferEnd = DateTime.Now;

        ScheduleProgressFlush();
    }

    internal static (long TransferredBytes, int ActiveCount, double ActivePercentage) CalculateProgressTotals(
        long transferredBytes,
        int activeCount,
        double activePercentage,
        long transferredBytesIncrease,
        double previousPercentage,
        double percentage)
    {
        transferredBytes = Math.Max(0, transferredBytes + transferredBytesIncrease);

        if (previousPercentage is > 0 and < 100)
        {
            activeCount--;
            activePercentage -= previousPercentage;
        }

        if (percentage is > 0 and < 100)
        {
            activeCount++;
            activePercentage += percentage;
        }

        return (transferredBytes, activeCount, activePercentage);
    }

    private void ScheduleProgressFlush()
    {
        if (Interlocked.Exchange(ref progressFlushScheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PROGRESS_UPDATE_INTERVAL, cancelTokenSource?.Token ?? CancellationToken.None);
                FlushPendingProgressUpdates();
            }
            catch (OperationCanceledException)
            { }
        });
    }

    private void FlushPendingProgressUpdates(bool invokeSynchronously = false)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref progressFlushScheduled, 0);
            return;
        }

        void applyUpdates()
        {
            List<(SyncFile File, FileOpProgressInfo Update)> updates = [];
            long totalBytes;
            long transferredBytes;
            int activeCount;
            double activePercentage;

            lock (progressFlushLock)
            {
                updates.AddRange(pendingProgressUpdates.Values);
                pendingProgressUpdates.Clear();
                totalBytes = progressTotalBytes;
                transferredBytes = progressTransferredBytes;
                activeCount = progressActiveCount;
                activePercentage = progressActivePercentage;
            }

            try
            {
                if (updates.Count > 0)
                {
                    ApplyProgressUpdates(
                        updates,
                        totalBytes,
                        transferredBytes,
                        activeCount,
                        activePercentage);
                }
            }
            finally
            {
                Interlocked.Exchange(ref progressFlushScheduled, 0);

                lock (progressFlushLock)
                {
                    if (pendingProgressUpdates.Count > 0)
                        ScheduleProgressFlush();
                }
            }
        }

        if (Dispatcher.CheckAccess())
            applyUpdates();
        else if (invokeSynchronously)
            Dispatcher.InvokeAsync(new Action(applyUpdates)).Task.Wait();
        else
            _ = Dispatcher.BeginInvoke(new Action(applyUpdates), DispatcherPriority.Background);

    }

    private void ApplyProgressUpdates(
        IReadOnlyList<(SyncFile File, FileOpProgressInfo Update)> updates,
        long totalBytes,
        long transferredBytes,
        int activeCount,
        double activePercentage)
    {
        AdbSyncProgressInfo currProgress = null;
        foreach (var (file, update) in updates)
        {
            file.AddUpdates(update);
            if (update is AdbSyncProgressInfo progress)
                currProgress = progress;
        }

        if (Status is not OperationStatus.InProgress)
            return;

        if (currProgress is not null)
        {
            var total = totalBytes > 0 ? (double)transferredBytes / totalBytes : 0;
            currProgress.TotalPercentage = total * 100;

            AdbSyncProgressInfo info = currProgress;

            // Total percentage is displayed for single file
            if (Files.Count == 1)
                info = new(currProgress.AndroidPath, currProgress.TotalPercentage, null, currProgress.CurrentFileBytesTransferred);
            else if (activeCount > 1)
            {
                info = new(string.Format(Strings.Resources.S_FILES_PLURAL, activeCount),
                           currProgress.TotalPercentage,
                           (int)(activePercentage / activeCount),
                           currProgress.TotalBytesTransferred);
            }

            StatusInfo = new InProgSyncProgressViewModel(info);
        }
    }

    public override void Cancel()
    {
        if (Status != OperationStatus.InProgress)
        {
            throw new Exception("Cannot cancel a deactivated operation!");
        }

        isCanceled = true;
        cancelTokenSource.Cancel();
    }

    public override void ClearChildren()
    {
        FilePath.ClearAll();

        lock (progressFlushLock)
        {
            pendingProgressUpdates.Clear();
            progressTotalBytes = 0;
            progressTransferredBytes = 0;
            progressActiveCount = 0;
            progressActivePercentage = 0;
        }

        files = null;
    }

    private void ReleaseTransferResources()
    {
        try
        {
            void detachFileTree()
            {
                if (StatusInfo is CompletedSyncProgressViewModel completed && completed.FilesSkipped > 0)
                {
                    var faultyFiles = Files.Where(file => !file.IsDirectory && file.LastUpdate is SyncErrorInfo)
                        .Take(20)
                        .Select(file => (
                            File: new SyncFile(file.FullPath)
                            {
                                PathType = file.PathType,
                                Size = file.Size,
                                UnixTime = file.UnixTime,
                            },
                            Update: file.LastUpdate))
                        .ToList();

                    FilePath.ClearAll();
                    foreach (var (file, update) in faultyFiles)
                        file.ProgressUpdates.Add(update);

                    FilePath.Children.AddRange(faultyFiles.Select(item => item.File));
                    return;
                }

                FilePath.ClearAll();
            }

            if (!Dispatcher.HasShutdownStarted && !Dispatcher.CheckAccess())
                Dispatcher.Invoke(detachFileTree);
            else
                detachFileTree();
        }
        finally
        {
            try
            {
                lock (progressFlushLock)
                {
                    pendingProgressUpdates.Clear();
                    progressTotalBytes = 0;
                    progressTransferredBytes = 0;
                    progressActiveCount = 0;
                    progressActivePercentage = 0;
                }

                files = null;

                var cancellation = cancelTokenSource;
                cancelTokenSource = null;
                cancellation?.Dispose();
            }
            finally
            {
                try
                {
                    var originalShellItem = OriginalShellItem;
                    OriginalShellItem = null;
                    (originalShellItem as IDisposable)?.Dispose();
                }
                finally
                {
                    transferCompletion.TrySetResult(true);
                }
            }
        }
    }

    public override void AddUpdates(IEnumerable<FileOpProgressInfo> newUpdates)
        => FilePath.AddUpdates(newUpdates, this);

    public override void AddUpdates(params FileOpProgressInfo[] newUpdates)
        => FilePath.AddUpdates(newUpdates, this);

    public static FileSyncOperation PullFile(SyncFile sourcePath, SyncFile targetPath, ADBService.AdbDevice adbDevice, Dispatcher dispatcher)
        => new(OperationType.Pull, sourcePath, targetPath, adbDevice, dispatcher);

    public static FileSyncOperation PushFile(SyncFile sourcePath, SyncFile targetPath, ADBService.AdbDevice adbDevice, Dispatcher dispatcher)
        => new(OperationType.Push, sourcePath, targetPath, adbDevice, dispatcher);
}
