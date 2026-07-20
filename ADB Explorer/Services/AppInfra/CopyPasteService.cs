using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Converters;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;
using System.Runtime.CompilerServices;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Explorer.Services;

public class CopyPasteService : ViewModelBase
{
    private int clipboardSnapshotVersion;
    private int dropSnapshotVersion;
    private int previewDataObjectIdentity;
    private CancellationTokenSource clipboardSnapshotCancellation;
    private CancellationTokenSource dropSnapshotCancellation;
    private DropSnapshot currentDropSnapshot = DropSnapshot.Empty;
    private ClipboardSnapshot currentClipboardSnapshot = ClipboardSnapshot.Empty;
    private readonly object markedCutItemsLock = new();
    private HashSet<FileClass> markedCutItems = [];

    [Flags]
    public enum DataSource
    {
        None = 0x8000,
        Android = 0x1,      // 0 for Windows
        Self = 0x2,         // 0 for other (including Android)
        Virtual = 0x4,      // 0 for immediately available files
    }

    private DragDropEffects pasteState = DragDropEffects.None;
    public DragDropEffects PasteState
    {
        get => pasteState;
        set
        {
            if (Set(ref pasteState, value))
            {
                App.FileActions.IsCutState.Value = value is DragDropEffects.Move;
                App.FileActions.IsCopyState.Value = value is DragDropEffects.Copy;
            }
        }
    }

    private DragDropEffects dropEffect = DragDropEffects.None;
    public DragDropEffects DropEffect
    {
        get => dropEffect;
        set => Set(ref dropEffect, value);
    }

    private DragDropEffects currentDropEffect = DragDropEffects.None;
    public DragDropEffects CurrentDropEffect
    {
        get => currentDropEffect;
        set => Set(ref currentDropEffect, value);
    }

    private string dropTarget = null;
    public string DropTarget
    {
        get => dropTarget;
        set => Set(ref dropTarget, value);
    }

    private DataSource pasteSource = DataSource.None;
    public DataSource PasteSource
    {
        get => pasteSource;
        set
        {
            if (Set(ref pasteSource, value)
                && pasteSource.HasFlag(DataSource.None)
                && value is not DataSource.None)
            {
                // Remove the none flag when settings something else
                pasteSource &= ~DataSource.None;
            }
        }
    }

    private DataSource dragPasteSource = DataSource.None;
    public DataSource DragPasteSource
    {
        get => dragPasteSource;
        set
        {
            if (Set(ref dragPasteSource, value)
                && dragPasteSource.HasFlag(DataSource.None)
                && value is not DataSource.None)
            {
                // Remove the none flag when settings something else
                dragPasteSource &= ~DataSource.None;
            }
        }
    }

    public enum DragState
    {
        None,
        Pending,
        Active,
    }

    private DragState dragStatus = DragState.None;

    public DragState DragStatus
    {
        get => dragStatus;
        set => Set(ref dragStatus, value);
    }

    public bool IsDrag => DragPasteSource is not DataSource.None;
    public bool IsClipboard => PasteSource is not DataSource.None && !IsDrag;
    
    public DataSource CurrentSource
    {
        get => IsDrag ? DragPasteSource : PasteSource;
        set
        {
            if (IsDrag)
                DragPasteSource = value;
            else
                PasteSource = value;
        }
    }

    public NativeMethods.HResult DragResult { get; set; }

    public DragDropEffects CurrentEffect => IsDrag ? DropEffect : PasteState;
    public string CurrentParent => IsDrag ? DragParent : ParentFolder;
    public bool IsSelf => CurrentSource.HasFlag(DataSource.Self);
    public bool IsSelfClipboard => IsSelf && IsClipboard;
    public bool IsWindows => !CurrentSource.HasFlag(DataSource.None) && !CurrentSource.HasFlag(DataSource.Android);
    public bool IsVirtual => CurrentSource.HasFlag(DataSource.Virtual);

    private string parentFolder = "";
    public string ParentFolder
    {
        get => parentFolder;
        set => Set(ref parentFolder, value);
    }

    private string dragParent = "";
    public string DragParent
    {
        get => dragParent;
        set => Set(ref dragParent, value);
    }

    private string[] files = [];
    private HashSet<string> fileSet = [];
    public string[] Files
    {
        get => files;
        set
        {
            value ??= [];
            if (Set(ref files, value))
                fileSet = value.ToHashSet(StringComparer.Ordinal);
        }
    }

    public IReadOnlySet<string> FileSet => fileSet;

    private string[] dragFiles = [];
    public string[] DragFiles
    {
        get => dragFiles;
        set
        {
            // The Set method only compares instances
            if (dragFiles.SequenceEqual(value))
                return;

            Set(ref dragFiles, value);
            ResetCurrentFiles();
        }
    }

    private FileDescriptor[] descriptors = [];
    public FileDescriptor[] Descriptors
    {
        get => descriptors;
        set
        {
            // The Set method only compares instances
            if (descriptors.SequenceEqual(value))
                return;

            Set(ref descriptors, value);
            ResetCurrentFiles();
        }
    }

    private IReadOnlyList<FileClass> _currentFiles = [];

    private void ResetCurrentFiles() => _currentFiles = null;

    public IEnumerable<FileClass> CurrentFiles
    {
        get
        {
            if (_currentFiles is null)
                _currentFiles = GetCurrentFiles().ToList();

            return _currentFiles;
        }
    }

