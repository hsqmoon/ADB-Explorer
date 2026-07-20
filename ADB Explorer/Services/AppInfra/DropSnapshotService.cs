using ADB_Explorer.Helpers;
using Vanara.Windows.Shell;

namespace ADB_Explorer.Services;

internal sealed record DropSnapshot(
    CopyPasteService.DataSource Source,
    string ParentFolder,
    string[] Files,
    FileDescriptor[] Descriptors,
    int MasterPid,
    string SourceDeviceId,
    DragDropEffects PreferredEffect,
    bool HasShellIdList,
    bool HasFileContents)
{
    public static readonly DropSnapshot Empty = new(
        CopyPasteService.DataSource.None,
        "",
        [],
        [],
        0,
        null,
        DragDropEffects.None,
        false,
        false);
}

internal sealed class DropSnapshotService
{
    private readonly ShellStaService shellSta;

    public DropSnapshotService(ShellStaService shellSta)
    {
        this.shellSta = shellSta;
    }

    public Task<DropSnapshot> ReadAsync(
        IDataObject dataObject,
        string currentDeviceId,
        CancellationToken cancellationToken) =>
        shellSta.InvokeAsync(() => Read(dataObject, currentDeviceId, cancellationToken), cancellationToken);

    internal static DropSnapshot Read(
        IDataObject dataObject,
        string currentDeviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dataObject is null)
            return DropSnapshot.Empty;

        var source = (CopyPasteService.DataSource)0;
        string parent = "";
        string[] files = [];
        FileDescriptor[] descriptors = [];
        int masterPid = 0;
        string sourceDeviceId = null;
        bool hasFileContents = dataObject.GetDataPresent(AdbDataFormats.FileContents);
        bool hasShellIdList = dataObject.GetDataPresent(AdbDataFormats.ShellidList);

        if (dataObject.GetDataPresent(AdbDataFormats.AdbDrop)
            && dataObject.GetData(AdbDataFormats.AdbDrop) is MemoryStream adbStream)
        {
            var dragList = NativeMethods.ADBDRAGLIST.FromStream(adbStream);
            sourceDeviceId = dragList.deviceId;
            masterPid = dragList.pid;
            parent = dragList.parentFolder;
            files = [.. dragList.items.Select(file => FileHelper.ConcatPaths(parent, file))];
            descriptors = dataObject.GetDataPresent(AdbDataFormats.FileDescriptor)
                ? FileDescriptor.GetDescriptors(dataObject) ?? []
                : [];
            source |= CopyPasteService.DataSource.Android;
            source |= sourceDeviceId == currentDeviceId
                ? CopyPasteService.DataSource.Self
                : CopyPasteService.DataSource.Virtual;
        }
        else if (dataObject.GetDataPresent(AdbDataFormats.ShellidList))
        {
            using var shellItemArray = ShellItemArray.FromDataObject(
                (System.Runtime.InteropServices.ComTypes.IDataObject)dataObject);
            if (shellItemArray is null)
                return DropSnapshot.Empty;

            var shellItems = shellItemArray.ToArray();
            try
            {
                descriptors = new FileDescriptor[shellItems.Length];
                files = new string[shellItems.Length];
                for (int index = 0; index < shellItems.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    descriptors[index] = new(shellItems[index]);
                    files[index] = shellItems[index].ParsingName;
                }
                if (shellItems.Length > 0 && !shellItems[0].IsFileSystem)
                    source |= CopyPasteService.DataSource.Virtual;
            }
            finally
            {
                foreach (var shellItem in shellItems)
                    shellItem.Dispose();
            }
        }
        else if (dataObject.GetDataPresent(AdbDataFormats.FileDescriptor))
        {
            descriptors = FileDescriptor.GetDescriptors(dataObject) ?? [];
            files = descriptors
                .Where(descriptor => !descriptor.Name.Contains('\\'))
                .Select(descriptor => descriptor.Name)
                .ToArray();
            source |= CopyPasteService.DataSource.Virtual;
        }
        else if (dataObject.GetDataPresent(DataFormats.FileDrop)
            && dataObject.GetData(DataFormats.FileDrop) is string[] droppedFiles)
        {
            files = new string[droppedFiles.Length];
            for (int index = 0; index < droppedFiles.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                files[index] = droppedFiles[index];
            }
        }

        if (files.Length == 0)
            return DropSnapshot.Empty;

        return new(
            source,
            parent,
            files,
            descriptors,
            masterPid,
            sourceDeviceId,
            VirtualFileDataObject.GetPreferredDropEffect(dataObject),
            hasShellIdList,
            hasFileContents);
    }
}
