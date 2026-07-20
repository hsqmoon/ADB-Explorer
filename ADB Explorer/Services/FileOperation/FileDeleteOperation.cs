using ADB_Explorer.Converters;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

public class FileDeleteOperation : AbstractShellFileOperation
{
    public FileDeleteOperation(Dispatcher dispatcher, ADBService.AdbDevice adbDevice, FileClass path)
        : base(path, adbDevice, dispatcher)
    {
        OperationName = OperationType.Delete;
        AltTarget = new(Navigation.SpecialLocation.devNull);
    }

    public override void Start()
    {
        if (Status == OperationStatus.InProgress)
        {
            throw new Exception("Cannot start an already active operation!");
        }

        Status = OperationStatus.InProgress;
        StatusInfo = new InProgShellProgressViewModel();

        _ = RunAsync(ADBService.ExecuteVoidShellCommand(
            Device.ID,
            CancelTokenSource.Token,
            "rm",
            "-rf",
            ADBService.EscapeAdbShellString(FilePath.FullPath)),
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
            string message = updates.LastOrDefault()?.Message ?? "";
            if (message.Contains(':'))
                message = message.Split(':').Last().TrimStart();

            var errorString = FileOpStatusConverter.StatusString(
                typeof(ShellErrorInfo),
                failed: hasChildren ? updates.Length : -1,
                message: message,
                total: true);

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
