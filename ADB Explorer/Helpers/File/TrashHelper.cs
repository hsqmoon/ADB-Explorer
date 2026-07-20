using ADB_Explorer.Models;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Helpers;

internal static class TrashHelper
{
    private static IReadOnlyDictionary<string, TrashIndexer> indexersByRecycleName =
        new Dictionary<string, TrashIndexer>();

    public static TrashIndexer FindIndexer(string recycleName)
        => indexersByRecycleName.GetValueOrDefault(recycleName);

    public static void EnableRecycleButtons(IEnumerable<FileClass> fileList = null)
    {
        if (fileList is null)
            fileList = (IEnumerable<FileClass>)(Application.Current as App)?.CurrentDirectorySession?.FileList
                ?? Array.Empty<FileClass>();

        App.FileActions.RestoreEnabled = fileList.Any(file => file.TrashIndex is not null && !string.IsNullOrEmpty(file.TrashIndex.OriginalPath));
        App.FileActions.DeleteEnabled = fileList.Any(item => item.Extension != AdbExplorerConst.RECYCLE_INDEX_SUFFIX);
    }

    public static async Task UpdateRecycledItemsCount()
    {
        var device = App.ActiveDevices.Current;
        var adbDevice = App.ActiveAdbDevice;
        if (device is null || adbDevice is null)
            return;

        long count;
        try
        {
            ulong rawResult = await adbDevice.CountFilesAsync(
                AdbExplorerConst.RECYCLE_PATH,
                includeNames: null,
                excludeNames: ["*" + AdbExplorerConst.RECYCLE_INDEX_SUFFIX],
                cancellationToken: DeviceHelper.GetCurrentDeviceWorkToken());
            long result = rawResult <= long.MaxValue ? (long)rawResult : long.MaxValue;
            count = result < 1
                ? (await FolderHelper.FolderExistsAsync(
                    AdbExplorerConst.RECYCLE_PATH,
                    showError: false).ConfigureAwait(false)) is null ? -1 : 0
                : result;
        }
        catch
        {
            return;
        }

        if (!ReferenceEquals(App.ActiveDevices.Current, device)
            || !ReferenceEquals(App.ActiveAdbDevice, adbDevice)
            || Application.Current is not App app)
        {
            return;
        }

        await app.EnqueueUiAsync("trash.count", () =>
        {
            if (!ReferenceEquals(App.ActiveDevices.Current, device)
                || !ReferenceEquals(App.ActiveAdbDevice, adbDevice))
                return;

            var trash = device.Drives.Find(d => d.Type is AbstractDrive.DriveType.Trash);
            ((VirtualDriveViewModel)trash)?.SetItemsCount(count);
        });
    }

    public static async Task ParseIndexersAsync(ADBService.AdbDevice device)
    {
        if (device is null)
            return;

        var indexers = await GetIndexersAsync(device);
        if (!ReferenceEquals(App.ActiveAdbDevice, device))
            return;

        void applyIndexers()
        {
            if (!ReferenceEquals(App.ActiveAdbDevice, device))
                return;

            indexersByRecycleName = indexers
                .Where(indexer => !string.IsNullOrEmpty(indexer.RecycleName))
                .GroupBy(indexer => indexer.RecycleName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            App.ExplorerState.RecycleIndex = new(indexers);
        }

        if (Application.Current is App app)
            await app.EnqueueUiAsync("trash.indexers", applyIndexers);
    }

    public static Task<List<TrashIndexer>> GetIndexersAsync(ADBService.AdbDevice device) => Task.Run(() =>
    {
        if (device is null)
            return new List<TrashIndexer>();

        var indexers = ADBService.FindFilesInPath(device.ID,
                                                  AdbExplorerConst.RECYCLE_PATH,
                                                  includeNames: ["*" + AdbExplorerConst.RECYCLE_INDEX_SUFFIX]);

        var lines = ShellFileOperation.ReadAllText(device, indexers).Split(ADBService.LINE_SEPARATORS,
                                                                                          StringSplitOptions.RemoveEmptyEntries);

        return lines.Select(line => new TrashIndexer(line)).ToList();
    });

}
