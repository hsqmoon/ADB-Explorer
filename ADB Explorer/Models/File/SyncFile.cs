using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Services;

namespace ADB_Explorer.Models;

public class SyncFile : FilePath
{
    public ObservableList<FileOpProgressInfo> ProgressUpdates { get; private set; } = [];

    public FileOpProgressInfo LastUpdate => ProgressUpdates.LastOrDefault();

    public double? CurrentPercentage => LastUpdate is AdbSyncProgressInfo adbInfo ? adbInfo.CurrentFilePercentage : null;

    public long? BytesTransferred => LastUpdate is AdbSyncProgressInfo adbInfo ? adbInfo.CurrentFileBytesTransferred : null;

    public ObservableList<SyncFile> Children { get; private set; } = [];

    public long? Size { get; set; }

    public double? UnixTime { get; set; }

    public DateTime? DateModified
    {
        get
        {
            if (!App.Settings.KeepDateModified)
                return null;

            return UnixTime.FromUnixTime();
        }
    }

    public SyncFile(string androidPath, FileType fileType = FileType.File)
        : base(androidPath, fileType: fileType)
    {

    }

    public SyncFile(FileClass fileClass, IEnumerable<(string, long?, double?)> tree = null)
        : base(fileClass.FullPath, fileClass.FullName, fileClass.Type)
    {
        Size = fileClass.Size;
        UnixTime = fileClass.ModifiedTime.ToUnixTime();

        if (tree is not null && IsDirectory)
            Children = [.. GetFolderTree(tree, FullPath)];
    }

    public static SyncFile FromWindowsPath(string path, bool includeContent = true)
    {
        var attributes = File.GetAttributes(path);
        FileSystemInfo root = attributes.HasFlag(FileAttributes.Directory)
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        return createFile(root, includeContent, false);

        static SyncFile createFile(FileSystemInfo info, bool includeChildren, bool addProgress)
        {
            var attributes = info.Attributes;
            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            SyncFile file = new(info.FullName, isDirectory ? FileType.Folder : FileType.File)
            {
                PathType = FilePathType.Windows,
                Size = isDirectory ? null : ((FileInfo)info).Length,
                UnixTime = ((DateTime?)info.LastWriteTime).ToUnixTime(),
            };

            if (addProgress)
                file.ProgressUpdates = [new AdbSyncProgressInfo(info.FullName, null, null, null)];

            if (includeChildren
                && isDirectory
                && !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                file.Children =
                [
                    .. ((DirectoryInfo)info).EnumerateFileSystemInfos()
                        .Select(child => createFile(child, true, true))
                ];
            }

            return file;
        }
    }

    static IEnumerable<SyncFile> GetFolderTree(IEnumerable<(string, long?, double?)> tree, string parent)
    {
        string rootPath = parent.Length > 1 ? parent.TrimEnd('/') : parent;
        string rootPrefix = rootPath == "/" ? rootPath : rootPath + '/';
        Dictionary<string, SyncFile> filesByPath = new(StringComparer.Ordinal);
        List<(string Path, SyncFile File)> files = [];

        foreach (var item in tree)
        {
            if (string.IsNullOrEmpty(item.Item1))
                continue;

            string fullPath = item.Item1.Length > 1 ? item.Item1.TrimEnd('/') : item.Item1;
            if (fullPath == rootPath || !fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
                continue;

            SyncFile file = new(fullPath, item.Item2 is null ? FileType.Folder : FileType.File)
            {
                Size = item.Item2,
                UnixTime = item.Item3,
                ProgressUpdates = [new AdbSyncProgressInfo(fullPath, null, null, null)]
            };

            if (filesByPath.TryAdd(fullPath, file))
                files.Add((fullPath, file));
        }

        List<SyncFile> rootChildren = [];
        foreach (var (path, file) in files)
        {
            string parentPath = FileHelper.GetParentPath(path);
            if (parentPath == rootPath)
                rootChildren.Add(file);
            else if (filesByPath.TryGetValue(parentPath, out var parentFile) && parentFile.IsDirectory)
                parentFile.Children.Add(file);
        }

        return rootChildren;
    }

    public void AddUpdates(params FileOpProgressInfo[] newUpdates)
        => AddUpdates(newUpdates.Where(o => o is not null));

    public void AddUpdates(IEnumerable<FileOpProgressInfo> newUpdates, FileOperation fileOp = null)
    {
        if (!newUpdates.Any())
            return;

        if (!IsDirectory || newUpdates.All(u => u.AndroidPath is not null && u.AndroidPath.Equals(FullPath)))
        {
            if (ProgressUpdates.Count > 0 && ProgressUpdates.Last().GetType() == newUpdates.First().GetType())
                ProgressUpdates = [.. newUpdates];
            else
                ProgressUpdates.AddRange(newUpdates);

            return;
        }

        if (fileOp is FileSyncOperation)
        {
            foreach (var update in newUpdates)
            {
                if (string.IsNullOrEmpty(update.AndroidPath))
                    update.SetPathToCurrent(fileOp);
            }
        }
        else
            newUpdates = newUpdates.Where(u => !string.IsNullOrEmpty(u.AndroidPath));

        var groups = newUpdates.GroupBy(update => DirectChildPath(update.AndroidPath));
        Dictionary<string, SyncFile> childrenByPath = new(StringComparer.Ordinal);
        foreach (var child in Children)
            childrenByPath.TryAdd(child.FullPath, child);
        
        foreach (var group in groups.Where(g => g.Key is not null))
        {
            childrenByPath.TryGetValue(group.Key, out SyncFile file);
            
            if (file is null)
            {
                var firstUpdate = group.First();
                bool isDir = !group.Key.Equals(firstUpdate.AndroidPath) || group.Key[^1] is '/' or '\\';
                file = new(group.Key, isDir ? FileType.Folder : FileType.File)
                {
                    PathType = PathType
                };

                Children.Add(file);
                childrenByPath[group.Key] = file;
            }

            file.AddUpdates(group);
        }
    }

    public string DirectChildPath(string fullPath)
        => FileHelper.DirectChildPath(FullPath, fullPath);

    public IEnumerable<SyncFile> AllChildren()
    {
        foreach (var child in Children)
        {
            yield return child;

            foreach (var grandChild in child.AllChildren())
            {
                yield return grandChild;
            }
        }
    }

    public static SyncFile MergeToWindowsPath(SyncFile syncFile, string windowsPath)
    {
        return new(FileHelper.ConcatPaths(windowsPath, syncFile.FullName, '\\'),
            syncFile.IsDirectory ? FileType.Folder : FileType.File)
        {
            PathType = FilePathType.Windows,
            Size = syncFile.Size,
            UnixTime = syncFile.UnixTime,
        };
    }

    /// <summary>
    /// Detaches progress updates and the complete child tree in constant time.
    /// </summary>
    public void ClearAll()
    {
        ProgressUpdates = [];
        Children = [];
        OnPropertyChanged(nameof(ProgressUpdates));
        OnPropertyChanged(nameof(Children));
        OnPropertyChanged(nameof(LastUpdate));
        OnPropertyChanged(nameof(CurrentPercentage));
        OnPropertyChanged(nameof(BytesTransferred));

    }
}

public class SyncFileComparer : IEqualityComparer<SyncFile>
{
    public bool Equals(SyncFile x, SyncFile y)
        => x.FullPath.Equals(y.FullPath);

    public int GetHashCode([DisallowNull] SyncFile obj)
    {
        throw new NotImplementedException();
    }
}
