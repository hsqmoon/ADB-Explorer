using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Services;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;
using Vanara.PInvoke;
using Vanara.Windows.Shell;

namespace ADB_Explorer.Models;

public class FileClass : FilePath, IFileStat, IBrowserItem
{
    #region Notify Properties

    private long? size;
    public long? Size
    {
        get => size;
        set => Set(ref size, value);
    }

    private bool isLink;
    public bool IsLink
    {
        get => isLink;
        set
        {
            if (Set(ref isLink, value))
                UpdateSpecialType();
        }
    }

    private string linkTarget = "";
    public string LinkTarget
    {
        get => linkTarget;
        set => Set(ref linkTarget, value);
    }

    private FileType type;
    public FileType Type
    {
        get => type;
        set
        {
            if (Set(ref type, value))
                UpdateSpecialType();
        }
    }

    private string typeName;
    public string TypeName
    {
        get => typeName;
        private set => Set(ref typeName, value);
    }

    private DateTime? modifiedTime;
    public DateTime? ModifiedTime
    {
        get => modifiedTime;
        set
        {
            if (Set(ref modifiedTime, value))
                OnPropertyChanged(nameof(ModifiedTimeString));
        }
    }

    public double? UnixTime => ModifiedTime.ToUnixTime();

    private BitmapSource icon = null;
    public BitmapSource Icon
    {
        get => icon;
        private set => Set(ref icon, value);
    }

    private BitmapSource iconOverlay = null;
    public BitmapSource IconOverlay
    {
        get => iconOverlay;
        private set => Set(ref iconOverlay, value);
    }

    private DragDropEffects cutState = DragDropEffects.None;
    public DragDropEffects CutState
    {
        get => cutState;
        set => Set(ref cutState, value);
    }

    private TrashIndexer trashIndex;
    public TrashIndexer TrashIndex
    {
        get => trashIndex;
        set
        {
            Set(ref trashIndex, value);
            if (value is not null && value.OriginalPath is not null)
                FullName = FileHelper.GetFullName(value.OriginalPath);
        }
    }

    #endregion

    private bool extensionIsGlyph = false;
    public bool ExtensionIsGlyph
    {
        get => extensionIsGlyph;
        set => Set(ref extensionIsGlyph, value);
    }

    private bool extensionIsFontIcon = false;
    public bool ExtensionIsFontIcon
    {
        get => extensionIsFontIcon;
        set => Set(ref extensionIsFontIcon, value);
    }

    public bool IsTemp { get; set; }

    public FileNameSort SortName { get; private set; }
    private bool iconLoaded;

    public IEnumerable<FileDescriptor> Descriptors { get; private set; }

    internal void ClearDescriptors() => Descriptors = null;

    #region Read Only Properties

    public bool IsApk => AdbExplorerConst.APK_NAMES.Contains(Extension.ToUpper());

    public bool IsInstallApk => Array.IndexOf(AdbExplorerConst.INSTALL_APK, Extension.ToUpper()) > -1;

    /// <summary>
    /// Returns the extension (including the period ".") of a regular file.<br />
    /// Returns an empty string if file has no extension, or is not a regular file.
    /// </summary>
    public override string Extension => Type is FileType.File ? base.Extension : "";

    public string ShortExtension
    {
        get
        {
            return (Extension.Length > 1 && Array.IndexOf(AdbExplorerConst.UNICODE_ICONS, char.GetUnicodeCategory(Extension[1])) > -1)
                ? Extension[1..]
                : "";
        }
    }

    public string ModifiedTimeString => TabularDateFormatter.Format(ModifiedTime, Thread.CurrentThread.CurrentCulture);

    public string SizeString => IsDirectory ? "" : Size?.BytesToSize(true);

    #endregion

    public FileClass(
        string fileName,
        string path,
        FileType type,
        bool isLink = false,
        long? size = null,
        DateTime? modifiedTime = null,
        bool isTemp = false,
        bool loadIcon = true)
        : base(path, fileName, type)
    {
        Type = type;
        Size = size;
        ModifiedTime = modifiedTime;
        IsLink = isLink;

        TypeName = GetTypeName();
        if (loadIcon)
            LoadIcon();
        IsTemp = isTemp;
        
        SortName = new(fileName);
    }

    public FileClass(FileClass other)
        : this(other.FullName, other.FullPath, other.Type, other.IsLink, other.Size, other.ModifiedTime, other.IsTemp)
    { }

    public FileClass(FilePath other)
        : this(other.FullName, other.FullPath, other.IsDirectory ? FileType.Folder : FileType.File)
    { }

    public FileClass(SyncFile other)
        : this(other.FullName,
               other.FullPath,
               other.IsDirectory ? FileType.Folder : FileType.File,
               other.SpecialType.HasFlag(SpecialFileType.LinkOverlay),
               other.Size,
               other.DateModified,
               loadIcon: false)
    { }

