using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;

namespace ADB_Explorer.Services;

public class FileSyncOperation : FileOperation
{
    private const int PROGRESS_UPDATE_INTERVAL_MS = 100;
    private const int MAX_LOCAL_SYNC_PARALLELISM = 4;
    private const int MAX_REMOTE_SYNC_PARALLELISM = 2;

    private CancellationTokenSource cancelTokenSource;
    private readonly TransferProgressAggregator progressAggregator;
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

    public bool IsBatch { get; set; }

    internal IReadOnlyList<(
        string ParentPath,
        string FullName,
        string FullPath,
        AbstractFile.FileType Type,
        long? Size,
        DateTime? Modified,
        string SourcePath,
        bool SourceIsDirectory)> PushedItems { get; private set; } = [];

    public DateTime TransferStart { get; private set; }
    public DateTime TransferEnd { get; private set; }

    private IReadOnlyList<SyncFile> Files => files ??= [FilePath, .. FilePath.AllChildren()];
    private long? TotalBytes => Files.Sum(f => f.Size);

    private bool isCanceled = false;

    public FileSyncOperation(OperationType operationName, FileDescriptor sourcePath, SyncFile targetPath, ADBService.AdbDevice adbDevice, FailedOpProgressViewModel status)
        : base(new FileClass(sourcePath), adbDevice, App.Current.Dispatcher)
    {
        progressAggregator = new(ApplyProgressSnapshotAsync);
        OperationName = operationName;
        FilePath = new(new FileClass(sourcePath));
        TargetPath = targetPath;

        StatusInfo = status;
        Status = OperationStatus.Failed;
        AltSource = new(Navigation.SpecialLocation.Unknown);
        ReleaseTransferResources();
    }

