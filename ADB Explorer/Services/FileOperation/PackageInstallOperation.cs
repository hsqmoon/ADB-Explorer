using ADB_Explorer.Controls;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

public class PackageInstallOperation : AbstractShellFileOperation
{
    public bool PushPackage;

    public string PackageName { get; }

    public bool IsUninstall => !string.IsNullOrEmpty(PackageName);

    public override string Tooltip => IsUninstall
        ? Strings.Resources.S_UNINSTALL
        : Strings.Resources.S_MENU_INSTALL;

    public override FrameworkElement OpIcon => IsUninstall ? new UninstallIcon() : new InstallIcon();

    public PackageInstallOperation(Dispatcher dispatcher,
                                   ADBService.AdbDevice adbDevice,
                                   FileClass path = null,
                                   string packageName = null,
                                   bool pushPackage = false) : base(path, adbDevice, dispatcher)
    {
        OperationName = OperationType.Install;
        PackageName = packageName;
        PushPackage = pushPackage;

        if (IsUninstall)
        {
            AltSource = new(Navigation.SpecialLocation.PackageDrive);
            AltTarget = new(Navigation.SpecialLocation.devNull);
        }
        else
            AltTarget = new(Navigation.SpecialLocation.PackageDrive);
    }

    public override void Start()
    {
        if (Status == OperationStatus.InProgress)
        {
            throw new Exception("Cannot start an already active operation!");
        }

        Status = OperationStatus.InProgress;
        StatusInfo = new InProgShellProgressViewModel();

        var args = new string[1];
        int index = 0;

        if (IsUninstall)
        {
            args = new string[2];
            args[0] = "uninstall";
            args[1] = PackageName;
            index = 1;
        }
        // install (pm / adb)
        else
        {
            if (!PushPackage)
            {
                args = new string[4];
                args[0] = "install";
                args[1] = "-r";
                args[2] = "-d";
                index = 3;
            }
            args[index] = FilePath.FullPath;
        }

        args[index] = PushPackage 
            ? ADBService.EscapeAdbString(args[index])
            : ADBService.EscapeAdbShellString(args[index]);

        _ = RunAsync(PushPackage
            ? ADBService.ExecuteDeviceAdbCommand(Device.ID, CancelTokenSource.Token, "install", args)
            : ADBService.ExecuteVoidShellCommand(Device.ID, CancelTokenSource.Token, "pm", args));
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
