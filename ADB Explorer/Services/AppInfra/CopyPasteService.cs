using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Converters;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;
using Vanara.Windows.Shell;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Explorer.Services;

public class CopyPasteService : ViewModelBase
{
    private static void RunInBackgroundSta(Action action)
    {
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Data.AddCommandLog($"@ADB Explorer: failed to read virtual files: {e.Message}");
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

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
                Data.FileActions.IsCutState.Value = value is DragDropEffects.Move;
                Data.FileActions.IsCopyState.Value = value is DragDropEffects.Copy;
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
    private IDataObject previewDataObject;

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
        FileActionLogic.UpdateFileActions();

        List<FileClass> cutItems = [];
        if (PasteSource is not DataSource.None
            && PasteSource.HasFlag(DataSource.Self)
            && Data.DirList is not null)
            cutItems = [.. Data.DirList.FileList.Where(f => FileSet.Contains(f.FullPath))];

        cutItems.ForEach(file => file.CutState = PasteState);
        Data.DirList?.FileList.Except(cutItems).ForEach(file => file.CutState = DragDropEffects.None);
    }

    public void Clear()
    {
        if (IsClipboard)
        {
            Clipboard.Clear();
            VirtualFileDataObject.ReleaseClipboardData();
            PasteState = DragDropEffects.None;
            PasteSource = DataSource.None;
            Files = [];
            Descriptors = [];
            _currentFiles = [];
            ParentFolder = "";
            SourceDevice = null;
            MasterPid = 0;
        }

        ClearDrag();
        UpdateUI();
    }