    public FileSyncOperation(
        OperationType operationName,
        SyncFile sourcePath,
        SyncFile targetPath,
        ADBService.AdbDevice adbDevice,
        Dispatcher dispatcher) : base(sourcePath, adbDevice, dispatcher)
    {
        progressAggregator = new(ApplyProgressSnapshotAsync);
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
            progressAggregator.Reset(transferFiles.Sum(file => file.Size ?? 0));

            App.AddCommandLog($"@AdvancedSharpAdbClient: {OperationName.ToString().ToLowerInvariant()} "
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

            TransferEnd = DateTime.Now;
            cancelTokenSource.Token.ThrowIfCancellationRequested();

        });
        _ = CompleteTransferAsync(task);
    }

    private async Task CompleteTransferAsync(Task transferTask)
    {
        try
        {
            await CompleteTransferCoreAsync(transferTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "transfer.complete");
            try
            {
                await CompleteAsync(
                    OperationStatus.Failed,
                    new FailedOpProgressViewModel(ex.GetBaseException().Message)).ConfigureAwait(false);
            }
            catch (Exception statusException)
            {
                App.ReportBackgroundFailure(statusException, "transfer.complete-status");
            }
        }
        finally
        {
            try
            {
                ReleaseTransferResources();
            }
            catch (Exception ex)
            {
                App.ReportBackgroundFailure(ex, "transfer.release");
            }
        }
    }

    private async Task CompleteTransferCoreAsync(Task transferTask)
    {
        Exception failure = null;
        bool canceled = false;
        try
        {
            await transferTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            await progressAggregator.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        FileOpProgressViewModel finalInfo;
        OperationStatus finalStatus;
        if (canceled)
        {
            finalInfo = new CanceledOpProgressViewModel();
            finalStatus = OperationStatus.Canceled;
        }
        else if (failure is not null)
        {
            string message = string.IsNullOrEmpty(failure.GetBaseException().Message)
                ? (FilePath.LastUpdate as SyncErrorInfo)?.Message ?? Strings.Resources.S_SYNC_FILE_NOT_FOUND
                : failure.GetBaseException().Message;
            finalInfo = new FailedOpProgressViewModel(
                FileOpStatusConverter.StatusString(typeof(SyncErrorInfo), message: message, total: true));
            finalStatus = OperationStatus.Failed;
        }
        else
        {
            var transferFiles = Files.Where(file => !file.IsDirectory).ToArray();
            int filesCount = transferFiles.Length;
            int completed = transferFiles.Count(file => file.CurrentPercentage == 100);
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
                finalInfo = new FailedOpProgressViewModel(
                    FileOpStatusConverter.StatusString(typeof(SyncErrorInfo), message: message, total: true));
                finalStatus = OperationStatus.Failed;
            }
            else
            {
                var totalSeconds = Math.Max(0, TransferEnd.Subtract(TransferStart).TotalSeconds);
                finalInfo = new CompletedSyncProgressViewModel(
                    new AdbSyncStatsInfo(FilePath.FullPath, TotalBytes, totalSeconds, completed, filesCount - completed));
                finalStatus = OperationStatus.Completed;
            }
        }

        if (OperationName is OperationType.Push
            && finalStatus is OperationStatus.Completed
            && finalInfo is CompletedSyncProgressViewModel { FilesSkipped: 0 })
        {
            PushedItems = CreatePushedItemSnapshot(FilePath, TargetPath, IsBatch);
        }

        await CompleteAsync(finalStatus, finalInfo, DetachFileTree).ConfigureAwait(false);
    }

    internal Task Completion => transferCompletion.Task;

    internal bool WaitForShellStreamCompletion(
        Task queueCompletion,
        VirtualFileDataObject dataObject)
    {
        if (Dispatcher.CheckAccess())
            return false;

        ((IAsyncResult)queueCompletion).AsyncWaitHandle.WaitOne();
        if (!dataObject.DidOperationsQueueSuccessfully)
            return false;

        ((IAsyncResult)transferCompletion.Task).AsyncWaitHandle.WaitOne();
        return true;
    }

    private int GetMaxDegreeOfParallelism()
        => CalculateMaxDegreeOfParallelism(
            App.Settings.AllowMultiOp,
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

    internal static IReadOnlyList<(
        string ParentPath,
        string FullName,
        string FullPath,
        AbstractFile.FileType Type,
        long? Size,
        DateTime? Modified,
        string SourcePath,
        bool SourceIsDirectory)> CreatePushedItemSnapshot(
            SyncFile source,
            SyncFile target,
            bool isBatch)
    {
        if (!isBatch)
        {
            return [(
                target.ParentPath,
                target.FullName,
                target.FullPath,
                target.IsDirectory ? AbstractFile.FileType.Folder : AbstractFile.FileType.File,
                target.Size,
                source.DateModified,
                source.FullPath,
                source.IsDirectory)];
        }

        return source.Children.Select(file => (
            target.FullPath,
            file.FullName,
            FileHelper.ConcatPaths(target.FullPath, file.FullName),
            file.IsDirectory ? AbstractFile.FileType.Folder : AbstractFile.FileType.File,
            file.Size,
            file.DateModified,
            file.FullPath,
            file.IsDirectory)).ToArray();
    }

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

        progressAggregator.Report(
            item,
            update,
            transferredBytesIncrease,
            previousPercentage,
            percentage,
            totalBytesIncrease);

        TransferEnd = DateTime.Now;
    }

    internal static (long TransferredBytes, int ActiveCount, double ActivePercentage) CalculateProgressTotals(
        long transferredBytes,
        int activeCount,
        double activePercentage,
        long transferredBytesIncrease,
        double previousPercentage,
        double percentage)
    {
        return TransferProgressAggregator.CalculateTotals(
            transferredBytes,
            activeCount,
            activePercentage,
            transferredBytesIncrease,
            previousPercentage,
            percentage);
    }

    private async Task ApplyProgressSnapshotAsync(TransferProgressSnapshot snapshot)
    {
        if (Application.Current is not App app)
            return;

        var slices = snapshot.Updates.Chunk(8).ToArray();
        for (int index = 0; index < slices.Length; index++)
        {
            var slice = snapshot with { Updates = slices[index] };
            bool updateOverallProgress = index == slices.Length - 1;
            await app.EnqueueUiAsync(
                "transfer.progress",
                () => ApplyProgressUpdates(slice, updateOverallProgress)).ConfigureAwait(false);
        }
    }

    private void ApplyProgressUpdates(
        TransferProgressSnapshot snapshot,
        bool updateOverallProgress)
    {
        AdbSyncProgressInfo currProgress = null;
        foreach (var (file, update) in snapshot.Updates)
        {
            file.AddUpdates(update);
            if (update is AdbSyncProgressInfo progress)
                currProgress = progress;
        }

        if (!updateOverallProgress || Status is not OperationStatus.InProgress)
            return;

        if (currProgress is not null)
        {
            var total = snapshot.TotalBytes > 0
                ? (double)snapshot.TransferredBytes / snapshot.TotalBytes
                : 0;
            currProgress.TotalPercentage = total * 100;

            AdbSyncProgressInfo info = currProgress;

            // Total percentage is displayed for single file
            if (Files.Count == 1)
                info = new(currProgress.AndroidPath, currProgress.TotalPercentage, null, currProgress.CurrentFileBytesTransferred);
            else if (snapshot.ActiveCount > 1)
            {
                info = new(string.Format(Strings.Resources.S_FILES_PLURAL, snapshot.ActiveCount),
                           currProgress.TotalPercentage,
                           (int)(snapshot.ActivePercentage / snapshot.ActiveCount),
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

        progressAggregator.Clear();

        files = null;
        PushedItems = [];
    }

    private void DetachFileTree()
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

    private void ReleaseTransferResources()
    {
        transferCompletion.TrySetResult(true);

        progressAggregator.Dispose();
        files = null;

        var cancellation = cancelTokenSource;
        cancelTokenSource = null;
        cancellation?.Dispose();
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
