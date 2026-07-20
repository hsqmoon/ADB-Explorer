using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Services;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;

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
    private int iconLoadScheduled;
    private int iconLoadVersion;
    private CancellationToken sessionIconCancellationToken;
    private CancellationTokenSource iconRequestCancellation;

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
            LoadIconAsync();
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
        if (iconLoaded || Volatile.Read(ref iconLoadScheduled) == 1)
        {
            iconLoaded = false;
            CancelIconLoad();

            if (Application.Current is App)
                LoadIconAsync();
        }
        OnPropertyChanged(nameof(ExtensionIsGlyph));
        OnPropertyChanged(nameof(ExtensionIsFontIcon));
    }

    public void LoadIconAsync()
    {
        if (iconLoaded || Interlocked.Exchange(ref iconLoadScheduled, 1) == 1)
            return;

        var requestCancellation = sessionIconCancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(sessionIconCancellationToken)
            : new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref iconRequestCancellation, requestCancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        int version = Volatile.Read(ref iconLoadVersion);
        string fileName = FullName;
        SpecialFileType specialType = SpecialType;

        _ = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                if (Application.Current is not App app)
                {
                    if (version == Volatile.Read(ref iconLoadVersion))
                        Interlocked.Exchange(ref iconLoadScheduled, 0);
                    return;
                }

                var icons = await app.GetFileIconsAsync(
                    fileName,
                    specialType,
                    requestCancellation.Token).ConfigureAwait(false);

                app.EnqueueUiLatest(
                    $"file.icon.{RuntimeHelpers.GetHashCode(this)}",
                    "file.icon",
                    () =>
                    {
                        if (version != Volatile.Read(ref iconLoadVersion))
                            return;

                        Interlocked.Exchange(ref iconLoadScheduled, 0);
                        ApplyIcons(icons);
                        iconLoaded = true;
                    });
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                if (version == Volatile.Read(ref iconLoadVersion))
                    Interlocked.Exchange(ref iconLoadScheduled, 0);
            }
            catch (Exception ex)
            {
                if (version == Volatile.Read(ref iconLoadVersion))
                    Interlocked.Exchange(ref iconLoadScheduled, 0);
                App.ReportBackgroundFailure(ex, "file.icon");
            }
            finally
            {
                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref iconRequestCancellation, null, requestCancellation),
                    requestCancellation))
                {
                    requestCancellation.Dispose();
                }
            }

        }
    }

    internal void SetIconCancellationToken(CancellationToken cancellationToken)
    {
        sessionIconCancellationToken = cancellationToken;
        CancelIconLoad();
    }

    internal void CancelIconLoad()
    {
        var cancellation = Interlocked.Exchange(ref iconRequestCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        Interlocked.Increment(ref iconLoadVersion);
        Interlocked.Exchange(ref iconLoadScheduled, 0);
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

                var queueCompletion = vfdo.EnsureOperationsQueued();

                // Wait for the operation to complete
                if (!fileOp.WaitForShellStreamCompletion(queueCompletion, vfdo))
                    return null;

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

                _ = DeleteMovedSourceAsync();

                async Task DeleteMovedSourceAsync()
                {
                    await op.Completion.ConfigureAwait(false);

                    try
                    {
                        await ADBService.ExecuteVoidShellCommand(
                            device.ID,
                            CancellationToken.None,
                            "rm",
                            "-rf",
                            ADBService.EscapeAdbShellString(sourcePath)).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    if (dispatcher.HasShutdownStarted || Application.Current is not App app)
                        return;

                    app.EnqueueUiLatest("virtual-files.source-removed", "virtual-files.source-removed", () =>
                    {
                        var session = App.ActiveDirectorySession;
                        if (device.ID == App.ActiveAdbDevice?.ID
                            && FileHelper.GetParentPath(sourcePath) == App.ExplorerState.CurrentPath
                            && session is not null)
                        {
                            session.RemoveItems(session.FileList.Where(f => f.FullPath == sourcePath));
                            FileActionLogic.ScheduleUpdateFileActions();
                        }
                    });
                }

                if (vfdo.AreOperationsCompleted && Application.Current is App app)
                {
                    app.EnqueueUiLatest(
                        "virtual-files.clipboard-complete",
                        "virtual-files.clipboard-complete",
                        App.CopyPaste.Clear);
                }
            }
        }
        finally
        {
            op.VFDO = null;
            op.PropertyChanged -= PullOperation_PropertyChanged;
        }
    }

    private void ApplyIcons(IReadOnlyList<BitmapSource> icons)
    {
        if (icons.Count > 0 && icons[0] is BitmapSource icon)
            Icon = icon;
        else
            Icon = null;

        if (icons.Count > 1 && icons[1] is BitmapSource icon2)
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