    public void ClearDrag()
    {
        previewDataObject = null;

        if (!IsDrag)
            return;

        Data.RuntimeSettings.DragBitmap = null;
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
        try
        {
            var CPDO = Clipboard.GetDataObject();
            if (CPDO is null)
            {
                ClearClipboardPasteItems();
                return;
            }

#if !DEPLOY
            try
            {
                DebugLog.PrintLine($"Clipboard formats: {string.Join(", ", CPDO.GetFormats())}");
            }
            catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
            {
                DebugLog.PrintLine($"Clipboard formats unavailable: {ex.GetType().Name}: {ex.Message}");
            }
#endif

            var allowedEffect = GetAllowedDragEffects(CPDO);
            if (allowedEffect is DragDropEffects.None)
            {
                ClearClipboardPasteItems();
                return;
            }

            var prefDropEffect = VirtualFileDataObject.GetPreferredDropEffect(CPDO);

            // Link is only allowed depending on the target
            if (prefDropEffect.HasFlag(DragDropEffects.Copy) && allowedEffect.HasFlag(DragDropEffects.Copy))
                PasteState = DragDropEffects.Copy;
            else if (prefDropEffect.HasFlag(DragDropEffects.Move) && allowedEffect.HasFlag(DragDropEffects.Move))
                PasteState = DragDropEffects.Move;
            else if (prefDropEffect is DragDropEffects.Move && allowedEffect is DragDropEffects.Copy)
                PasteState = DragDropEffects.Copy; // fallback to copy
            else
                PasteState = DragDropEffects.None;

            Files = DragFiles;
            ParentFolder = DragParent;

            UpdateUI();
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
        {
#if !DEPLOY
            DebugLog.PrintLine($"Clipboard inspection failed: {ex.GetType().Name}: {ex.Message}");
#endif
            ClearClipboardPasteItems();
        }
    }

    private void ClearClipboardPasteItems()
    {
        VirtualFileDataObject.ReleaseClipboardData();
        PasteState = DragDropEffects.None;
        PasteSource = DataSource.None;
        Files = [];
        Descriptors = [];
        ResetCurrentFiles();
        _currentFiles = [];
        ParentFolder = "";
        SourceDevice = null;
        MasterPid = 0;
        UpdateUI();
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

        SourceDevice = Data.CurrentADBDevice.Device;
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
        if (sender is null)
            previewDataObject = null;

        if (DragFiles.Length < 1)
            return DragDropEffects.None;

        var dataContext = sender?.DataContext;
        FileClass file = dataContext is FileClass fc ? fc : null;

        if (Data.FileActions.IsAppDrive)
        {
            if (FileHelper.AllFilesAreApks(DragFiles))
                return DragDropEffects.Copy;
        }
        else if (dataContext is null || file?.IsDirectory is true)
        {
            Data.CopyPaste.DropTarget = dataContext is null
                ? Data.CurrentPath
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
        if (Data.CurrentADBDevice is null)
            return;

        if (ReferenceEquals(previewDataObject, dataObject))
            return;

        previewDataObject = dataObject;
        CurrentSource &= ~(DataSource.Android | DataSource.Self | DataSource.Virtual);

        DragParent = "";
        string[] oldFiles = [.. DragFiles];

        // ADB Drop - for all Android to Android transfers (including self)
        if (dataObject.GetDataPresent(AdbDataFormats.AdbDrop) && dataObject.GetData(AdbDataFormats.AdbDrop) is MemoryStream adbStream)
        {
            var dragList = NativeMethods.ADBDRAGLIST.FromStream(adbStream);
            var deviceId = dragList.deviceId;

            var device = Data.DevicesObject.UIList.OfType<LogicalDeviceViewModel>().FirstOrDefault(d => d.ID == deviceId && d.Status is AbstractDevice.DeviceStatus.Ok);
            if (!IsDrag && device is null)
            {
                Clear();
                return;
            }
            else
                SourceDevice = device;

            MasterPid = dragList.pid;
            DragParent = dragList.parentFolder;
            DragFiles = [.. dragList.items.Select(f => FileHelper.ConcatPaths(DragParent, f))];

            CurrentSource |= DataSource.Android;
            if (deviceId == Data.CurrentADBDevice.ID)
                CurrentSource |= DataSource.Self;
            else
                CurrentSource |= DataSource.Virtual;

            if (dataObject.GetDataPresent(AdbDataFormats.FileDescriptor))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    if (!ReferenceEquals(previewDataObject, dataObject))
                        return;

                    RunInBackgroundSta(() =>
                    {
                        var newDescriptors = FileDescriptor.GetDescriptors(dataObject);
                        if (newDescriptors is null)
                            return;

                        _ = App.Current.Dispatcher.BeginInvoke(
                            new Action(() =>
                            {
                                if (!ReferenceEquals(previewDataObject, dataObject))
                                    return;

                                Descriptors = newDescriptors;
                                UpdateUI();
                            }), DispatcherPriority.Background);
                    });
                });
            }
        }
        // Shell ID List - the only format Microsoft supports for anything added after Windows XP (non-ZIP archives, UNC paths, etc.)
        else if (dataObject.GetDataPresent(AdbDataFormats.ShellidList))
        {
            if (IsDrag)
            {
                Descriptors = [];
                DragFiles = [];

                RunInBackgroundSta(() =>
                {
                    using var shItems = ShellItemArray.FromDataObject(
                        (System.Runtime.InteropServices.ComTypes.IDataObject)dataObject);
                    if (shItems is null)
                        return;

                    var shellItems = shItems.ToArray();
                    try
                    {
                        var newDescriptors = shellItems.Select(sh => new FileDescriptor(sh)).ToArray();
                        var newFiles = shellItems.Select(sh => sh.ParsingName).ToArray();
                        var isVirtual = shellItems.Length > 0 && !shellItems[0].IsFileSystem;

                        _ = App.Current.Dispatcher.BeginInvoke(
                            new Action(() =>
                            {
                                if (!ReferenceEquals(previewDataObject, dataObject))
                                    return;

                                Descriptors = newDescriptors;
                                DragFiles = newFiles;
                                CurrentSource &= ~DataSource.Android;
                                if (isVirtual)
                                    CurrentSource |= DataSource.Virtual;
                                else
                                    CurrentSource &= ~DataSource.Virtual;

                                UpdateUI();
                            }), DispatcherPriority.Background);
                    }
                    finally
                    {
                        foreach (var shellItem in shellItems)
                            shellItem.Dispose();
                    }
                });
            }
            else
            {
                using var shItems = ShellItemArray.FromDataObject(
                    (System.Runtime.InteropServices.ComTypes.IDataObject)dataObject);
                if (shItems is null)
                {
                    Descriptors = [];
                    DragFiles = [];
                }
                else
                {
                    var shellItems = shItems.ToArray();
                    try
                    {
                        Descriptors = [.. shellItems.Select(sh => new FileDescriptor(sh))];
                        DragFiles = [.. shellItems.Select(sh => sh.ParsingName)];

                        CurrentSource &= ~DataSource.Android;
                        if (shellItems.Length > 0 && !shellItems[0].IsFileSystem)
                            CurrentSource |= DataSource.Virtual;
                    }
                    finally
                    {
                        foreach (var shellItem in shellItems)
                            shellItem.Dispose();
                    }
                }
            }
        }
        // VFDO (FileGroupDescriptor + FileContents) - the only viable format for virtual files not mapped to a drive.
        // This is the format we supply to File Explorer. Also provided by File Explorer for contents of ZIP archives (introduced in Windows ME).
        else if (dataObject.GetDataPresent(AdbDataFormats.FileDescriptor))
        {
            if (IsDrag)
            {
                Descriptors = [];
                DragFiles = [];
                var hasFileContents = dataObject.GetDataPresent(AdbDataFormats.FileContents);

                RunInBackgroundSta(() =>
                {
                    var newDescriptors = FileDescriptor.GetDescriptors(dataObject) ?? [];
                    var newFiles = newDescriptors
                        .Where(descriptor => !descriptor.Name.Contains('\\'))
                        .Select(descriptor => descriptor.Name)
                        .ToArray();

                    _ = App.Current.Dispatcher.BeginInvoke(
                        new Action(() =>
                        {
                            if (!ReferenceEquals(previewDataObject, dataObject))
                                return;

                            Descriptors = newDescriptors;
                            DragFiles = newFiles;
                            CurrentSource |= DataSource.Virtual;
                            if (hasFileContents)
                                CurrentSource &= ~DataSource.Android;

                            UpdateUI();
                        }), DispatcherPriority.Background);
                });
            }
            else
            {
                GetDescriptors(dataObject);

                DragFiles = [.. Descriptors.Where(d => !d.Name.Contains('\\')).Select(d => d.Name)];

                CurrentSource |= DataSource.Virtual;
                if (dataObject.GetDataPresent(AdbDataFormats.FileContents))
                    CurrentSource &= ~DataSource.Android;
            }
        }
        // If the data object only has FileDrop, then it's probably dropping by target detect, which we can't support (7-Zip, WinRAR, etc.)
        else
        {
            DragFiles = [];
            UpdateUI();
        }

