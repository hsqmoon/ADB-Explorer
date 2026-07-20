using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

public class FileMoveOperation : AbstractShellFileOperation
{
    public string RecycleName;
    public string IndexerPath;
    public DateTime? DateModified;
    public readonly bool isLink;

    public FileMoveOperation(FileClass filePath, SyncFile targetPath, ADBService.AdbDevice adbDevice, Dispatcher dispatcher, DragDropEffects cutType = DragDropEffects.None)
        : base(filePath, adbDevice, dispatcher)
    {
        if (cutType is DragDropEffects.Copy or DragDropEffects.Link)
            OperationName = OperationType.Copy;
        else if (targetPath.FullPath.StartsWith(AdbExplorerConst.RECYCLE_PATH))
        {
            OperationName = OperationType.Recycle;
            AltTarget = new(Navigation.SpecialLocation.RecycleBin);
        }
        else if (filePath.TrashIndex is not null)
        {
            OperationName = OperationType.Restore;
            AltSource = new(Navigation.SpecialLocation.RecycleBin);
        }
        else
            OperationName = OperationType.Move;

        TargetPath = targetPath;
        isLink = cutType is DragDropEffects.Link;
    }

    public override void Start()
    {
        if (Status == OperationStatus.InProgress)
        {
            throw new Exception("Cannot start an already active operation!");
        }

        Status = OperationStatus.InProgress;
        StatusInfo = new InProgShellProgressViewModel();

        if (OperationName is OperationType.Recycle)
        {
            RecycleName = $"{{{DateTimeOffset.Now.ToUnixTimeMilliseconds()}}}";
            TargetPath.UpdatePath(FileHelper.ConcatPaths(TargetPath.ParentPath, RecycleName));
            IndexerPath = $"{AdbExplorerConst.RECYCLE_PATH}/.{RecycleName}{AdbExplorerConst.RECYCLE_INDEX_SUFFIX}";
        }
        else if (OperationName is OperationType.Restore)
        {
            RecycleName = FilePath.TrashIndex.RecycleName;
            IndexerPath = FilePath.TrashIndex.IndexerPath;
        }

        var cmd = OperationName is OperationType.Copy ? "cp" : "mv";
        var flag = "";

        if (OperationName is OperationType.Copy)
        {
            flag += "p"; // Preserve timestamps, ownership, and mode
            if (FilePath.IsDirectory)
                flag += "r"; // Recurse into subdirectories (DEST must be a directory)
        }

        if (isLink)
            flag += "s"; // Symlink instead of copy

        if (flag.Length > 0)
            flag = "-" + flag;

        if (OperationName is OperationType.Copy or OperationType.Recycle)
            DateModified = DateTime.Now;

        _ = RunAsync(ADBService.ExecuteVoidShellCommand(Device.ID, CancelTokenSource.Token, cmd, flag,
            ADBService.EscapeAdbShellString(FilePath.FullPath),
            ADBService.EscapeAdbShellString(TargetPath.FullPath)),
            Children.Count > 0);
    }

    private async Task RunAsync(Task<string> operationTask, bool hasChildren)
    {
        try
        {
            string result = await operationTask.ConfigureAwait(false);
            if (CancelTokenSource?.IsCancellationRequested is true)
            {
                await CompleteAsync(OperationStatus.Canceled, new CanceledOpProgressViewModel()).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(result))
            {
                await CompleteAsync(OperationStatus.Completed, new CompletedShellProgressViewModel()).ConfigureAwait(false);
                return;
            }

            var updates = AdbRegEx.RE_SHELL_ERROR()
                .Matches(result)
                .Where(match => match.Success)
                .Select(match => new ShellErrorInfo(match, FilePath.FullPath))
                .ToArray();
            string message = updates.LastOrDefault()?.Message ?? result;
            if (message.Contains(':'))
                message = message.Split(':').Last().TrimStart();

            var errorString = FileOpStatusConverter.StatusString(
                typeof(ShellErrorInfo),
                failed: hasChildren ? updates.Length : -1,
                message: message,
                total: true);

            if (OperationName is OperationType.Recycle)
            {
                await ADBService.ExecuteVoidShellCommand(
                    Device.ID,
                    CancellationToken.None,
                    "rm",
                    "-rf",
                    ADBService.EscapeAdbShellString(TargetPath.FullPath),
                    ADBService.EscapeAdbShellString(IndexerPath)).ConfigureAwait(false);
            }

            await CompleteAsync(
                OperationStatus.Failed,
                new FailedOpProgressViewModel(errorString),
                () => AddUpdates(updates)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CompleteAsync(OperationStatus.Canceled, new CanceledOpProgressViewModel()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CompleteAsync(OperationStatus.Failed, new FailedOpProgressViewModel(ex.GetBaseException().Message)).ConfigureAwait(false);
        }
    }
}