    public FileClass(ShellItem windowsPath)
        : base(windowsPath)
    {
        Type = IsDirectory ? FileType.Folder : FileType.File;
        IsLink = windowsPath.IsLink;

        (Size, ModifiedTime) = FileHelper.GetShellSizeDate(windowsPath, IsDirectory);

        TypeName = GetTypeName();
        LoadIcon();

        SortName = new(FullName);
    }

    public FileClass(FileDescriptor fileDescriptor)
        : base(fileDescriptor.SourcePath, fileDescriptor.Name, fileDescriptor.IsDirectory ? FileType.Folder : FileType.File)
    {
        Size = fileDescriptor.Length;
        ModifiedTime = fileDescriptor.ChangeTimeUtc;
        Type = fileDescriptor.IsDirectory ? FileType.Folder : FileType.File;
    }

    public static FileClass GenerateAndroidFile(FileStat fileStat) => new(
        fileName: fileStat.FullName,
        path: fileStat.FullPath,
        type: fileStat.Type,
        size: fileStat.Size,
        modifiedTime: fileStat.ModifiedTime,
        isLink: fileStat.IsLink,
        loadIcon: false
    );

    public override void UpdatePath(string androidPath)
    {
        base.UpdatePath(androidPath);
        UpdateType();

        SortName = new(FullName);
    }

    public void UpdateType()
    {
        TypeName = GetTypeName();
        if (iconLoaded)
            GetIcon();
        OnPropertyChanged(nameof(ExtensionIsGlyph));
        OnPropertyChanged(nameof(ExtensionIsFontIcon));
    }

    public void LoadIcon()
    {
        if (iconLoaded)
            return;

        GetIcon();
        iconLoaded = true;
    }

    public void UpdateSpecialType()
    {
        SpecialType = Type switch
        {
            FileType.Folder => SpecialFileType.Folder,
            FileType.Unknown => SpecialFileType.Unknown,
            FileType.BrokenLink => SpecialFileType.BrokenLink,
            _ => SpecialFileType.Regular
        };

        if (IsApk)
            SpecialType |= SpecialFileType.Apk;

        if (IsLink && Type is not FileType.BrokenLink)
            SpecialType |= SpecialFileType.LinkOverlay;
    }

    private string GetTypeName()
    {
        var type = Type switch
        {
            FileType.File => GetTypeName(FullName),
            FileType.Folder => Strings.Resources.S_MENU_FOLDER,
            FileType.Unknown => "",
            _ => GetFileTypeName(Type),
        };

        if (IsLink && Type is not FileType.BrokenLink)
            type = string.IsNullOrEmpty(type)
                ? Strings.Resources.S_FILE_TYPE_LINK
                : string.Format(Strings.Resources.S_KNOWN_TYPE_LINK, type);

        return type;
    }

    public string GetTypeName(string fileName)
    {
        if (IsApk)
            return Strings.Resources.S_FILE_TYPE_APK;

        if (string.IsNullOrEmpty(fileName) || (IsHidden && FullName.Count(c => c == '.') == 1))
            return Strings.Resources.S_MENU_FILE;

        if (Extension.Equals(".exe", StringComparison.CurrentCultureIgnoreCase))
            return Strings.Resources.S_FILE_TYPE_EXE;

        if (!Ascii.IsValid(Extension))
        {
            if (ShortExtension.Length == 1)
                ExtensionIsGlyph = true;
            else if (ShortExtension.Length > 1)
                ExtensionIsFontIcon = true;
            else
            {
                ExtensionIsGlyph =
                ExtensionIsFontIcon = false;

                return $"{Extension[1..]} {Strings.Resources.S_MENU_FILE}";
            }

            return $"{ShortExtension} {Strings.Resources.S_MENU_FILE}";
        }
        else
        {
            ExtensionIsGlyph =
            ExtensionIsFontIcon = false;

            return NativeMethods.GetShellFileType(fileName);
        }
    }

