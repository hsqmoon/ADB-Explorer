using ADB_Explorer.Converters;
using ADB_Explorer.Models;
using ADB_Explorer.Services;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Explorer.Helpers;

public static class FileHelper
{
    private const int FIND_PATH_BATCH_LENGTH = 8_000;

    public static FileClass ListerFileManipulator(FileClass item)
    {
        if (App.CopyPaste.Files.Length > 0
            && App.CopyPaste.IsSelfClipboard
            && App.CopyPaste.ParentFolder == App.ExplorerState.CurrentPath
            && App.CopyPaste.FileSet.Contains(item.FullPath))
        {
            item.CutState = App.CopyPaste.PasteState;
            App.CopyPaste.MarkCutItem(item);
        }

        if (App.FileActions.IsRecycleBin)
        {
            var indexer = TrashHelper.FindIndexer(item.FullName);
            if (indexer is not null)
            {
                item.TrashIndex = indexer;
                item.UpdateType();
            }
        }

        return item;
    }

    public static Predicate<object> HideFiles() => file =>
    {
        if (file is not FileClass fileClass)
            return false;

        if (fileClass.IsHidden)
            return false;

        return !IsHiddenRecycleItem(fileClass);
    };

    public static Predicate<object> PkgFilter() => pkg =>
    {
        if (pkg is not Package)
            return false;
        
        return string.IsNullOrEmpty(App.FileActions.ExplorerFilter)
            || pkg.ToString().Contains(App.FileActions.ExplorerFilter, StringComparison.OrdinalIgnoreCase);
    };

