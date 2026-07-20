using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

public class FileChangeModifiedOperation : AbstractShellFileOperation
{
    public readonly DateTime NewDate;

    public FileChangeModifiedOperation(FileClass filePath, DateTime newDate, ADBService.AdbDevice adbDevice, Dispatcher dispatcher)
        : base(filePath, adbDevice, dispatcher)
    {
        OperationName = OperationType.Update;
        NewDate = newDate;
    }

    public override void Start()
    {
        if (Status == OperationStatus.InProgress)
        {
            throw new Exception("Cannot start an already active operation!");
        }

        Status = OperationStatus.InProgress;
        StatusInfo = new InProgShellProgressViewModel();

        _ = RunAsync(ADBService.ExecuteVoidShellCommand(Device.ID,
                                                        CancelTokenSource.Token,
                                                        "touch",
                                                        "-m",
                                                        "-t",
                                                        NewDate.ToString("yyyyMMddHHmm.ss"),
                                                        ADBService.EscapeAdbShellString(FilePath.FullPath)));
    }

    private async Task RunAsync(Task<string> operationTask)
    {
        try
        {
            string result = await operationTask.ConfigureAwait(false);
            if (CancelTokenSource?.IsCancellationRequested is true)
            {
                await CompleteAsync(OperationStatus.Canceled, new CanceledOpProgressViewModel()).ConfigureAwait(false);
                return;
            }

            await CompleteAsync(
                string.IsNullOrEmpty(result) ? OperationStatus.Completed : OperationStatus.Failed,
                string.IsNullOrEmpty(result)
                    ? new CompletedShellProgressViewModel()
                    : new FailedOpProgressViewModel(result)).ConfigureAwait(false);
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