    private IEnumerable<FileClass> GetCurrentFiles()
    {
        if (IsWindows && !IsVirtual)
        {
            foreach (var file in DragFiles)
            {
                yield return new(new FileDescriptor
                {
                    Name = FileHelper.GetFullName(file),
                    SourcePath = file,
                    IsDirectory = Directory.Exists(file),
                })
                {
                    PathType = FilePathType.Windows,
                };
            }
        }
        else
        {
            if (IsSelf
                && MasterPid == Environment.ProcessId
                && VirtualFileDataObject.SelfFiles is not null)
            {
                foreach (var file in VirtualFileDataObject.SelfFiles)
                {
                    yield return file;
                }

                yield break;
            }

            if (!IsWindows && DragFiles.Length > 0)
            {
                var descriptorMap = Descriptors
                    .GroupBy(descriptor => descriptor.Name.Replace('\\', '/').Trim('/'))
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

                foreach (var fullPath in DragFiles)
                {
                    var relativePath = FileHelper.ExtractRelativePath(fullPath, CurrentParent)
                        .Replace('\\', '/')
                        .Trim('/');
                    descriptorMap.TryGetValue(relativePath, out var descriptor);

                    var item = new FileClass(
                        FileHelper.GetFullName(fullPath),
                        fullPath,
                        descriptor is null
                            ? FileType.Unknown
                            : descriptor.IsDirectory ? FileType.Folder : FileType.File,
                        size: descriptor?.Length,
                        modifiedTime: descriptor?.ChangeTimeUtc,
                        loadIcon: false)
                    {
                        TrashIndex = CurrentParent is AdbExplorerConst.RECYCLE_PATH
                            ? new() { RecycleName = fullPath }
                            : null,
                    };

                    yield return item;
                }

                yield break;
            }

            for (int i = 0; i < Descriptors.Length; i++)
            {
                TrashIndexer indexer = null;
                if (DragFiles.Length == Descriptors.Length && CurrentParent is AdbExplorerConst.RECYCLE_PATH)
                    indexer = new() { RecycleName = DragFiles[i] };

                var desc = Descriptors[i];
                desc.SourcePath = FileHelper.ConcatPaths(CurrentParent, desc.Name);
                yield return new(desc)
                {
                    PathType = IsWindows
                        ? FilePathType.Windows
                        : FilePathType.Android,
                    TrashIndex = indexer,
                };
            }
        }
    }

    public int MasterPid { get; private set; }

    public bool IsDragFromMaster => MasterPid != Environment.ProcessId;

    public LogicalDeviceViewModel SourceDevice { get; private set; }

    public static string UserTemp => $"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\Temp\\";

    public void UpdateUI()
    {
        FileActionLogic.ScheduleUpdateFileActions();
    }

    private void UpdateCutItems()
    {
        HashSet<FileClass> cutItems = [];
        var session = App.ActiveDirectorySession;
        if (PasteSource is not DataSource.None
            && PasteSource.HasFlag(DataSource.Self)
            && session is not null)
        {
            cutItems = session.FileList
                .Where(file => FileSet.Contains(file.FullPath))
                .ToHashSet();
        }

        HashSet<FileClass> previousItems;
        lock (markedCutItemsLock)
        {
            previousItems = markedCutItems;
            markedCutItems = cutItems;
        }

        foreach (var file in previousItems.Except(cutItems))
            file.CutState = DragDropEffects.None;
        foreach (var file in cutItems)
            file.CutState = PasteState;
    }

    internal void MarkCutItem(FileClass file)
    {
        lock (markedCutItemsLock)
            markedCutItems.Add(file);
    }

    public void Clear()
    {
        if (IsClipboard)
        {
            Interlocked.Increment(ref clipboardSnapshotVersion);
            var clipboardCancellation = Interlocked.Exchange(
                ref clipboardSnapshotCancellation,
                null);
            clipboardCancellation?.Cancel();
            clipboardCancellation?.Dispose();
            ClearShellClipboard(clearSystemClipboard: true);
            PasteState = DragDropEffects.None;
            PasteSource = DataSource.None;
            Files = [];
            Descriptors = [];
            _currentFiles = [];
            ParentFolder = "";
            SourceDevice = null;
            MasterPid = 0;
            UpdateCutItems();
        }

        ClearDrag();
        UpdateUI();
    }