        if (oldFiles != DragFiles && IsDrag)
            UpdateUI();
    }

    public void GetDescriptors(IDataObject dataObject)
    {
        var fds = FileDescriptor.GetDescriptors(dataObject);
        if (fds is not null)
        {
            Descriptors = fds;
            UpdateUI();
        }
    }

    public void AcceptDataObject(System.Windows.DragEventArgs e, FrameworkElement sender, bool isLink = false)
    {
        var dataContext = sender.DataContext;

        string targetFolder = dataContext is FileClass { IsDirectory: true } file
            ? file.FullPath
            : Data.CurrentPath;
        
        // Do not perform implicit duplicate by drag (only with Ctrl)
        if (IsSelf && targetFolder == DragParent && e.KeyStates is DragDropKeyStates.None)
            return;

        AcceptDataObject(e.Data, targetFolder, isLink);
    }

    public void AcceptDataObject(IDataObject dataObject, IEnumerable<FileClass> selectedFiles, bool isLink = false)
    {
        string targetFolder = selectedFiles.Count() == 1 && selectedFiles.First().IsDirectory
            ? selectedFiles.First().FullPath
            : Data.CurrentPath;
        AcceptDataObject(dataObject, targetFolder, isLink);
    }

    public void AcceptDataObject(IDataObject dataObject, string targetFolder, bool isLink = false)
    {
        var dropEffect = CurrentEffect;
        var isAppDrive = Data.FileActions.IsAppDrive;
        var allFilesAreApks = isAppDrive && FileHelper.AllFilesAreApks(DragFiles);
        var dragFromMaster = IsDragFromMaster;
        var sourceMasterPid = MasterPid;
        var targetDevice = Data.CurrentADBDevice;

        void ReadObject()
        {
            // For all cases where the files aren't immediately available on disk
            if (IsVirtual)
            {
                string tempDragPath = Data.RuntimeSettings.ResetTempDragPath();

                // Transfer from another Android device
                if (!IsWindows)
                {
                    ADBService.AdbDevice sourceDevice = new(SourceDevice);
                    var sourcePaths = DragFiles.ToArray();
                    var dispatcher = App.Current.Dispatcher;
                    _ = Task.Run(() =>
                    {
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

                            async void pullCompleted(object s, PropertyChangedEventArgs e)
                            {
                                if (e.PropertyName != nameof(FileSyncOperation.Status)
                                    || pullOp.Status is FileOperation.OperationStatus.Waiting or FileOperation.OperationStatus.InProgress)
                                    return;

                                pullOp.PropertyChanged -= pullCompleted;
                                if (pullOp.Status is not FileOperation.OperationStatus.Completed
                                    || pullOp.StatusInfo is not CompletedSyncProgressViewModel { FilesSkipped: 0 })
                                    return;

                                if (isAppDrive)
                                {
                                    if (allFilesAreApks)
                                        ShellFileOperation.PushPackages(
                                            targetDevice,
                                            transfers.Select(transfer => transfer.TargetPath),
                                            dispatcher);

                                    return;
                                }

                                var targetPaths = transfers.Select(transfer => transfer.TargetPath).ToArray();
                                var pushOps = await VerifyAndPush(
                                    targetFolder,
                                    targetPaths,
                                    targetDevice,
                                    dropEffect);

                                if (dropEffect is not DragDropEffects.Move)
                                    return;

                                var queuedItems = pushOps.Sum(op => op.IsBatch ? op.FilePath.Children.Count : 1);
                                if (queuedItems != targetPaths.Length)
                                    return;

                                await Task.WhenAll(pushOps.Select(op => Task.Run(op.WaitForCompletion)));
                                if (pushOps.Any(op => op.Status is not FileOperation.OperationStatus.Completed
                                    || op.StatusInfo is not CompletedSyncProgressViewModel { FilesSkipped: 0 }))
                                {
                                    return;
                                }

                                // Delete source roots only after every queued upload has fully succeeded.
                                _ = Task.Run(() =>
                                {
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
                                });
                            }

                            pullOp.PropertyChanged += pullCompleted;
                            pullOps.Add(pullOp);
                        }

                        if (pullOps.Count > 0 && !dispatcher.HasShutdownStarted)
                        {
                            _ = dispatcher.BeginInvoke(
                                new Action(() => Data.FileOpQ.AddOperations(pullOps)),
                                DispatcherPriority.Background);
                        }
                    });

                }
                // From archives, UNC paths, & DLNA servers
                else if (dataObject.GetDataPresent(AdbDataFormats.ShellidList))
                {
                    RunInBackgroundSta(() =>
                    {
                        using ShellFolder tempDrag = new(tempDragPath);
                        using var shItems = ShellItemArray.FromDataObject(
                            (System.Runtime.InteropServices.ComTypes.IDataObject)dataObject);
                        if (shItems is null)
                            return;

                        using ShellFileOperations shFileOp = new(NativeMethods.InterceptClipboard.MainWindowHandle);
                        var shellItems = shItems.ToArray();
                        try
                        {
                            shellItems.ForEach(shia => shFileOp.QueueCopyOperation(shia, tempDrag));

                            ShellItem lastTopItem = null;
                            ShellItem lastTopSource = null;
                            shFileOp.PostCopyItem += (s, e) =>
                            {
                                // Skip non top level items
                                if (e.DestItem.Parent.ParsingName != tempDragPath)
                                    return;

                                // A new top level item means the previous one is done
                                if (lastTopItem is not null && lastTopItem.ParsingName != e.DestItem.ParsingName)
                                {
                                    if (isAppDrive)
                                    {
                                        if (allFilesAreApks)
                                            ShellFileOperation.PushPackages(targetDevice, [lastTopItem.ParsingName], App.Current.Dispatcher);
                                    }
                                    else
                                        _ = VerifyAndPush(targetFolder, lastTopItem.ParsingName, targetDevice, dropEffect, lastTopSource);
                                }

                                lastTopItem = e.DestItem;
                                lastTopSource = e.SourceItem;
                            };

                            shFileOp.FinishOperations += (s, e) =>
                            {
                                // The last item is not caught by the PostCopyItem event
                                if (lastTopItem is not null)
                                {
                                    if (isAppDrive)
                                    {
                                        if (allFilesAreApks)
                                            ShellFileOperation.PushPackages(targetDevice, [lastTopItem.ParsingName], App.Current.Dispatcher);
                                    }
                                    else
                                        _ = VerifyAndPush(targetFolder, lastTopItem.ParsingName, targetDevice, dropEffect, lastTopSource);
                                }
                            };

                            shFileOp.PerformOperations();
                        }
                        finally
                        {
                            foreach (var shellItem in shellItems)
                                shellItem.Dispose();
                        }
                    });
                }
                // Was supposed to be the main method for zip archives, but Vanara covers that in ShellItemArray.
                // Will be left in to support any virtual files that don't provide ShellID List Array.
                else if (dataObject.GetDataPresent(AdbDataFormats.FileContents))
                {
                    var dragDescriptors = Descriptors.ToArray();
                    RunInBackgroundSta(() =>
                    {
                        string[] files = new string[dragDescriptors.Length];
                        List<FileOperation> failedOps = [];

                        for (int i = 0; i < dragDescriptors.Length; i++)
                        {
                            files[i] = FileHelper.ConcatPaths(tempDragPath, dragDescriptors[i].Name, '\\');
                            try
                            {
                                if (dragDescriptors[i].IsDirectory)
                                {
                                    Directory.CreateDirectory(files[i]);
                                    continue;
                                }

                                // Save the stream of each descriptor and release its COM storage medium.
                                Directory.CreateDirectory(FileHelper.GetParentPath(files[i]));
                                VirtualFileDataObject.SaveFileContents(dataObject, i, files[i]);

                                if (dragDescriptors[i].ChangeTimeUtc is not null)
                                    File.SetLastWriteTime(files[i], dragDescriptors[i].ChangeTimeUtc.Value.ToLocalTime());
                            }
                            catch (Exception e)
                            {
                                if (!dragDescriptors[i].IsDirectory)
                                {
                                    try
                                    {
                                        File.Delete(files[i]);
                                    }
                                    catch
                                    { }
                                }

                                files[i] = null;

                                // If failed, add a failed operation to the queue
                                failedOps.Add(
                                    new FileSyncOperation(
                                        FileOperation.OperationType.Push,
                                        dragDescriptors[i],
                                        new(targetFolder),
                                        targetDevice,
                                        new FailedOpProgressViewModel(e.Message)));

                                continue;
                            }
                        }

                        if (failedOps.Count > 0)
                            _ = App.Current.Dispatcher.BeginInvoke(
                                new Action(() => Data.FileOpQ.AddOperations(failedOps)), DispatcherPriority.Background);

                        var topLevelFiles = files.Where(d => d is not null
                            && FileHelper.GetParentPath(d) == tempDragPath
                            && (File.Exists(d) || Directory.Exists(d))).ToList();
                        
                        if (topLevelFiles.Count > 0)
                        {
                            if (isAppDrive)
                            {
                                if (allFilesAreApks)
                                    ShellFileOperation.PushPackages(targetDevice, topLevelFiles, App.Current.Dispatcher);
                            }
                            else
                                _ = VerifyAndPush(targetFolder, topLevelFiles, targetDevice, dropEffect);
                        }
                    });
                }
            }
            else if (IsWindows) // FileDrop format
            {
                if (isAppDrive)
                {
                    if (allFilesAreApks)
                        ShellFileOperation.PushPackages(targetDevice, CurrentFiles.Select(f => f.FullPath), App.Current.Dispatcher);
                }
                else
                    _ = VerifyAndPush(targetFolder, CurrentFiles, targetDevice, dropEffect);
            }
            else if (IsSelf)
            {
                // Dragging a folder into itself is not allowed
                if (DragFiles.Length == 1 && DragFiles[0] == targetFolder && IsDrag)
                    return;

                if (isAppDrive)
                {
                    if (allFilesAreApks)
                        ShellFileOperation.InstallPackages(targetDevice, CurrentFiles, App.Current.Dispatcher);
                }
                else
                {
                    var masterPid = dragFromMaster ? sourceMasterPid : 0;
                    VerifyAndPaste(isLink ? DragDropEffects.Link : dropEffect,
                               targetFolder,
                               CurrentFiles,
                               App.Current.Dispatcher,
                               targetDevice,
                               Data.CurrentPath,
                               masterPid);
                }
            }
            else
            {
                // Not supported
                return;
            }

            if (dropEffect is DragDropEffects.Move)
                Clear();
        }

        ReadObject();

        if (IsDrag)
            ClearDrag();
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
            Data.AddCommandLog($"@ADB Explorer: failed to prepare upload: {e.Message}");
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
            Data.AddCommandLog($"@ADB Explorer: failed to prepare upload: {e.Message}");
        }
    }

    public static async Task<FileSyncOperation> VerifyAndPush(string targetPath, string itemPath, ADBService.AdbDevice device, DragDropEffects dropEffects = DragDropEffects.Copy, ShellItem originalShellItem = null)
    {
        var transferred = false;
        try
        {
            var items = await MergeFiles([itemPath], targetPath, device);
            if (!items.Any())
                return null;

            var operation = await FileActionLogic.PushShellObject(itemPath, targetPath, device, dropEffects, originalShellItem);
            transferred = true;
            return operation;
        }
        catch (Exception e)
        {
            Data.AddCommandLog($"@ADB Explorer: failed to prepare upload: {e.Message}");
            return null;
        }
        finally
        {
            if (!transferred)
            {
                try
                {
                    originalShellItem?.Dispose();
                }
                catch
                { }
            }
        }
    }

    public async void VerifyAndPaste(DragDropEffects cutType,
                               string targetPath,
                               IEnumerable<FileClass> pasteItems,
                               Dispatcher dispatcher,
                               ADBService.AdbDevice device,
                               string currentPath,
                               int masterPid = 0)
    {
        pasteItems = await RemoveAncestor(pasteItems, targetPath, cutType);
        if (!pasteItems.Any())
            return;

        pasteItems = await MergeFiles(targetPath, device, pasteItems);
        if (!pasteItems.Any())
            return;

        await ShellFileOperation.MoveItems(device: device,
                  items: pasteItems,
                  targetPath: targetPath,
                  currentPath: currentPath,
                  existingItems: Data.DirList.FileList.Select(f => f.FullName),
                  dispatcher: dispatcher,
                  cutType: cutType,
                  masterPid: masterPid);
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
            var mergeTask = await App.Current.Dispatcher.InvokeAsync(() => MergeFiles(filePaths, targetPath, device));
            return await mergeTask;
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
            if (targetPath == Data.CurrentPath
                && device.ID == Data.CurrentADBDevice?.ID
                && Data.DirList is not null)
            {
                var currentFileNames = Data.DirList.FileList.Select(f => f.FullName).ToArray();
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
        if (Data.CurrentDisplayNames.TryGetValue(targetPath, out var drive))
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
            var mergeTask = await App.Current.Dispatcher.InvokeAsync(() => MergeFiles(targetPath, device, filePaths));
            return await mergeTask;
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
            if (targetPath == Data.CurrentPath
                && device.ID == Data.CurrentADBDevice?.ID
                && Data.DirList is not null)
            {
                var currentFileNames = Data.DirList.FileList.Select(f => f.FullName).ToArray();
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
        if (Data.CurrentDisplayNames.TryGetValue(targetPath, out var drive))
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
