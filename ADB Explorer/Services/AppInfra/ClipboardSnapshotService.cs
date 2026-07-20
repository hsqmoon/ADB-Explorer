namespace ADB_Explorer.Services;

internal sealed record ClipboardSnapshot(
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
    public static readonly ClipboardSnapshot Empty = new(
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

internal sealed class ClipboardSnapshotService
{
    private readonly ShellStaService shellSta;

    public ClipboardSnapshotService(ShellStaService shellSta)
    {
        this.shellSta = shellSta;
    }

    public Task<ClipboardSnapshot> ReadAsync(string currentDeviceId, CancellationToken cancellationToken) =>
        shellSta.InvokeAsync(() => Read(currentDeviceId, cancellationToken), cancellationToken);

    public Task SetAsync(VirtualFileDataObject dataObject, CancellationToken cancellationToken) =>
        shellSta.InvokeAsync(
            () =>
            {
                Clipboard.SetDataObject(dataObject);
                dataObject.AcquireClipboardOwnership();
                return true;
            },
            cancellationToken);

    public Task ClearAsync(bool clearSystemClipboard, CancellationToken cancellationToken) =>
        shellSta.InvokeAsync(
            () =>
            {
                if (clearSystemClipboard)
                    Clipboard.Clear();
                VirtualFileDataObject.ReleaseClipboardData();
                return true;
            },
            cancellationToken);

    public async Task<bool> SetTextAsync(string text, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await shellSta.InvokeAsync(
                    () =>
                    {
                        Clipboard.SetDataObject(text, true);
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x800401D0)
            {
                if (attempt == 3)
                    return false;
                await Task.Delay(20 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    public Task<string> ReadTextAsync(CancellationToken cancellationToken) =>
        shellSta.InvokeAsync(
            () =>
            {
                try
                {
                    return Clipboard.ContainsText() ? Clipboard.GetText() : "";
                }
                catch
                {
                    return "";
                }
            },
            cancellationToken);

    private static ClipboardSnapshot Read(string currentDeviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataObject = Clipboard.GetDataObject();
        if (dataObject is null)
            return ClipboardSnapshot.Empty;

        var snapshot = DropSnapshotService.Read(dataObject, currentDeviceId, cancellationToken);
        if (snapshot.Source is CopyPasteService.DataSource.None || snapshot.Files.Length == 0)
            return ClipboardSnapshot.Empty;

        return new(
            snapshot.Source,
            snapshot.ParentFolder,
            snapshot.Files,
            snapshot.Descriptors,
            snapshot.MasterPid,
            snapshot.SourceDeviceId,
            snapshot.PreferredEffect,
            snapshot.HasShellIdList,
            snapshot.HasFileContents);
    }
}