    public void ClearDrag()
    {
        previewDataObjectIdentity = 0;
        currentDropSnapshot = DropSnapshot.Empty;
        Interlocked.Increment(ref dropSnapshotVersion);
        var cancellation = Interlocked.Exchange(ref dropSnapshotCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();

        if (!IsDrag)
            return;

        App.RuntimeSettings.DragBitmap = null;
        if (IsClipboard)
            return;

        DropEffect = DragDropEffects.None;
        DragPasteSource = DataSource.None;
        DragFiles = [];
        Descriptors = [];
        _currentFiles = [];
        DragParent = "";
        SourceDevice = null;
        MasterPid = 0;
    }

    public void GetClipboardPasteItems()
    {
        if (Application.Current is not App app)
            return;

        int version = Interlocked.Increment(ref clipboardSnapshotVersion);
        string currentDeviceId = App.ActiveAdbDevice?.ID;
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(
            ref clipboardSnapshotCancellation,
            cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        _ = ReadAsync();

        async Task ReadAsync()
        {
            try
            {
                ClipboardSnapshot snapshot;
                try
                {
                    snapshot = await app.ReadClipboardAsync(
                        currentDeviceId,
                        cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
                {
                    App.ReportBackgroundFailure(ex, "clipboard.snapshot");
                    snapshot = ClipboardSnapshot.Empty;
                }

                app.EnqueueUiLatest("clipboard.snapshot", "clipboard.snapshot", () =>
                {
                    if (version == Volatile.Read(ref clipboardSnapshotVersion))
                        ApplyClipboardSnapshot(snapshot);
                });
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            { }
            catch (Exception ex)
            {
                App.ReportBackgroundFailure(ex, "clipboard.snapshot");
            }
            finally
            {
                if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref clipboardSnapshotCancellation,
                        null,
                        cancellation),
                    cancellation))
                {
                    cancellation.Dispose();
                }
            }
        }
    }

    public FileClass GetPreviewFile() => _currentFiles?.FirstOrDefault() ?? GetCurrentFiles().FirstOrDefault();

    private void ApplyClipboardSnapshot(ClipboardSnapshot snapshot)
    {
        if (snapshot.Source is DataSource.None || snapshot.Files.Length == 0)
        {
            ClearClipboardPasteItems();
            return;
        }

        var sourceDevice = snapshot.Source.HasFlag(DataSource.Android)
            ? App.ActiveDevices.LogicalDeviceViewModels.FirstOrDefault(device =>
                device.ID == snapshot.SourceDeviceId && device.Status is AbstractDevice.DeviceStatus.Ok)
            : null;
        if (snapshot.Source.HasFlag(DataSource.Android) && sourceDevice is null)
        {
            ClearClipboardPasteItems();
            return;
        }

        DragPasteSource = DataSource.None;
        currentClipboardSnapshot = snapshot;
        PasteSource = snapshot.Source;
        SourceDevice = sourceDevice;
        MasterPid = snapshot.MasterPid;
        DragParent = snapshot.ParentFolder;
        DragFiles = snapshot.Files;
        Descriptors = snapshot.Descriptors;

        DragDropEffects allowedEffect;
        if (App.FileActions.IsAppDrive)
            allowedEffect = FileHelper.AllFilesAreApks(snapshot.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        else if (!snapshot.Source.HasFlag(DataSource.Android) && snapshot.Source.HasFlag(DataSource.Virtual))
            allowedEffect = DragDropEffects.Copy;
        else
            allowedEffect = DragDropEffects.Move | DragDropEffects.Copy;

        if (snapshot.PreferredEffect.HasFlag(DragDropEffects.Copy) && allowedEffect.HasFlag(DragDropEffects.Copy))
            PasteState = DragDropEffects.Copy;
        else if (snapshot.PreferredEffect.HasFlag(DragDropEffects.Move) && allowedEffect.HasFlag(DragDropEffects.Move))
            PasteState = DragDropEffects.Move;
        else if (snapshot.PreferredEffect is DragDropEffects.Move && allowedEffect.HasFlag(DragDropEffects.Copy))
            PasteState = DragDropEffects.Copy;
        else
            PasteState = DragDropEffects.None;

        if (PasteState is DragDropEffects.None)
        {
            ClearClipboardPasteItems();
            return;
        }

        Files = DragFiles;
        ParentFolder = DragParent;
        UpdateCutItems();
        UpdateUI();
    }

    private void ClearClipboardPasteItems()
    {
        currentClipboardSnapshot = ClipboardSnapshot.Empty;
        ClearShellClipboard(clearSystemClipboard: false);
        PasteState = DragDropEffects.None;
        PasteSource = DataSource.None;
        Files = [];
        Descriptors = [];
        ResetCurrentFiles();
        _currentFiles = [];
        ParentFolder = "";
        SourceDevice = null;
        MasterPid = 0;
        UpdateCutItems();
        UpdateUI();
    }

    private static void ClearShellClipboard(bool clearSystemClipboard)
    {
        if (Application.Current is not App app)
            return;

        _ = ClearAsync();

        async Task ClearAsync()
        {
            try
            {
                await app.ClearClipboardAsync(clearSystemClipboard).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.ReportBackgroundFailure(ex, "clipboard.clear");
            }
        }
    }

    public void UpdateSelfVFDO(bool isDrag)
    {
        if (VirtualFileDataObject.SelfFiles is null || !VirtualFileDataObject.SelfFiles.Any())
            return;

        if (isDrag)
        {
            DragPasteSource |= DataSource.Android | DataSource.Self;
        }
        else
        {
            PasteSource |= DataSource.Android | DataSource.Self;
        }

        SourceDevice = App.ActiveAdbDevice.Device;
        MasterPid = Environment.ProcessId;
        DragParent = VirtualFileDataObject.SelfFiles.First().ParentPath;
        DragFiles = [.. VirtualFileDataObject.SelfFileGroup.FileDescriptors.Select(d => d.Name)];
        Descriptors = [.. VirtualFileDataObject.SelfFileGroup.FileDescriptors];

        UpdateUI();
    }

    public DragDropEffects GetAllowedDragEffects(IDataObject dataObject, FrameworkElement sender = null)
    {
        if (sender is null)
        {
            PasteSource &= ~DataSource.None;
            DragPasteSource = DataSource.None;
        }
        else
            DragPasteSource &= ~DataSource.None;

        PreviewDataObject(dataObject);

        if (DragFiles.Length < 1)
            return DragDropEffects.None;

        var dataContext = sender?.DataContext;
        FileClass file = dataContext is FileClass fc ? fc : null;

        if (App.FileActions.IsAppDrive)
        {
            if (FileHelper.AllFilesAreApks(DragFiles))
                return DragDropEffects.Copy;
        }
        else if (dataContext is null || file?.IsDirectory is true)
        {
            App.CopyPaste.DropTarget = dataContext is null
                ? App.ExplorerState.CurrentPath
                : file.FullPath;

            if (CurrentSource.HasFlag(DataSource.Android))
            {
                if (IsDrag)
                    return FileActionLogic.EnableDropPaste(file);
            }
            else if (CurrentSource.HasFlag(DataSource.Virtual))
                return DragDropEffects.Copy;
            
            return DragDropEffects.Move | DragDropEffects.Copy;
        }

        return DragDropEffects.None;
    }

    public void PreviewDataObject(IDataObject dataObject)
    {
        if (App.ActiveAdbDevice is null || Application.Current is not App app)
            return;

        int identity = RuntimeHelpers.GetHashCode(dataObject);
        if (previewDataObjectIdentity == identity)
            return;

        previewDataObjectIdentity = identity;
        int version = Interlocked.Increment(ref dropSnapshotVersion);
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref dropSnapshotCancellation, cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();

        CurrentSource &= ~(DataSource.Android | DataSource.Self | DataSource.Virtual);
        currentDropSnapshot = DropSnapshot.Empty;
        DragParent = "";
        DragFiles = [];
        Descriptors = [];
        SourceDevice = null;
        MasterPid = 0;
        UpdateUI();

        _ = ReadAsync();

        async Task ReadAsync()
        {
            DropSnapshot snapshot;
            try
            {
                snapshot = await app.ReadDropAsync(
                    dataObject,
                    App.ActiveAdbDevice?.ID,
                    cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                App.ReportBackgroundFailure(ex, "drop.snapshot");
                snapshot = DropSnapshot.Empty;
            }

            app.EnqueueUiLatest("drop.snapshot", "drop.snapshot", () =>
            {
                if (version != Volatile.Read(ref dropSnapshotVersion))
                    return;

                ApplyDropSnapshot(snapshot);
            });
        }
    }

    private void ApplyDropSnapshot(DropSnapshot snapshot)
    {
        if (snapshot.Source is DataSource.None || snapshot.Files.Length == 0)
        {
            DragFiles = [];
            Descriptors = [];
            UpdateUI();
            return;
        }

        var sourceDevice = snapshot.Source.HasFlag(DataSource.Android)
            ? App.ActiveDevices.UIList
                .OfType<LogicalDeviceViewModel>()
                .FirstOrDefault(device => device.ID == snapshot.SourceDeviceId
                    && device.Status is AbstractDevice.DeviceStatus.Ok)
            : null;
        if (snapshot.Source.HasFlag(DataSource.Android) && sourceDevice is null)
        {
            DragFiles = [];
            Descriptors = [];
            UpdateUI();
            return;
        }

        DragPasteSource = snapshot.Source;
        currentDropSnapshot = snapshot;
        SourceDevice = sourceDevice;
        MasterPid = snapshot.MasterPid;
        DragParent = snapshot.ParentFolder;
        DragFiles = snapshot.Files;
        Descriptors = snapshot.Descriptors;
        UpdateUI();
    }

    public void AcceptDataObject(System.Windows.DragEventArgs e, FrameworkElement sender, bool isLink = false)
    {
        var dataContext = sender.DataContext;

        string targetFolder = dataContext is FileClass { IsDirectory: true } file
            ? file.FullPath
            : App.ExplorerState.CurrentPath;
        
        // Do not perform implicit duplicate by drag (only with Ctrl)
        if (IsSelf && targetFolder == DragParent && e.KeyStates is DragDropKeyStates.None)
            return;

        AcceptDataObject(e.Data, targetFolder, isLink);
    }

    public void AcceptClipboard(IEnumerable<FileClass> selectedFiles, bool isLink = false)
    {
        string targetFolder = selectedFiles.Count() == 1 && selectedFiles.First().IsDirectory
            ? selectedFiles.First().FullPath
            : App.ExplorerState.CurrentPath;
        AcceptDataObject(null, targetFolder, isLink);
    }

    public void AcceptDataObject(IDataObject dataObject, string targetFolder, bool isLink = false)
    {
        var dropEffect = CurrentEffect;
        var isAppDrive = App.FileActions.IsAppDrive;
        var allFilesAreApks = isAppDrive && FileHelper.AllFilesAreApks(DragFiles);
        var dragFromMaster = IsDragFromMaster;
        var sourceMasterPid = MasterPid;
        var targetDevice = App.ActiveAdbDevice;
        var descriptors = Descriptors.ToArray();
        bool fromClipboard = IsClipboard;
        bool hasShellIdList = fromClipboard
            ? currentClipboardSnapshot.HasShellIdList
            : currentDropSnapshot.HasShellIdList;
        bool hasFileContents = fromClipboard
            ? currentClipboardSnapshot.HasFileContents
            : currentDropSnapshot.HasFileContents;

        void ReadObject()
        {
            // For all cases where the files aren't immediately available on disk
            if (IsVirtual)
            {
                string tempDragPath = App.RuntimeSettings.ResetTempDragPath();

                // Transfer from another Android device
                if (!IsWindows)
                {
                    ADBService.AdbDevice sourceDevice = new(SourceDevice);
                    var sourcePaths = DragFiles.ToArray();
                    var dispatcher = App.Current.Dispatcher;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            Directory.CreateDirectory(tempDragPath);
                            var treesBySource = FileHelper.GetFolderTrees(sourcePaths, sourceDevice);

                            List<FileOperation> pullOps = [];
                            List<(FileClass Item, SyncFile Source, string TargetPath)> transfers = [];
                            foreach (var sourcePath in sourcePaths)
                            {
                                var targetPath = FileHelper.ConcatPaths(
                                    tempDragPath,
                                    FileHelper.GetFullName(sourcePath),
                                    '\\');
                                if (!treesBySource.TryGetValue(sourcePath, out var sourceItems)
                                    || sourceItems.Count == 0)
                                {
                                    FileDescriptor failedSource = new()
                                    {
                                        Name = FileHelper.GetFullName(sourcePath),
                                        SourcePath = sourcePath,
                                    };
                                    SyncFile failedTarget = new(targetPath) { PathType = FilePathType.Windows };
                                    pullOps.Add(new FileSyncOperation(
                                        FileOperation.OperationType.Pull,
                                        failedSource,
                                        failedTarget,
                                        sourceDevice,
                                        new FailedOpProgressViewModel(Strings.Resources.S_SYNC_FILE_NOT_FOUND)));
                                    continue;
                                }

                                var root = sourceItems[0];
                                FileClass item = new(
                                    FileHelper.GetFullName(sourcePath),
                                    sourcePath,
                                    root.Item2 is null ? FileType.Folder : FileType.File,
                                    size: root.Item2,
                                    modifiedTime: root.Item3.FromUnixTime(),
                                    loadIcon: false);
                                transfers.Add((item, new SyncFile(item, sourceItems.Skip(1)), targetPath));
                            }

                            if (transfers.Count > 0)
                            {
                                FileSyncOperation pullOp;
                                if (transfers.Count == 1)
                                {
                                    SyncFile target = new(transfers[0].Item) { PathType = FilePathType.Windows };
                                    target.UpdatePath(transfers[0].TargetPath);
                                    pullOp = FileSyncOperation.PullFile(
                                        transfers[0].Source,
                                        target,
                                        sourceDevice,
                                        dispatcher);
                                }
                                else
                                {
                                    SyncFile batchSource = new(
                                        FileHelper.GetParentPath(transfers[0].Item.FullPath),
                                        FileType.Folder);
                                    batchSource.Children.AddRange(transfers.Select(transfer => transfer.Source));
                                    SyncFile batchTarget = new(tempDragPath, FileType.Folder)
                                    {
                                        PathType = FilePathType.Windows,
                                    };
                                    pullOp = FileSyncOperation.PullFile(
                                        batchSource,
                                        batchTarget,
                                        sourceDevice,
                                        dispatcher);
                                    pullOp.IsBatch = true;
                                }

                                void pullCompleted(object s, PropertyChangedEventArgs e)
                                {
                                    if (e.PropertyName != nameof(FileSyncOperation.Status)
                                        || pullOp.Status is FileOperation.OperationStatus.Waiting or FileOperation.OperationStatus.InProgress)
                                    {
                                        return;
                                    }

                                    pullOp.PropertyChanged -= pullCompleted;
                                    _ = CompleteDeviceDropAsync();
                                }

                                async Task CompleteDeviceDropAsync()
                                {
                                    try
                                    {
                                        if (pullOp.Status is not FileOperation.OperationStatus.Completed
                                            || pullOp.StatusInfo is not CompletedSyncProgressViewModel { FilesSkipped: 0 })
                                        {
                                            return;
                                        }

                                        if (isAppDrive)
                                        {
                                            if (allFilesAreApks)
                                            {
                                                await ShellFileOperation.PushPackagesAsync(
                                                    targetDevice,
                                                    transfers.Select(transfer => transfer.TargetPath),
                                                    dispatcher).ConfigureAwait(false);
                                            }

                                            return;
                                        }

                                        var targetPaths = transfers.Select(transfer => transfer.TargetPath).ToArray();
                                        var pushOps = await VerifyAndPush(
                                            targetFolder,
                                            targetPaths,
                                            targetDevice,
                                            dropEffect).ConfigureAwait(false);

                                        if (dropEffect is not DragDropEffects.Move)
                                            return;

                                        var queuedItems = pushOps.Sum(op => op.IsBatch ? op.FilePath.Children.Count : 1);
                                        if (queuedItems != targetPaths.Length)
                                            return;

                                        await Task.WhenAll(pushOps.Select(op => op.Completion)).ConfigureAwait(false);
                                        if (pushOps.Any(op => op.Status is not FileOperation.OperationStatus.Completed
                                            || op.StatusInfo is not CompletedSyncProgressViewModel { FilesSkipped: 0 }))
                                        {
                                            return;
                                        }

                                        // Delete source roots only after every queued upload has fully succeeded.
                                        foreach (var transfer in transfers)
                                        {
                                            try
                                            {
                                                ShellFileOperation.SilentDelete(sourceDevice, transfer.Item.FullPath);
                                                if (dragFromMaster)
                                                    IpcService.NotifyFileMoved(sourceMasterPid, sourceDevice, transfer.Item);
                                            }
                                            catch
                                            { }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        App.ReportBackgroundFailure(ex, "transfer.complete-device-drop");
                                    }
                                }

                                pullOp.PropertyChanged += pullCompleted;
                                pullOps.Add(pullOp);
                            }

                            if (pullOps.Count > 0 && !dispatcher.HasShutdownStarted)
                            {
                                await App.ActiveFileOperations.AddOperationsAsync(pullOps).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            App.ReportBackgroundFailure(ex, "transfer.prepare-device-drop");
                        }
                    });

                }
                else if (hasShellIdList || hasFileContents)
                {
                    _ = MaterializeAndPushAsync(
                        dataObject,
                        fromClipboard,
                        descriptors,
                        hasShellIdList,
                        hasFileContents,
                        tempDragPath,
                        targetFolder,
                        targetDevice,
                        dropEffect,
                        isAppDrive,
                        allFilesAreApks);
                }
            }
            else if (IsWindows) // FileDrop format
            {
                if (isAppDrive)
                {
                    if (allFilesAreApks)
                        _ = ShellFileOperation.PushPackagesAsync(targetDevice, DragFiles, App.Current.Dispatcher);
                }
                else
                    _ = VerifyAndPush(targetFolder, DragFiles, targetDevice, dropEffect);
            }
            else if (IsSelf)
            {
                // Dragging a folder into itself is not allowed
                if (DragFiles.Length == 1 && DragFiles[0] == targetFolder && IsDrag)
                    return;

                if (isAppDrive)
                {
                    if (allFilesAreApks)
                        _ = ShellFileOperation.InstallPackagesAsync(targetDevice, CurrentFiles, App.Current.Dispatcher);
                }
                else
                {
                    var masterPid = dragFromMaster ? sourceMasterPid : 0;
                    _ = VerifyAndPasteAsync(
                        isLink ? DragDropEffects.Link : dropEffect,
                        targetFolder,
                        CurrentFiles,
                        App.Current.Dispatcher,
                        targetDevice,
                        App.ExplorerState.CurrentPath,
                        masterPid);
                }
            }
            else
            {
                // Not supported
                return;
            }

            if (dropEffect is DragDropEffects.Move
                && !(fromClipboard && IsVirtual && (hasShellIdList || hasFileContents)))
                Clear();
        }

        ReadObject();

        if (IsDrag)
            ClearDrag();
    }

    private async Task MaterializeAndPushAsync(
        IDataObject dataObject,
        bool fromClipboard,
        FileDescriptor[] descriptors,
        bool hasShellIdList,
        bool hasFileContents,
        string tempDragPath,
        string targetFolder,
        ADBService.AdbDevice targetDevice,
        DragDropEffects dropEffect,
        bool isAppDrive,
        bool allFilesAreApks)
    {
        if (Application.Current is not App app)
            return;

        try
        {
            var snapshot = fromClipboard
                ? await app.MaterializeClipboardAsync(
                    descriptors,
                    hasShellIdList,
                    hasFileContents,
                    tempDragPath).ConfigureAwait(false)
                : await app.MaterializeDropAsync(
                    dataObject,
                    descriptors,
                    hasShellIdList,
                    hasFileContents,
                    tempDragPath).ConfigureAwait(false);

            if (snapshot.Failures.Length > 0)
            {
                var failedOperations = snapshot.Failures.Select(failure =>
                    (FileOperation)new FileSyncOperation(
                        FileOperation.OperationType.Push,
                        failure.Descriptor,
                        new(targetFolder),
                        targetDevice,
                        new FailedOpProgressViewModel(failure.Message))).ToList();
                await App.ActiveFileOperations.AddOperationsAsync(failedOperations).ConfigureAwait(false);
            }

            if (snapshot.TopLevelPaths.Length == 0)
                return;

            if (isAppDrive)
            {
                if (allFilesAreApks)
                {
                    await ShellFileOperation.PushPackagesAsync(
                        targetDevice,
                        snapshot.TopLevelPaths,
                        App.Current.Dispatcher).ConfigureAwait(false);
                }
            }
            else
            {
                await VerifyAndPush(
                    targetFolder,
                    snapshot.TopLevelPaths,
                    targetDevice,
                    dropEffect).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "shell.materialize");
        }
        finally
        {
            if (fromClipboard && dropEffect is DragDropEffects.Move)
                await app.EnqueueUiAsync("clipboard.move-complete", Clear).ConfigureAwait(false);
        }
    }

    public static async Task<IReadOnlyList<FileSyncOperation>> VerifyAndPush(
        string targetPath,
        IEnumerable<string> itemPaths,
        ADBService.AdbDevice device,
        DragDropEffects dropEffects = DragDropEffects.Copy)
    {
        try
        {
            var paths = itemPaths.ToList();
            var files = (await MergeFiles(paths, targetPath, device)).ToList();
            if (files.Count == 0)
                return [];

            if (files.Count < paths.Count)
                paths = paths.Where(files.Contains).ToList();

            return await FileActionLogic.PushShellObjects(paths, targetPath, device, dropEffects);
        }
        catch (Exception e)
        {
            App.AddCommandLog($"@ADB Explorer: failed to prepare upload: {e.Message}");
            return [];
        }
    }

    public static async Task VerifyAndPush(string targetPath, IEnumerable<FileClass> pasteItems, ADBService.AdbDevice device, DragDropEffects dropEffects = DragDropEffects.Copy)
    {
        try
        {
            var items = await MergeFiles(targetPath, device, pasteItems.ToList());
            if (!items.Any())
                return;

            await FileActionLogic.PushShellObjects(items.Select(f => f.FullPath), targetPath, device, dropEffects);
        }
        catch (Exception e)
        {
            App.AddCommandLog($"@ADB Explorer: failed to prepare upload: {e.Message}");
        }
    }

    public async Task VerifyAndPasteAsync(
        DragDropEffects cutType,
        string targetPath,
        IEnumerable<FileClass> pasteItems,
        Dispatcher dispatcher,
        ADBService.AdbDevice device,
        string currentPath,
        int masterPid = 0)
    {
        try
        {
            pasteItems = await RemoveAncestor(pasteItems, targetPath, cutType);
            if (!pasteItems.Any())
                return;

            pasteItems = await MergeFiles(targetPath, device, pasteItems);
            if (!pasteItems.Any())
                return;

            var session = App.ActiveDirectorySession;
            var currentNames = session is not null
                && currentPath == App.ExplorerState.CurrentPath
                && device.ID == App.ActiveAdbDevice?.ID
                    ? session.FileList.Select(file => file.FullName).ToArray()
                    : Array.Empty<string>();
            await ShellFileOperation.MoveItems(
                device: device,
                items: pasteItems,
                targetPath: targetPath,
                currentPath: currentPath,
                existingItems: currentNames,
                dispatcher: dispatcher,
                cutType: cutType,
                masterPid: masterPid);
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "transfer.verify-paste");
        }
    }

    /// <summary>
    /// Check for existing top level items in the target location. <br />
    /// Ask the user whether to abort, continue, or exclude the conflicting items.
    /// </summary>
    /// <param name="filePaths">Full paths of the files to be transferred.</param>
    /// <param name="targetPath">Full path of the target location.</param>
    /// <returns>
    /// An empty list if user selected Cancel. <br />
    /// The original list if user selected Merge or Replace. <br />
    /// The file list excluding the top level conflicting items if user selected Skip.
    /// </returns>
    public static async Task<IEnumerable<string>> MergeFiles(IEnumerable<string> filePaths, string targetPath, ADBService.AdbDevice device)
    {
        if (filePaths is null || targetPath is null)
            return [];

        if (!App.Current.Dispatcher.CheckAccess())
        {
            if (Application.Current is not App app)
                return [];

            Task<IEnumerable<string>> mergeTask = null;
            await app.EnqueueUiAsync(
                "transfer.merge-files",
                () => mergeTask = MergeFiles(filePaths, targetPath, device));
            return await mergeTask.ConfigureAwait(false);
        }

        var fileList = filePaths.ToList();

        // Figure out whether the target is Windows or Android
        var sep = FileHelper.GetSeparator(targetPath);

        // File names on (non virtual) Unix file systems are case sensitive
        var isUnix = sep is '/' && !DriveHelper.GetCurrentDrive(targetPath).IsFUSE;
        StringComparer comparer = isUnix
            ? StringComparer.InvariantCulture
            : StringComparer.InvariantCultureIgnoreCase;

        // Prepare a set with file system dependent comparison. Currently we only check for top level conflicts.
        // We receive full paths of the top level items in AdbDragList and FileDrop.

        HashSet<string> fileNames = await Task.Run(() =>
            new HashSet<string>(fileList.Select(FileHelper.GetFullName), comparer));
        HashSet<string> existingItems;

        if (sep is '/') // Android
        {
            var session = App.ActiveDirectorySession;
            if (targetPath == App.ExplorerState.CurrentPath
                && device.ID == App.ActiveAdbDevice?.ID
                && session is not null)
            {
                var currentFileNames = session.FileList.Select(f => f.FullName).ToArray();
                existingItems = await Task.Run(() =>
                    currentFileNames.Intersect(fileNames, comparer).ToHashSet(comparer));
            }
            else
            {
                var foundFiles = await Task.Run(() => ADBService.FindFilesInPath(
                    device.ID, targetPath, includeNames: fileNames, caseSensitive: isUnix));
                existingItems = foundFiles.Select(FileHelper.GetFullName).ToHashSet(comparer);
            }
        }
        else // Windows
        {
            existingItems = await Task.Run(() => Directory.GetDirectories(targetPath)
                .Concat(Directory.GetFiles(targetPath))
                .Select(Path.GetFileName)
                .Intersect(fileNames, comparer)
                .ToHashSet(comparer));
        }

        var count = existingItems.Count;
        if (count < 1)
            return fileList;

        string destination = FileHelper.GetFullName(targetPath);
        if (App.ExplorerState.CurrentDisplayNames.TryGetValue(targetPath, out var drive))
            destination = drive;

        var message = count == 1
            ? string.Format(Strings.Resources.S_CONFLICT_ITEMS_DESTINATION, destination)
            : string.Format(Strings.Resources.S_CONFLICT_ITEMS_PLURAL_DESTINATION, count, destination);

        var result = await DialogService.ShowConfirmation(
            message,
            Strings.Resources.S_PASTE_CONFLICTS_TITLE,
            primaryText: Strings.Resources.S_MERGE_OR_REPLACE,
            secondaryText: count == fileList.Count ? "" : Strings.Resources.S_SKIP,
            cancelText: Strings.Resources.S_CANCEL,
            icon: DialogService.DialogIcon.Exclamation);

        if (result.Item1 is ContentDialogResult.None) // Cancel
        {
            return [];
        }
        if (result.Item1 is ContentDialogResult.Secondary) // Skip
        {
            fileList = await Task.Run(() => fileList
                .Where(item => !existingItems.Contains(FileHelper.GetFullName(item)))
                .ToList());
        }

        return fileList;
    }

    /// <summary>
    /// Check for existing top level items in the target location. <br />
    /// Ask the user whether to abort, continue, or exclude the conflicting items.
    /// </summary>
    /// <param name="targetPath">Full path of the target location.</param>
    /// <param name="filePaths">Full paths of the files to be transferred.</param>
    /// <returns>
    /// An empty list if user selected Cancel. <br />
    /// The original list if user selected Merge or Replace. <br />
    /// The file list excluding the top level conflicting items if user selected Skip.
    /// </returns>
    public static async Task<IEnumerable<FileClass>> MergeFiles(string targetPath, ADBService.AdbDevice device, params IEnumerable<FileClass> filePaths)
    {
        if (filePaths is null || targetPath is null)
            return [];

        if (!App.Current.Dispatcher.CheckAccess())
        {
            if (Application.Current is not App app)
                return [];

            Task<IEnumerable<FileClass>> mergeTask = null;
            await app.EnqueueUiAsync(
                "transfer.merge-items",
                () => mergeTask = MergeFiles(targetPath, device, filePaths));
            return await mergeTask.ConfigureAwait(false);
        }

        var fileList = filePaths.ToList();

        // Figure out whether the target is Windows or Android
        var sep = FileHelper.GetSeparator(targetPath);

        // File names on (non virtual) Unix file systems are case sensitive
        var isUnix = sep is '/' && !DriveHelper.GetCurrentDrive(targetPath).IsFUSE;
        StringComparer comparer = isUnix
            ? StringComparer.InvariantCulture
            : StringComparer.InvariantCultureIgnoreCase;

        // Prepare a set with file system dependent comparison. Currently we only check for top level conflicts.
        // We receive full paths of the top level items in AdbDragList and FileDrop.

        var sourceNames = fileList.Select(file => file.FullName).ToArray();
        HashSet<string> fileNames = await Task.Run(() => new HashSet<string>(sourceNames, comparer));
        HashSet<string> existingItems;

        if (sep is '/') // Android
        {
            var session = App.ActiveDirectorySession;
            if (targetPath == App.ExplorerState.CurrentPath
                && device.ID == App.ActiveAdbDevice?.ID
                && session is not null)
            {
                var currentFileNames = session.FileList.Select(f => f.FullName).ToArray();
                existingItems = await Task.Run(() =>
                    currentFileNames.Intersect(fileNames, comparer).ToHashSet(comparer));
            }
            else
            {
                var foundFiles = await Task.Run(() => ADBService.FindFilesInPath(
                    device.ID, targetPath, includeNames: fileNames, caseSensitive: isUnix));
                existingItems = foundFiles.Select(FileHelper.GetFullName).ToHashSet(comparer);
            }
        }
        else // Windows
        {
            existingItems = await Task.Run(() => Directory.GetDirectories(targetPath)
                .Concat(Directory.GetFiles(targetPath))
                .Select(Path.GetFileName)
                .Intersect(fileNames, comparer)
                .ToHashSet(comparer));
        }

        var count = existingItems.Count;
        if (count <= 0)
            return fileList;

        string destination = FileHelper.GetFullName(targetPath);
        if (App.ExplorerState.CurrentDisplayNames.TryGetValue(targetPath, out var drive))
            destination = drive;

        var message = count == 1
            ? string.Format(Strings.Resources.S_CONFLICT_ITEMS_DESTINATION, destination)
            : string.Format(Strings.Resources.S_CONFLICT_ITEMS_PLURAL_DESTINATION, count, destination);

        var result = await DialogService.ShowConfirmation(
            message,
            Strings.Resources.S_PASTE_CONFLICTS_TITLE,
            primaryText: Strings.Resources.S_MERGE_OR_REPLACE,
            secondaryText: count == fileList.Count ? "" : Strings.Resources.S_SKIP,
            cancelText: Strings.Resources.S_CANCEL,
            icon: DialogService.DialogIcon.Exclamation);

        if (result.Item1 is ContentDialogResult.None) // Cancel
        {
            return [];
        }
        if (result.Item1 is ContentDialogResult.Secondary) // Skip
        {
            fileList = await Task.Run(() => fileList
                .Where(item => !existingItems.Contains(item.FullName))
                .ToList());
        }

        return fileList;
    }

    /// <summary>
    /// Check for pasting in descendant or self
    /// </summary>
    public async Task<IEnumerable<FileClass>> RemoveAncestor(IEnumerable<FileClass> pasteItems, string targetPath, DragDropEffects cutType)
    {
        if (cutType is DragDropEffects.Link || !IsSelf)
            return pasteItems;

        var ancestor = pasteItems.FirstOrDefault(f => f.Relation(targetPath) is RelationType.Self or RelationType.Descendant);

        if (ancestor is null)
            return pasteItems;

        var result = await DialogService.ShowConfirmation(
            string.Format(Strings.Resources.S_PASTE_ANCESTOR, ancestor.FullName),
            string.Format(Strings.Resources.S_PASTE_CONFLICT, IsDrag ? Strings.Resources.S_DROP : Strings.Resources.S_PASTE),
            Strings.Resources.S_SKIP,
            cancelText: Strings.Resources.S_BUTTON_ABORT,
            icon: DialogService.DialogIcon.Exclamation);

        return result.Item1 is ContentDialogResult.Primary
            ? pasteItems.Except([ancestor])
            : [];
    }

}
