using ADB_Explorer.Helpers;
using Vanara.Windows.Shell;

namespace ADB_Explorer.Services;

internal sealed record ShellMaterializationFailure(FileDescriptor Descriptor, string Message);

internal sealed record ShellMaterializationSnapshot(
    string[] TopLevelPaths,
    ShellMaterializationFailure[] Failures)
{
    public static readonly ShellMaterializationSnapshot Empty = new([], []);
}

internal sealed class ShellMaterializationService
{
    private readonly ShellStaService shellSta;

    public ShellMaterializationService(ShellStaService shellSta)
    {
        this.shellSta = shellSta;
    }

    public Task<ShellMaterializationSnapshot> MaterializeDropAsync(
        IDataObject dataObject,
        FileDescriptor[] descriptors,
        bool hasShellIdList,
        bool hasFileContents,
        string targetDirectory,
        CancellationToken cancellationToken) =>
        shellSta.InvokeAsync(
            () => Materialize(
                dataObject,
                descriptors,
                hasShellIdList,
                hasFileContents,
                targetDirectory,
                cancellationToken),
            cancellationToken);

    public Task<ShellMaterializationSnapshot> MaterializeClipboardAsync(
        FileDescriptor[] descriptors,
        bool hasShellIdList,
        bool hasFileContents,
        string targetDirectory,
        CancellationToken cancellationToken) =>
        shellSta.InvokeAsync(
            () =>
            {
                var dataObject = Clipboard.GetDataObject();
                return dataObject is null
                    ? ShellMaterializationSnapshot.Empty
                    : Materialize(
                        dataObject,
                        descriptors,
                        hasShellIdList,
                        hasFileContents,
                        targetDirectory,
                        cancellationToken);
            },
            cancellationToken);

    private static ShellMaterializationSnapshot Materialize(
        IDataObject dataObject,
        FileDescriptor[] descriptors,
        bool hasShellIdList,
        bool hasFileContents,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);

        if (hasShellIdList)
        {
            using var destination = new ShellFolder(targetDirectory);
            using var shellItemArray = ShellItemArray.FromDataObject(
                (System.Runtime.InteropServices.ComTypes.IDataObject)dataObject);
            if (shellItemArray is null)
                return ShellMaterializationSnapshot.Empty;

            var shellItems = shellItemArray.ToArray();
            try
            {
                using ShellFileOperations operations = new(NativeMethods.InterceptClipboard.MainWindowHandle);
                foreach (var shellItem in shellItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    operations.QueueCopyOperation(shellItem, destination);
                }

                operations.PerformOperations();
            }
            finally
            {
                foreach (var shellItem in shellItems)
                    shellItem.Dispose();
            }

            return new(Directory.EnumerateFileSystemEntries(targetDirectory).ToArray(), []);
        }

        if (!hasFileContents)
            return ShellMaterializationSnapshot.Empty;

        string[] paths = new string[descriptors.Length];
        List<ShellMaterializationFailure> failures = [];
        for (int i = 0; i < descriptors.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = descriptors[i];
            string path = FileHelper.ConcatPaths(targetDirectory, descriptor.Name, '\\');
            try
            {
                if (descriptor.IsDirectory)
                {
                    Directory.CreateDirectory(path);
                }
                else
                {
                    Directory.CreateDirectory(FileHelper.GetParentPath(path));
                    VirtualFileDataObject.SaveFileContents(dataObject, i, path);
                    if (descriptor.ChangeTimeUtc is not null)
                        File.SetLastWriteTimeUtc(path, descriptor.ChangeTimeUtc.Value);
                }

                paths[i] = path;
            }
            catch (Exception ex)
            {
                if (!descriptor.IsDirectory)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch
                    { }
                }

                failures.Add(new(descriptor, ex.Message));
            }
        }

        var topLevelPaths = paths
            .Where(path => path is not null
                && FileHelper.GetParentPath(path) == targetDirectory
                && (File.Exists(path) || Directory.Exists(path)))
            .ToArray();
        return new(topLevelPaths, failures.ToArray());
    }
}