    public static bool IsHiddenRecycleItem(FileClass file)
    {
        if (AdbExplorerConst.POSSIBLE_RECYCLE_PATHS.Contains(file.FullPath) || file.Extension == AdbExplorerConst.RECYCLE_INDEX_SUFFIX)
            return true;
        
        if (!string.IsNullOrEmpty(App.FileActions.ExplorerFilter) && !file.ToString().Contains(App.FileActions.ExplorerFilter, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static void RenameFile(FileClass file, string newName)
    {
        var newPath = ConcatPaths(file.ParentPath, newName);
        if (!App.Settings.ShowExtensions)
            newPath += file.Extension;

        ShellFileOperation.Rename(file, newPath, App.ActiveAdbDevice);
    }

    public static string DisplayName(TextBox textBox) => DisplayName(textBox.DataContext as FilePath);

    public static string DisplayName(FilePath file) => App.Settings.ShowExtensions ? file?.FullName : file?.NoExtName;

    public static FileClass GetFromCell(DataGridCellInfo cell) => CellConverter.GetDataGridCell(cell).DataContext as FileClass;

    public static string ConcatPaths(FilePath path1, string path2) => 
        ConcatPaths(path1.FullPath, path2, path1.PathType is FilePathType.Android ? '/' : '\\');

    public static string ConcatPaths(string path1, string path2, char separator = '/')
    {
        string result = $"{path1.TrimEnd('/', '\\')}{separator}{path2.TrimStart('/', '\\')}";

        return result.Replace(separator is '/' ? '\\' : '/', separator);
    }

    public static string ExtractRelativePath(string fullPath, string parent, bool includeSelf = true)
    {
        if (fullPath == parent)
        {
            return includeSelf
                ? GetFullName(fullPath)
                : $"{GetSeparator(fullPath)}";
        }

        var index = fullPath.IndexOf(parent);
        
        var result = index < 0
            ? fullPath
            : fullPath[parent.Length..];

        return result.TrimStart('/', '\\');
    }

    public static string GetParentPath(string fullPath)
    {
        var index = LastSeparatorIndex(fullPath);
        if (index.Value == 0)
            index = 1;

        return fullPath[..index];
    }

    public static string GetShortFileName(string fullName, int length = -1)
    {
        if (string.IsNullOrEmpty(fullName))
            return fullName;

        var name = GetFullName(fullName);
        if (length < 0)
            return name;

        return name.Length > length ? name[..length] + "…" : name;
    }

    public static string GetFullName(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return fullPath;

        var separator = GetSeparator(fullPath);
        if (fullPath.IndexOf(separator) == fullPath.Length - 1)
            return fullPath;

        fullPath = fullPath.TrimEnd(separator);
        var index = LastSeparatorIndex(fullPath);

        return index.IsFromEnd
            ? fullPath
            : fullPath[(index.Value + 1)..];
    }

    public static char GetSeparator(string path)
    {
        if (path.Contains('/'))
            return '/';

        return path.Contains('\\')
            ? '\\'
            : '\0';
    }

    public static Index LastSeparatorIndex(string path)
        => IndexAdjust(path.LastIndexOf(GetSeparator(path)));

    public static Index NextSeparatorIndex(string parentPath, string childPath)
        => IndexAdjust(childPath.IndexOf(GetSeparator(childPath), parentPath.Length + 1));

    public static Index IndexAdjust(int originalIndex) => originalIndex switch
    {
        < 0 => ^0,
        _ => originalIndex,
    };

    public static string DirectChildPath(string parentPath, string childPath)
    {
        if (childPath is null || !childPath.Contains(parentPath) || childPath.Length - parentPath.Length < 2)
            return null;

        var index = NextSeparatorIndex(parentPath, childPath);
        return childPath[..index];
    }

    public static long TotalSize(IEnumerable<FileClass> files)
    {
        if (files.Any(f => f.Type is not FileType.File || f.IsLink))
            return 0;

        return files.Select(f => f.Size.GetValueOrDefault(0)).Sum();
    }

    /// <summary>
    /// Returns the extension (including the period ".").<br />
    /// Returns an empty string if file has no extension.
    /// </summary>
    public static string GetExtension(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        if (lastDot < 1)
            return "";

        var secondLast = fullName[..lastDot].LastIndexOf('.');

        if (secondLast > 0 && fullName[(secondLast + 1)..lastDot] == "tar")
            return fullName[secondLast..];

        return fullName[lastDot..];
    }

    public static string DuplicateFile(IEnumerable<FileClass> fileList, string fullName, DragDropEffects cutType = DragDropEffects.None)
        => DuplicateFile(fileList.Select(f => f.FullName), fullName, cutType);

    public static string DuplicateFile(IEnumerable<string> fileList, string fullName, DragDropEffects cutType = DragDropEffects.None)
    {
        // None - new file
        var copySuffix = cutType is DragDropEffects.None ? "" : Strings.Resources.S_ITEM_COPY.Replace("{0}", "");
        var extension = GetExtension(fullName);
        var noExtName = fullName[..^extension.Length];

        // Has the exact same name before the suffix, optionally has the suffix which is optionally iterated, and has the exact same extension
        Regex re = new($"^{noExtName}(?:{copySuffix}(?: (?<iter>\\d+))?)?{extension.Replace(".", "\\.")}$");

        var items = from i in fileList
                    let match = re.Match(i)
                    where match.Success
                    select match.Groups["iter"].Value;

        return $"{noExtName}{ExistingIndexes(items, copySuffix)}{extension}";
    }

    public static string ExistingIndexes(IEnumerable<string> suffixes, string copySuffix = "")
    {
        var indexes = (from i in suffixes
                       where int.TryParse(i, out _)
                       select int.Parse(i)).ToList();
        if (suffixes.Any(s => s == ""))
            indexes.Add(0);

        indexes.Sort();
        if (indexes.Count == 0 || indexes[0] != 0)
            return "";

        var result = "";
        for (int i = 0; i < indexes.Count; i++)
        {
            if (indexes[i] <= i)
                continue;

            result = $"{copySuffix} {i}";
            break;
        }
        if (result == "")
            result = $"{copySuffix} {indexes.Count}";

        return result;
    }

    public enum RenameTarget
    {
        Unix,
        FUSE,
        Windows,
        WinRoot,
    }

    private static readonly Func<string, bool> FileNamePredicateWindows = (name) => 
        !AdbExplorerConst.INVALID_WINDOWS_FILENAMES.Contains(name)
        && !name.Any(c => AdbExplorerConst.INVALID_NTFS_CHARS.Any(chr => chr == c))
        && name.Length > 0
        && name[^1] is not ' ' and not '.'
        && name[0] is not ' ';

    private static readonly Func<string, bool> FileNamePredicateWinRoot = (name) =>
        !AdbExplorerConst.INVALID_WINDOWS_ROOT_PATHS.Contains(name)
        && !AdbExplorerConst.INVALID_WINDOWS_FILENAMES.Contains(name)
        && !name.Any(c => AdbExplorerConst.INVALID_NTFS_CHARS.Any(chr => chr == c))
        && name.Length > 0
        && name[^1] is not ' ' and not '.'
        && name[0] is not ' ';

    private static readonly Func<string, bool> FileNamePredicateFuse = (name) =>
        !name.Any(c => AdbExplorerConst.INVALID_NTFS_CHARS.Contains(c))
        && name.Length > 0
        && name is not "." and not "..";

    private static readonly Func<string, bool> FileNamePredicateUnix = (name) =>
        name.Length > 0
        && name is not "." and not "..";

    public static bool FileNameLegal(string fileName, RenameTarget target)
        => FileNameLegal([fileName], target);

    public static bool FileNameLegal(IEnumerable<FilePath> files, RenameTarget target)
        => FileNameLegal(files.Select(f => f.FullName), target);

    public static bool FileNameLegal(IEnumerable<string> names, RenameTarget target)
    {
        var predicate = target switch
        {
            RenameTarget.Unix => FileNamePredicateUnix,
            RenameTarget.FUSE => FileNamePredicateFuse,
            RenameTarget.Windows => FileNamePredicateWindows,
            RenameTarget.WinRoot => FileNamePredicateWinRoot,
            _ => throw new NotSupportedException(),
        };

        return names.AnyAll(predicate);
    }

    public static string[] ApkExtensions => [.. AdbExplorerConst.APK_NAMES.Select(n => n[1..])];

    public static bool AllFilesAreApks(string[] items) =>
        items.AnyAll(i => i.Contains('.') && ApkExtensions.Any(n => n == i.Split('.').Last().ToUpper()));

    /// <summary>
    /// Returns the relation of <paramref name="other"/> to <paramref name="self"/>.<br />
    /// Example: RelationFrom(File, File.Parent) = Ancestor</summary>
    /// <param name="self"></param>
    /// <param name="other"></param>
    /// <returns></returns>
    public static RelationType RelationFrom(string self, string other)
    {
        var separator = GetSeparator(self);

        if (other == self)
            return RelationType.Self;

        if (other.StartsWith(self) && other[self.Length..(self.Length + 1)][0] == separator)
            return RelationType.Descendant;

        if (self.StartsWith(other))
            return RelationType.Ancestor;

        return RelationType.Unrelated;
    }

    public static IEnumerable<FileClass> GetFilesFromTree((string, long?, double?)[] tree) => 
        tree.Select(t => new FileClass(GetFullName(t.Item1), t.Item1, t.Item2 is null ? FileType.Folder : FileType.File, size: t.Item2)
        { ModifiedTime = t.Item3.FromUnixTime() });

    public static (string, long?, double?)[] GetFolderTree(
        IEnumerable<string> paths,
        ADBService.AdbDevice device,
        bool isFolder = true,
        CancellationToken cancellationToken = default)
    {
        string stdout = "";
        var files = string.Join(" ", paths.Select(p => ADBService.EscapeAdbShellString(p)));
        var depth = isFolder ? "-mindepth 1" : "";

        if (ShellCommands.SupportsFindPrintf(device.ID))
        {
            // get absolute paths, sizes and dates of all files, directries are marked with 'd'
            string[] args =
            [
                files,
                depth,
                """\( -type d -printf '/// %p /// d /// d ///\n' \)""",
                "-o",
                $"""\( -type f -printf '/// %p /// %s /// {(App.Settings.KeepDateModified ? "%T@" : "d")} ///\n' \)""",
                "2>&1"
            ];

            ADBService.ExecuteDeviceAdbShellCommand(device.ID, "find", out stdout, out _, cancellationToken, args);
        }
        else // when find does not support -printf
        {
            // gives the same result, but much slower since it executes stat on each file *after* performing find
            string[] args =
            [
                files,
                depth,
                """2>/dev/null | while IFS= read -r f;""",
                """do if [ -d \"$f\" ]; then""",
                """echo /// $f /// d /// d ///;""",
                $"""else echo /// $f /// $(stat -c '%s /// %Y' {(App.Settings.KeepDateModified ? "\\\"$f\\\")" : ") d")} ///;""",
                """fi; done;"""
            ];

            ADBService.ExecuteDeviceAdbShellCommand(device.ID, "find", out stdout, out _, cancellationToken, args);
        }
        var matches = AdbRegEx.RE_FIND_TREE().Matches(stdout);

        return [.. matches.Where(m => m.Success)
                .Select(m =>
                (
                    m.Groups["Name"].Value,
                    m.Groups["Size"].Value == "d" ? (long?)null : long.Parse(m.Groups["Size"].Value, CultureInfo.InvariantCulture),
                    m.Groups["Date"].Value == "d" ? (double?)null : double.Parse(m.Groups["Date"].Value, CultureInfo.InvariantCulture)
                ))];
    }

    internal static Dictionary<string, List<(string, long?, double?)>> GetFolderTrees(
        IEnumerable<string> paths,
        ADBService.AdbDevice device,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, List<(string, long?, double?)>> treesBySource = new(StringComparer.Ordinal);
        foreach (var path in paths.Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.Ordinal))
            treesBySource.Add(path, []);

        if (treesBySource.Count == 0)
            return treesBySource;

        List<string> batch = [];
        int batchLength = 0;

        void readBatch()
        {
            if (batch.Count == 0)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            List<(string, long?, double?)> currentTree = null;
            foreach (var treeItem in GetFolderTree(batch, device, false, cancellationToken))
            {
                if (treesBySource.TryGetValue(treeItem.Item1, out var rootTree))
                    currentTree = rootTree;

                currentTree?.Add(treeItem);
            }

            batch.Clear();
            batchLength = 0;
        }

        foreach (var path in treesBySource.Keys)
        {
            int pathLength = ADBService.EscapeAdbShellString(path).Length + 1;
            if (batch.Count > 0 && batchLength + pathLength > FIND_PATH_BATCH_LENGTH)
                readBatch();

            batch.Add(path);
            batchLength += pathLength;
        }

        readBatch();
        return treesBySource;
    }

    /// <summary>
    /// Resolves an executable name (e.g. "adb") to its full path by searching the system PATH directories.
    /// </summary>
    /// <returns>The full path to the executable if found; otherwise, <see langword="null"/>.</returns>
    public static string ResolveExecutableFromPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        // PATHEXT defines executable extensions to try (e.g. ".EXE;.CMD")
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        bool hasExtension = Path.HasExtension(fileName);

        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (hasExtension)
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            else
            {
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(dir, fileName + ext);
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }
}