    public FileSyncOperation PrepareDescriptors(
        VirtualFileDataObject vfdo,
        string tempDragPath,
        ADBService.AdbDevice device,
        string name,
        bool includeContent = true,
        IEnumerable<(string, long?, double?)> children = null)
    {
        SyncFile target = new(FileHelper.ConcatPaths(tempDragPath, name, '\\'))
            { PathType = FilePathType.Windows };

        if (!includeContent || !IsDirectory)
            children = null;

        FileSyncOperation fileOp = null;
        if (includeContent)
        {
            fileOp = FileSyncOperation.PullFile(new(this, children), target, device, App.Current.Dispatcher);
            fileOp.PropertyChanged += PullOperation_PropertyChanged;
            fileOp.VFDO = vfdo;
        }

        (string, long?, double?)[] items = [(name, Size, UnixTime)];
        if (includeContent && children is not null)
        {
            items = [.. items, .. children];
        }

        Descriptors = items.Select(item => new FileDescriptor
        {
            Name = FileHelper.ExtractRelativePath(item.Item1, ParentPath),
            SourcePath = FullPath,
            IsDirectory = item.Item2 is null,
            Length = item.Item2,
            ChangeTimeUtc = item.Item3.FromUnixTime(),
            Stream = () =>
            {
                if (!includeContent)
                    return null;

                var operations = vfdo.Operations;

                // When a VFDO that does not contain folders is sent to the clipboard, the shell immediately requests the file contents.
                // To prevent this, we refuse to give data when the app is focused.
                // When a legitimate request for data is made, the app can't be focused during the first file, but it can become focused again for the next files.
                lock (operations)
                {
                    var uninitiated = operations.Where(op => op.Status is FileOperation.OperationStatus.None).ToList();
                    if (Data.CopyPaste.IsClipboard
                        && uninitiated.Count > 0
                        && App.Current.Dispatcher.Invoke(() => App.Current.MainWindow.IsActive))
                    {
                        return null;
                    }

#if !DEPLOY
                    DebugLog.PrintLine($"Total uninitiated operations: {uninitiated.Count}");
#endif

                    // Add all uninitiated operations to the queue.
                    // For all consecutive files this list will be empty.
                    if (uninitiated.Count > 0)
                        App.Current.Dispatcher.Invoke(() => Data.FileOpQ.AddOperations(uninitiated));
                }

                // Wait for the operation to complete
                fileOp.WaitForCompletion();

                if (fileOp.Status is not FileOperation.OperationStatus.Completed)
                    return null;

                var file = FileHelper.ConcatPaths(tempDragPath, FileHelper.ExtractRelativePath(item.Item1, ParentPath), '\\');

                // Try 10 times to read from the file and write to the stream,
                // in case the file is still in use by ADB or hasn't appeared yet
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        var stream = NativeMethods.GetComStreamFromFile(file);

                        if (stream is not null)
                            return stream;
                    }
                    catch (Exception e)
                    {
#if !DEPLOY
                        DebugLog.PrintLine($"Failed to open stream on {file}: {e.Message}");
#endif

                        Thread.Sleep(100);
                        continue;
                    }
                }

                return null;
            }
        });

        return fileOp;
    }

    private static void PullOperation_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        var op = sender as FileSyncOperation;

        if (e.PropertyName != nameof(FileOperation.Status)
            || op.Status is FileOperation.OperationStatus.Waiting
            or FileOperation.OperationStatus.InProgress)
            return;

        var vfdo = op.VFDO;
        try
        {
            if (op.Status is FileOperation.OperationStatus.Completed
                && op.StatusInfo is CompletedSyncProgressViewModel { FilesSkipped: 0 }
                && vfdo?.CurrentEffect.HasFlag(DragDropEffects.Move) is true)
            {
                var device = op.Device;
                var sourcePath = op.FilePath.FullPath;
                var dispatcher = op.Dispatcher;

                _ = Task.Run(() =>
                {
                    op.WaitForCompletion();

                    try
                    {
                        ShellFileOperation.SilentDelete(device, sourcePath);
                    }
                    catch
                    {
                        return;
                    }

                    if (dispatcher.HasShutdownStarted)
                        return;

                    _ = dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (device.ID == Data.CurrentADBDevice?.ID
                            && FileHelper.GetParentPath(sourcePath) == Data.CurrentPath
                            && Data.DirList is not null)
                        {
                            Data.DirList.FileList.RemoveAll(f => f.FullPath == sourcePath);
                            FileActionLogic.ScheduleUpdateFileActions();
                        }
                    }), DispatcherPriority.Background);
                });

                if (vfdo.Operations.All(operation => operation.Status is FileOperation.OperationStatus.Completed))
                    _ = op.Dispatcher.BeginInvoke(
                        new Action(Data.CopyPaste.Clear), DispatcherPriority.Background);
            }
        }
        finally
        {
            op.VFDO = null;
            op.PropertyChanged -= PullOperation_PropertyChanged;
        }
    }

    public void GetIcon()
    {
        var icons = FileToIconConverter.GetImage(this, true).ToArray();
        
        if (icons.Length > 0 && icons[0] is BitmapSource icon)
            Icon = icon;
        else
            Icon = null;

        if (icons.Length > 1 && icons[1] is BitmapSource icon2)
            IconOverlay = icon2;
        else
            IconOverlay = null;
    }

    public override string ToString()
    {
        if (TrashIndex is null)
        {
            return $"{DisplayName} \n{ModifiedTimeString} \n{TypeName} \n{SizeString}";
        }
        else
        {
            return $"{DisplayName} \n{TrashIndex.OriginalPath} \n{TrashIndex.ModifiedTimeString} \n{TypeName} \n{SizeString} \n{ModifiedTimeString}";
        }
    }

    protected override bool Set<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
    {
        if (propertyName is nameof(FullName) or nameof(Type) or nameof(IsLink))
        {
            UpdateType();
        }

        return base.Set(ref storage, value, propertyName);
    }

    public static explicit operator SyncFile(FileClass self)
        => new(self.FullPath, self.Type);
}

public class FileNameSort(string name) : IComparable
{
    public string Name { get; } = name;

    public override string ToString()
    {
        return Name;
    }

    public int CompareTo(object obj)
    {
        if (obj is not FileNameSort other)
            return 0;

        return NativeMethods.StringCompareLogical(Name, other.Name);
    }
}
