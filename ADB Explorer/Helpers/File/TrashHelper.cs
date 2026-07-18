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
            fileList = Data.DirList.FileList;

        Data.FileActions.RestoreEnabled = fileList.Any(file => file.TrashIndex is not null && !string.IsNullOrEmpty(file.TrashIndex.OriginalPath));
        Data.FileActions.DeleteEnabled = fileList.Any(item => item.Extension != AdbExplorerConst.RECYCLE_INDEX_SUFFIX);
    }

    public static async Task UpdateRecycledItemsCount()
    {
        var device = Data.DevicesObject.Current;
        if (device is null)
            return;

        long count;
        try
        {
            count = await Task.Run(() =>
            {
                long result = ADBService.CountRecycle(device.ID);
                return result < 1
                    ? FolderHelper.FolderExists(AdbExplorerConst.RECYCLE_PATH) is null ? -1 : 0
                    : result;
            });
        }
        catch
        {
            return;
        }

        if (Data.DevicesObject.Current?.ID != device.ID
            || App.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
        {
            return;
        }

        var trash = device.Drives.Find(d => d.Type is AbstractDrive.DriveType.Trash);
        await dispatcher.InvokeAsync(
            () => ((VirtualDriveViewModel)trash)?.SetItemsCount(count),
            DispatcherPriority.Background);
    }

    public static async Task ParseIndexersAsync(ADBService.AdbDevice device)
    {
        if (device is null)
            return;

        var indexers = await GetIndexersAsync(device);
        if (Data.CurrentADBDevice?.ID != device.ID)
            return;

        void applyIndexers()
        {
            if (Data.CurrentADBDevice?.ID != device.ID)
                return;

            indexersByRecycleName = indexers
                .Where(indexer => !string.IsNullOrEmpty(indexer.RecycleName))
                .GroupBy(indexer => indexer.RecycleName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            Data.RecycleIndex.RemoveAll();
            Data.RecycleIndex.AddRange(indexers);
        }

        if (App.Current.Dispatcher.CheckAccess())
            applyIndexers();
        else
            await App.Current.Dispatcher.InvokeAsync(applyIndexers);
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
