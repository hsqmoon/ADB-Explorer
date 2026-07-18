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
    private static readonly TimeSpan PROGRESS_UPDATE_INTERVAL = TimeSpan.FromMilliseconds(75);
    private const int MAX_LOCAL_SYNC_PARALLELISM = 4;
    private const int MAX_REMOTE_SYNC_PARALLELISM = 2;

    private CancellationTokenSource cancelTokenSource;
    private readonly ConcurrentQueue<FileOpProgressInfo> pendingProgressUpdates = [];
    private readonly object progressFlushLock = new();
    private int progressFlushScheduled = 0;

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

    public DateTime TransferStart { get; private set; }
    public DateTime TransferEnd { get; private set; }

    IEnumerable<SyncFile> Files => [FilePath, .. FilePath.AllChildren()];
    private long? TotalBytes => Files.Sum(f => f.Size);
    private IEnumerable<SyncFile> ActiveFiles => Files.Where(f => f.CurrentPercentage is > 0 and < 100);

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

        if (OperationName is OperationType.Push &&
            !File.Exists(FilePath.FullPath) && !Directory.Exists(FilePath.FullPath))
        {
            Status = OperationStatus.Failed;
            StatusInfo = new FailedOpProgressViewModel(FileOpStatusConverter.StatusString(typeof(SyncErrorInfo), message: Strings.Resources.S_SYNC_FILE_NOT_FOUND, total: true));

            return;
        }

        UnixFileStatus fileMode = UnixFileStatus.AllPermissions | UnixFileStatus.Regular;

        var task = Task.Run(() =>
        {
            TransferStart = DateTime.Now;

            if (OperationName is OperationType.Push)
            {
                var paths = FolderHelper.GetBottomMostFolders(Files)
                    .Select(f => FileHelper.ConcatPaths(TargetPath.FullPath, FileHelper.ExtractRelativePath(f.FullPath, FilePath.FullPath, false)));

                if (paths.Any())
                    ShellFileOperation.MakeDirs(Device, paths);
            }
            else
            {
                foreach (var dir in FolderHelper.GetBottomMostFolders(Files))
                {
                    var targetDirPath = FileHelper.ConcatPaths(TargetPath, FileHelper.ExtractRelativePath(dir.FullPath, FilePath.FullPath, false));
                    Directory.CreateDirectory(targetDirPath);

                    Data.AddCommandLog($"@Windows: mkdir {targetDirPath}");
                }
            }

            ParallelOptions options = new()
            {
                MaxDegreeOfParallelism = GetMaxDegreeOfParallelism()
            };
            bool useV2 = Device.AndroidVersion >= 11;

            Parallel.ForEach(Files.Where(f => !f.IsDirectory), options, (item) =>
            {
                void SyncProgressCallback(SyncProgressChangedEventArgs eventArgs) => QueueProgressUpdate(item, eventArgs);

                // Open a new connection for each file to allow parallel transfers, maximizing throughput of the medium.
                // Connecting by both USB and WiFi at the same time causes instability and doesn't seem to improve the speed further.
                using SyncService service = new(ADBService.AdbServerEndPoint, Device.Device.DeviceData);
                var targetPath = FilePath.IsDirectory
                        ? FileHelper.ConcatPaths(TargetPath, FileHelper.ExtractRelativePath(item.FullPath, FilePath.FullPath))
                        : TargetPath.FullPath;

                if (OperationName is OperationType.Push)
                {
                    Data.AddCommandLog($"@AdvancedSharpAdbClient: push {item.FullPath} -> {targetPath}");

                    var lastWriteTime = item.DateModified ?? DateTime.Now;

                    try
                    {
                        // target = [Android parent folder]\[relative path from Windows parent folder to current item]
                        using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        service.Push(stream, targetPath, fileMode, lastWriteTime, SyncProgressCallback, useV2, in isCanceled);
                    }
                    catch (Exception e)
                    {
                        QueueProgressUpdate(new SyncErrorInfo(item.FullPath, e.Message));
                    }
                }
                else
                {
                    Data.AddCommandLog($"@AdvancedSharpAdbClient: pull {item.FullPath} -> {targetPath}");

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
                        QueueProgressUpdate(new SyncErrorInfo(item.FullPath, e.Message));
                    }
                }
            });

            FlushPendingProgressUpdates(true);

        });

        task.ContinueWith((t) =>
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

                Status = OperationStatus.Failed;
                StatusInfo = new FailedOpProgressViewModel(FileOpStatusConverter.StatusString(typeof(SyncErrorInfo), message: message, total: true));
            }
            else
            {
                Status = OperationStatus.Completed;

                var totalSeconds = TransferEnd.Subtract(TransferStart).TotalSeconds;
                if (totalSeconds < 0)
                    totalSeconds = 0;

                AdbSyncStatsInfo adbInfo = new(FilePath.FullPath, TotalBytes, totalSeconds, completed, filesCount - completed);
                StatusInfo = new CompletedSyncProgressViewModel(adbInfo);
            }

            ReleaseTransferResources();

        }, TaskContinuationOptions.OnlyOnRanToCompletion);

        task.ContinueWith((t) =>
        {
            FlushPendingProgressUpdates(true);
            Status = OperationStatus.Canceled;
            StatusInfo = new CanceledOpProgressViewModel();

            ReleaseTransferResources();
        }, TaskContinuationOptions.OnlyOnCanceled);

        task.ContinueWith((t) =>
        {
            FlushPendingProgressUpdates(true);

            string message = string.IsNullOrEmpty(t.Exception.InnerException.Message)
                ? (FilePath.LastUpdate as SyncErrorInfo)?.Message ?? Strings.Resources.S_SYNC_FILE_NOT_FOUND
                : t.Exception.InnerException.Message;

            Status = OperationStatus.Failed;
            StatusInfo = new FailedOpProgressViewModel(FileOpStatusConverter.StatusString(typeof(SyncErrorInfo), message: message, total: true));

            ReleaseTransferResources();
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private int GetMaxDegreeOfParallelism()
    {
        if (!Data.Settings.AllowMultiOp)
            return 1;

        int deviceLimit = Device.Type switch
        {
            AbstractDevice.DeviceType.Remote or AbstractDevice.DeviceType.WSA or AbstractDevice.DeviceType.Emulator
                => MAX_REMOTE_SYNC_PARALLELISM,
            _ => MAX_LOCAL_SYNC_PARALLELISM,
        };

        int processorLimit = Math.Clamp(Environment.ProcessorCount / 2, 2, deviceLimit);
        int fileCount = Math.Max(1, Files.Count(f => !f.IsDirectory));

        return Math.Min(fileCount, processorLimit);
    }

    private void QueueProgressUpdate(SyncFile item, SyncProgressChangedEventArgs eventArgs)
    {
        item.Size ??= (long)eventArgs.TotalBytesToReceive;

        QueueProgressUpdate(new AdbSyncProgressInfo(item.FullPath, null, eventArgs.ProgressPercentage, (long)eventArgs.ReceivedBytesSize));
    }

    private void QueueProgressUpdate(FileOpProgressInfo update)
    {
        if (update is null)
            return;

        pendingProgressUpdates.Enqueue(update);
        TransferEnd = DateTime.Now;

        ScheduleProgressFlush();
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
        List<FileOpProgressInfo> updates = [];

        lock (progressFlushLock)
        {
            Interlocked.Exchange(ref progressFlushScheduled, 0);

            while (pendingProgressUpdates.TryDequeue(out var update))
            {
                updates.Add(update);
            }
        }

        if (updates.Count == 0 || Dispatcher.HasShutdownStarted)
            return;

        void applyUpdates() => ApplyProgressUpdates(updates);

        if (Dispatcher.CheckAccess())
            applyUpdates();
        else if (invokeSynchronously)
            Dispatcher.InvokeAsync(new Action(applyUpdates)).Task.Wait();
        else
            _ = Dispatcher.BeginInvoke(new Action(applyUpdates));

        if (!pendingProgressUpdates.IsEmpty)
            ScheduleProgressFlush();
    }

    private void ApplyProgressUpdates(IEnumerable<FileOpProgressInfo> updates)
    {
        var updateList = updates.ToList();
        AddUpdates(updateList);

        if (Status is not OperationStatus.InProgress)
            return;

        if (updateList.LastOrDefault(update => update is AdbSyncProgressInfo) is AdbSyncProgressInfo currProgress)
        {
            var totalBytes = TotalBytes;
            var total = totalBytes is > 0
                ? (double)Files.Sum(f => f.BytesTransferred) / totalBytes.Value
                : 0;
            currProgress.TotalPercentage = total * 100;

            AdbSyncProgressInfo info = currProgress;

            // Total percentage is displayed for single file
            if (Files.Count() == 1)
                info = new(currProgress.AndroidPath, currProgress.TotalPercentage, null, currProgress.CurrentFileBytesTransferred);
            else if(ActiveFiles.Count() > 1)
            {
                info = new(string.Format(Strings.Resources.S_FILES_PLURAL, ActiveFiles.Count()),
                           currProgress.TotalPercentage,
                           (int)ActiveFiles.Average(f => f.CurrentPercentage),
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

        while (pendingProgressUpdates.TryDequeue(out _))
        { }
    }

    private void ReleaseTransferResources()
    {
        if (StatusInfo is CompletedSyncProgressViewModel completed && completed.FilesSkipped > 0)
        {
            var faultyFiles = Files.Where(f => !f.IsDirectory && f.LastUpdate is SyncErrorInfo)
                                   .Take(20)
                                   .Select(f => (File: f, Update: f.LastUpdate))
                                   .ToList();

            FilePath.ClearAll();

            foreach (var (file, update) in faultyFiles)
            {
                file.ProgressUpdates.Add(update);
            }

            FilePath.Children.AddRange(faultyFiles.Select(f => f.File));
        }
        else
        {
            FilePath.ClearAll();
        }

        while (pendingProgressUpdates.TryDequeue(out _))
        { }

        if (OriginalShellItem is IDisposable disposable)
        {
            disposable.Dispose();
            OriginalShellItem = null;
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
