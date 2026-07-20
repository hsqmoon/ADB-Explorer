using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Helpers;

internal class DriveHelper
{
    public static void ClearSelectedDrives()
    {
        if (App.ActiveDevices.Current is not null)
        {
            foreach (var drive in App.ActiveDevices.Current.Drives)
                drive.DriveSelected = false;
        }
    }

    public static void ClearDrives()
    {
        var drives = App.ActiveDevices.Current?.Drives;
        drives?.Clear();
        App.FileActions.IsDriveViewVisible = false;
    }

    public static DriveViewModel GetCurrentDrive(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // First search for a non-root drive that matches the path
        var nonRoot = App.ActiveDevices.Current?.Drives.FirstOrDefault(d => d.Type is not AbstractDrive.DriveType.Root && path.StartsWith(d.Path));
        if (nonRoot is null)
            return App.ActiveDevices.Current?.Drives.FirstOrDefault(d => d.Type is AbstractDrive.DriveType.Root);

        return nonRoot;
    }
}
