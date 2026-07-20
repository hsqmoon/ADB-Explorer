using ADB_Explorer.Models;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Helpers;

public static class FolderHelper
{
    public static void CombineDisplayNames()
    {
        var driveView = AdbLocation.StringFromLocation(Navigation.SpecialLocation.DriveView);
        if (App.ExplorerState.CurrentDisplayNames.ContainsKey(driveView))
            App.ExplorerState.CurrentDisplayNames[driveView] = App.ActiveDevices.Current.Name;
        else
            App.ExplorerState.CurrentDisplayNames.Add(driveView, App.ActiveDevices.Current.Name);

        foreach (var drive in App.ActiveDevices.Current.Drives.OfType<LogicalDriveViewModel>().Where(d => d.Type
            is not AbstractDrive.DriveType.Root
            and not AbstractDrive.DriveType.Internal))
        {
            App.ExplorerState.CurrentDisplayNames.TryAdd(drive.Path, drive.Type is AbstractDrive.DriveType.External
                ? drive.ID : drive.DisplayName);
        }

        foreach (var item in AdbExplorerConst.DRIVE_TYPES.Where(d => d.Value is AbstractDrive.DriveType.Root or AbstractDrive.DriveType.Internal))
        {
            App.ExplorerState.CurrentDisplayNames.TryAdd(item.Key, AbstractDrive.GetDriveDisplayName(item.Value));
        }

        foreach (var item in AdbExplorerConst.DRIVE_TYPES)
        {
            var names = Enum.GetValues<AbstractDrive.DriveType>().Where(n => n == item.Value && item.Value
                is not AbstractDrive.DriveType.Root
                and not AbstractDrive.DriveType.Internal)
                .Select(AbstractDrive.GetDriveDisplayName);

            if (names.Any())
                App.ExplorerState.CurrentDisplayNames.TryAdd(item.Key, names.First());
        }

        (Application.Current as App)?.RequestUi(UiCommand.RefreshBreadcrumbs);
    }

    public static async Task<string> FolderExistsAsync(string path, bool showError = true)
    {
        if (path == AdbLocation.StringFromLocation(Navigation.SpecialLocation.PackageDrive))
            return path;

        if (path == AdbLocation.StringFromLocation(Navigation.SpecialLocation.RecycleBin))
            return AdbExplorerConst.RECYCLE_PATH;

        try
        {
            return await App.ActiveAdbDevice.TranslateDevicePathAsync(path).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (showError && path != AdbExplorerConst.RECYCLE_PATH)
            {
                if (Application.Current is App app)
                {
                    await app.EnqueueUiAsync(
                        "navigation.error",
                        () => DialogService.ShowMessage(
                            e.Message,
                            Strings.Resources.S_NAV_ERR_TITLE,
                            DialogService.DialogIcon.Critical,
                            copyToClipboard: true)).ConfigureAwait(false);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Retrieves the bottom-most folders from a collection of files.
    /// </summary>
    /// <remarks>A "bottom-most folder" is defined as a directory that does not contain any other directory 
    /// from the provided collection as a descendant. This method filters out parent directories to return only the
    /// deepest-level directories in the hierarchy.</remarks>
    /// <param name="files">A collection of <see cref="SyncFile"/> objects to evaluate. Each object represents a file or directory.</param>
    /// <returns>An enumerable collection of <see cref="SyncFile"/> objects representing directories that are not ancestors of
    /// any other directory in the collection.</returns>
    public static IEnumerable<SyncFile> GetBottomMostFolders(IEnumerable<SyncFile> files)
        => files.Where(file => file.IsDirectory && !file.Children.Any(child => child.IsDirectory));
}
