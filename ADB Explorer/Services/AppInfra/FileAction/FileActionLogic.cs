using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using Vanara.Windows.Shell;
using System.Windows.Threading;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Explorer.Services.AppInfra;

internal static class FileActionLogic
{
    private static readonly object DriveRefreshLock = new();
    private static readonly HashSet<string> ActiveDriveRefreshes = [];
    private static readonly Dictionary<string, DateTime> LastDriveRefresh = [];
    private static readonly object PushedFilesLock = new();
    private static readonly Dictionary<
        (string DeviceId, string ParentPath, string FullName),
        (string FullPath, FileType Type, long? Size, DateTime? Modified)> PendingPushedFiles = [];
    private static bool PushedFilesUpdateScheduled;
    private static int fileActionsRefreshScheduled;
    private static int packageRefreshVersion;

    private static string RemoveApkMessage(IEnumerable<IBrowserItem> objects)
    {
        var count = objects.Count();

        if (count == 1)
            return string.Format(Strings.Resources.S_REM_APK, objects.First().DisplayName);

        return string.Format(Strings.Resources.S_REM_APK_PLURAL, count);
    }

    public static async void UninstallPackages()
    {
        var pkgs = Data.SelectedPackages;
        var files = Data.SelectedFiles;

        var result = await DialogService.ShowConfirmation(
            RemoveApkMessage(Data.FileActions.IsAppDrive ? pkgs : files),
            Strings.Resources.S_CONF_UNI_TITLE,
            Strings.Resources.S_UNINSTALL,
            icon: DialogService.DialogIcon.Exclamation);

        if (result.Item1 is not ContentDialogResult.Primary)
            return;

        var packageTask = await Task.Run(() =>
        {
            if (Data.FileActions.IsAppDrive)
                return pkgs.Select(pkg => pkg.Name);

            return files.Select(item => ShellFileOperation.GetPackageName(Data.CurrentADBDevice, item.FullPath));
        });

        ShellFileOperation.UninstallPackages(Data.CurrentADBDevice, packageTask, App.Current.Dispatcher);
    }

    public static void InstallPackages()
    {
        var packages = Data.SelectedFiles;

        ShellFileOperation.InstallPackages(Data.CurrentADBDevice, packages, App.Current.Dispatcher);
    }

    public static void CopyToTemp()
    {
        Data.CopyPaste.VerifyAndPaste(
            DragDropEffects.Copy,
            AdbExplorerConst.TEMP_PATH,
            Data.SelectedFiles,
            App.Current.Dispatcher,
            Data.CurrentADBDevice,
            Data.CurrentPath);
    }

    public static void PushPackages()
    {
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = false,
            Multiselect = true,
            DefaultDirectory = Data.Settings.DefaultFolder,
            Title = Strings.Resources.S_INSTALL_APK,
        };
        dialog.Filters.Add(new(Strings.Resources.S_FILE_TYPE_APK, string.Join(';', AdbExplorerConst.INSTALL_APK.Select(name => name[1..]))));

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            return;

        ShellFileOperation.PushPackages(Data.CurrentADBDevice, dialog.FileNames, App.Current.Dispatcher);
    }

    public static void UpdateModifiedDates()
    {
        ShellFileOperation.ChangeDateFromName(Data.CurrentADBDevice, Data.SelectedFiles, App.Current.Dispatcher);
    }

    public static void OpenEditor()
    {
        if (!Data.FileActions.EditFileEnabled || Data.FileActions.IsEditorOpen && Data.FileActions.EditorAndroidPath.Equals(Data.SelectedFiles.First()))
        {
            Data.FileActions.IsEditorOpen = false;
            return;
        }
        Data.FileActions.IsEditorOpen = true;

        Data.FileActions.EditorAndroidPath = Data.SelectedFiles.First();
        
        var readTask = Task.Run(() =>
        {
            try
            {
                return AdbHelper.ReadFile(Data.CurrentADBDevice, Data.FileActions.EditorAndroidPath.FullPath);
            }
            catch (Exception e)
            {
                _ = App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    DialogService.ShowMessage(e.Message, Strings.Resources.S_READ_FILE_ERROR_TITLE, DialogService.DialogIcon.Exclamation, copyToClipboard: true)));

                return "";
            }
        });

        readTask.ContinueWith((t) =>
        {
            _ = App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                Data.FileActions.EditorText =
                Data.FileActions.OriginalEditorText = t.Result;
            }));
        });
    }

    public static void SaveEditorText()
    {
        var writeTask = Task.Run(() =>
        {
            try
            {
                AdbHelper.WriteFile(Data.CurrentADBDevice, Data.FileActions.EditorAndroidPath.FullPath, Data.FileActions.EditorText);
                return true;
            }
            catch (Exception e)
            {
                _ = App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    DialogService.ShowMessage(e.Message, Strings.Resources.S_WRITE_FILE_ERROR_TITLE, DialogService.DialogIcon.Exclamation, copyToClipboard: true)));

                return false;
            }
        });

        writeTask.ContinueWith((t) =>
        {
            _ = App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!t.Result)
                    return;

                Data.FileActions.OriginalEditorText = Data.FileActions.EditorText;

                if (Data.FileActions.EditorAndroidPath.ParentPath == Data.CurrentPath)
                    Data.RuntimeSettings.Refresh = true;
            }));
        });
    }

    public static async void RestoreItems()
    {
        var device = Data.CurrentADBDevice;
        var lister = Data.DirList;
        var currentPath = Data.CurrentPath;
        if (device is null || lister is null)
            return;

        var restoreItems = (!Data.SelectedFiles.Any() ? lister.FileList : Data.SelectedFiles)
            .Where(file => file.TrashIndex is not null
                && !string.IsNullOrEmpty(file.TrashIndex.OriginalPath))
            .ToList();
        if (restoreItems.Count == 0)
            return;

        HashSet<FileClass> conflictingItems;
        bool merge;
        try
        {
            (conflictingItems, merge) = await Task.Run(() =>
            {
                var existingPaths = (ADBService.FindFiles(
                        device.ID,
                        restoreItems.Select(file => file.TrashIndex.OriginalPath)) ?? [])
                    .ToHashSet(StringComparer.Ordinal);
                HashSet<FileClass> conflicts = restoreItems
                    .Where(item => existingPaths.Contains(item.TrashIndex.OriginalPath))
                    .ToHashSet();

                foreach (var group in restoreItems.GroupBy(
                    item => item.TrashIndex.OriginalPath,
                    StringComparer.Ordinal))
                {
                    conflicts.UnionWith(group.Skip(1));
                }

                return (conflicts, conflicts.Any(item => item.IsDirectory));
            });
        }
        catch (Exception e)
        {
            Data.AddCommandLog($"@ADB Explorer: failed to check restore conflicts: {e.Message}");
            return;
        }

        if (conflictingItems.Count > 0)
        {
            var result = await DialogService.ShowConfirmation(
                conflictingItems.Count == 1
                    ? Strings.Resources.S_CONFLICT_ITEMS
                    : string.Format(Strings.Resources.S_CONFLICT_ITEMS_PLURAL, conflictingItems.Count),
                Strings.Resources.S_RESTORE_CONF_TITLE,
                primaryText: merge
                    ? Strings.Resources.S_MERGE_OR_REPLACE
                    : Strings.Resources.S_REPLACE,
                secondaryText: conflictingItems.Count == restoreItems.Count ? "" : Strings.Resources.S_SKIP,
                cancelText: Strings.Resources.S_CANCEL,
                icon: DialogService.DialogIcon.Exclamation);

            if (result.Item1 is ContentDialogResult.None)
                return;

            if (result.Item1 is ContentDialogResult.Secondary)
                restoreItems = restoreItems.Where(item => !conflictingItems.Contains(item)).ToList();
        }

        await ShellFileOperation.MoveItems(
            device: device,
            items: restoreItems,
            targetPath: null,
            currentPath: currentPath,
            fileList: lister.FileList,
            dispatcher: App.Current.Dispatcher);

        if (!ReferenceEquals(Data.DirList, lister))
            return;

        var remainingItems = lister.FileList.Except(restoreItems).ToList();
        TrashHelper.EnableRecycleButtons(remainingItems);

        // Clear all remaining files if none of them are indexed
        if (!remainingItems.Any(item => item.TrashIndex is not null))
            _ = Task.Run(() => ShellFileOperation.SilentDelete(device, remainingItems));

        if (!Data.SelectedFiles.Any())
            TrashHelper.EnableRecycleButtons();
    }

    public static void CopyItemPath()
    {
        var path = Data.FileActions.IsAppDrive ? Data.SelectedPackages.First().Name : Data.SelectedFiles.First().FullPath;
        Clipboard.SetText(path);
    }

    public static void CreateNewItem(FileClass file, string newName = null)
    {
        if (!string.IsNullOrEmpty(newName))
            file.UpdatePath($"{Data.CurrentPath}{(Data.CurrentPath == "/" ? "" : "/")}{newName}");

        if (Data.Settings.ShowExtensions)
            file.UpdateType();

        try
        {
            if (file.Type is FileType.Folder)
                _ = ShellFileOperation.MakeDir(Data.CurrentADBDevice, file.FullPath);
            else if (file.Type is FileType.File)
                ShellFileOperation.MakeFile(Data.CurrentADBDevice, file.FullPath);
            else
                throw new NotSupportedException();
        }
        catch (Exception e)
        {
            DialogService.ShowMessage(e.Message, Strings.Resources.S_CREATE_ERR_TITLE, DialogService.DialogIcon.Critical, copyToClipboard: true);
            Data.DirList.FileList.Remove(file);
            throw;
        }

        file.IsTemp = false;
        file.ModifiedTime = DateTime.Now;
        if (file.Type is FileType.File)
            file.Size = 0;

        var index = Data.DirList.FileList.IndexOf(file);
        Data.DirList.FileList.Remove(file);
        Data.DirList.FileList.Insert(index, file);
        Data.FileActions.ItemToSelect = file;
    }

    public static void IsPasteEnabled()
    {
        // Do not update if drag is active
        if (Data.CopyPaste.IsDrag)
            return;

        // Explorer view AND source is clipboard
        if (Data.FileActions.IsPasteStateVisible && Data.CopyPaste.Files.Length > 0)
        {
            Data.FileActions.CutItemsCount.Value = Data.CopyPaste.Files.Length.ToString();
        }
        else
        {
            Data.FileActions.CutItemsCount.Value = "";
            Data.FileActions.IsCopyState.Value = false;
            Data.FileActions.IsCutState.Value = false;

            Data.FileActions.PasteEnabled = false;
            Data.FileActions.IsKeyboardPasteEnabled = false;

            return;
        }

        string stringFormat;
        if (Data.CopyPaste.Files.Length > 1)
        {
            if (Data.FileActions.IsAppDrive)
            {
                stringFormat = Strings.Resources.S_DRAG_INSTALL_MULTIPLE;
            }
            else
            {
                stringFormat = Data.CopyPaste.PasteState is DragDropEffects.Move
                    ? Strings.Resources.S_PASTE_PLURAL_CUT_ITEMS
                    : Strings.Resources.S_PASTE_PLURAL_COPIED_ITEMS;
            }

            Data.FileActions.PasteDescription.Value = string.Format(stringFormat, Data.CopyPaste.Files.Length);
        }
        else
        {
            if (Data.FileActions.IsAppDrive)
            {
                stringFormat = string.Format(Strings.Resources.S_DRAG_INSTALL_SINGLE, Data.CopyPaste.CurrentFiles.FirstOrDefault()?.NoExtName);
            }
            else
            {
                stringFormat = Data.CopyPaste.PasteState is DragDropEffects.Move
                    ? Strings.Resources.S_PASTE_ONE_CUT_ITEM
                    : Strings.Resources.S_PASTE_ONE_COPIED_ITEM;
            }

            Data.FileActions.PasteDescription.Value = stringFormat;
        }

        Data.FileActions.PasteEnabled = EnableUiPaste();
        Data.FileActions.IsKeyboardPasteEnabled = EnableKeyboardPaste();
    }

    public static bool EnableUiPaste()
    {
        string[] files = Data.CopyPaste.Files;
        if (Data.CopyPaste.IsWindows
            && Data.CopyPaste.IsVirtual
            && Data.CopyPaste.Descriptors.Length == files.Length)
        {
            files = [.. Data.CopyPaste.Descriptors.Select(d => d.Name)];
        }

        Data.FileActions.IsPastingInDescendant = files.Length == 1
            && FileHelper.RelationFrom(files[0], Data.CurrentPath) is RelationType.Descendant or RelationType.Self;

        if (Data.FileActions.IsPastingInDescendant)
            return false;

        var selected = Data.SelectedFiles?.Count();

        string targetPath;
        if (selected == 1)
        {
            var targetFile = Data.SelectedFiles.First();
            targetPath = targetFile.IsLink ? targetFile.LinkTarget : targetFile.FullPath;
        }
        else
        {
            targetPath = Data.CurrentPath;
        }

        PastingOnFuse(targetPath, files);

        if (Data.FileActions.IsPastingIllegalOnFuse || Data.FileActions.IsPastingConflictingOnFuse)
            return false;

        switch (selected)
        {
            case 0:
                Data.FileActions.IsPastingInDescendant = Data.CopyPaste.ParentFolder == Data.CurrentPath
                    && Data.CopyPaste.PasteState is DragDropEffects.Move;

                break;
            case 1:
                var item = Data.SelectedFiles.First();
                if (!item.IsDirectory)
                    return false;

                Data.FileActions.IsPastingInDescendant = (files.Length == 1 && files[0] == item.FullPath)
                    || (Data.CopyPaste.ParentFolder == item.FullPath);

                break;
            default:
                return false;
        }

        return !Data.FileActions.IsPastingInDescendant;
    }

    public static bool EnableKeyboardPaste()
    {
        string[] files = Data.CopyPaste.Files;
        if (Data.CopyPaste.IsWindows
            && Data.CopyPaste.IsVirtual
            && Data.CopyPaste.Descriptors.Length == files.Length)
        {
            files = [.. Data.CopyPaste.Descriptors.Select(d => d.Name)];
        }

        Data.FileActions.IsPastingInDescendant = files.Length == 1
            && FileHelper.RelationFrom(files[0], Data.CurrentPath) is RelationType.Descendant or RelationType.Self;

        if (Data.FileActions.IsPastingInDescendant)
            return false;

        var selected = Data.SelectedFiles?.Count() > 1 ? 0 : Data.SelectedFiles?.Count();

        string targetPath;
        if (selected == 1)
        {
            var targetFile = Data.SelectedFiles.First();
            targetPath = targetFile.IsLink ? targetFile.LinkTarget : targetFile.FullPath;
        }
        else
        {
            targetPath = Data.CurrentPath;
        }

        PastingOnFuse(targetPath, files);

        if (Data.FileActions.IsPastingIllegalOnFuse || Data.FileActions.IsPastingConflictingOnFuse)
            return false;

        switch (selected)
        {
            case 0:
                Data.FileActions.IsPastingInDescendant = Data.CopyPaste.ParentFolder == Data.CurrentPath
                    && Data.CopyPaste.PasteState is DragDropEffects.Move;

                break;
            case 1:
                // When duplicating a file multiple times using the keyboard, the selection is the previous copy
                if (Data.CopyPaste.PasteState is DragDropEffects.Copy && Data.DirList.FileList.Any(f => f.FullPath == files[0]))
                    return true;

                var item = Data.SelectedFiles.First();
                if (!item.IsDirectory)
                    return false;

                Data.FileActions.IsPastingInDescendant = (files.Length == 1 && files[0] == item.FullPath)
                    || (Data.CopyPaste.ParentFolder == item.FullPath);

                break;
            default:
                return false;
        }

        return !Data.FileActions.IsPastingInDescendant;
    }

    public static DragDropEffects EnableDropPaste(FileClass target = null)
    {
        if (!Data.CopyPaste.CurrentFiles.Any())
            return DragDropEffects.None;

        var pastingInDescendant = Data.CopyPaste.DragFiles.Length == 1
            && Data.CopyPaste.CurrentFiles.First().Relation(Data.CurrentPath) is RelationType.Descendant or RelationType.Self;

        if (pastingInDescendant || Data.FileActions.IsRecycleBin)
            return DragDropEffects.None;

        if (FileHelper.RelationFrom(Data.CopyPaste.DragParent, AdbExplorerConst.RECYCLE_PATH) is RelationType.Self or RelationType.Ancestor)
            return DragDropEffects.Move;

        string targetPath = target switch
        {
            null => Data.CurrentPath,
            _ when target.IsLink => target.LinkTarget,
            _ => target.FullPath,
        };

        PastingOnFuse(targetPath, [.. Data.CopyPaste.CurrentFiles.Select(f => f.FullPath)]);

        var result = DragDropEffects.Copy;
        if (Data.RuntimeSettings.IsRootActive 
            && Data.CopyPaste.IsSelf
            && DriveHelper.GetCurrentDrive(targetPath)?.IsFUSE is false
            && Data.CopyPaste.CurrentFiles.Count() == 1)
            result |= DragDropEffects.Link;

        if (Data.FileActions.IsPastingIllegalOnFuse || Data.FileActions.IsPastingConflictingOnFuse)
            return DragDropEffects.None;

        if (target is null)
        {
            if (Data.CopyPaste.DragParent == Data.CurrentPath)
                return result;
        }
        else
        {
            if (!target.IsDirectory)
                return DragDropEffects.None;

            pastingInDescendant = (Data.CopyPaste.DragFiles.Length == 1 && Data.CopyPaste.CurrentFiles.First().FullPath == target.FullPath)
                || (Data.CopyPaste.DragParent == target.FullPath);
        }

        return pastingInDescendant
            ? DragDropEffects.None
            : result | DragDropEffects.Move;
    }

    private static void PastingOnFuse(string targetPath, string[] files)
    {
        if (Data.FileActions.IsAppDrive)
        {
            Data.FileActions.IsPastingIllegalOnFuse = Data.CopyPaste.IsSelf && DriveHelper.GetCurrentDrive(files[0])?.IsFUSE is true;
            return;
        }
        bool isFuse = DriveHelper.GetCurrentDrive(targetPath)?.IsFUSE is true;

        Data.FileActions.IsPastingIllegalOnFuse = isFuse
            && !FileHelper.FileNameLegal(files.Select(FileHelper.GetFullName), FileHelper.RenameTarget.FUSE);

        Data.FileActions.IsPastingConflictingOnFuse = isFuse
            && files.Distinct(StringComparer.InvariantCultureIgnoreCase)
            .Count() != files.Length;
    }

    public static void PasteFiles(IEnumerable<FileClass> selectedFiles, bool isLink = false)
    {
        Data.CopyPaste.AcceptDataObject(Clipboard.GetDataObject(), selectedFiles, isLink);

        IsPasteEnabled();
    }

    public static void CutItems(bool isCopy = false)
    {
        if (Data.FileActions.IsAppDrive)
            CopyPackages(Data.SelectedPackages);
        else
            CutFiles(Data.SelectedFiles, isCopy);
    }

    public static void CopyPackages(IEnumerable<Package> items)
    {
        Data.FileActions.CopyEnabled = false;
        Data.FileActions.CutEnabled = true;

        IsPasteEnabled();

        var vfdo = VirtualFileDataObject.PrepareTransfer(items, VirtualFileDataObject.DataObjectMethod.Clipboard);
        vfdo?.SendObjectToShell(VirtualFileDataObject.DataObjectMethod.Clipboard, allowedEffects: DragDropEffects.Copy);
    }

    public static void CutFiles(IEnumerable<FileClass> items, bool isCopy = false)
    {
        var itemsToCut = Data.DevicesObject.Current.Root is not AbstractDevice.RootStatus.Enabled
                    ? items.Where(file => file.Type is FileType.File or FileType.Folder) : items;

        Data.FileActions.CopyEnabled = !isCopy;
        Data.FileActions.CutEnabled = isCopy;

        IsPasteEnabled();

        var dropEffect = isCopy ? DragDropEffects.Copy : DragDropEffects.Move;
        var vfdo = VirtualFileDataObject.PrepareTransfer(itemsToCut, dropEffect, VirtualFileDataObject.DataObjectMethod.Clipboard);
        vfdo?.SendObjectToShell(VirtualFileDataObject.DataObjectMethod.Clipboard, allowedEffects: dropEffect);
    }

    public static void Rename(TextBox textBox)
    {
        if (textBox.DataContext is not FileClass file)
            return;
        
        var name = FileHelper.DisplayName(textBox);

        if (!Data.FileActions.IsRenameUnixLegal
            || (Data.CurrentDrive?.IsFUSE is true && !Data.FileActions.IsRenameFuseLegal)
            || !Data.FileActions.IsRenameUnique)
        {
            return;
        }

        if (file.IsTemp)
        {
            if (string.IsNullOrEmpty(textBox.Text))
            {
                Data.DirList.FileList.Remove(file);
                return;
            }
            
            try
            {
                CreateNewItem(file, textBox.Text);
            }
            catch (Exception e)
            {
                if (e is NotImplementedException)
                    throw;
            }
        }
        else if (!string.IsNullOrEmpty(textBox.Text) && textBox.Text != name)
        {
            try
            {
                string text = textBox.Text;
                if (text.Count(c => c == TextHelper.RTL_MARK) == 1)
                    text = text.Replace($"{TextHelper.RTL_MARK}", "");

                FileHelper.RenameFile(file, text);
            }
            catch (Exception)
            { }
        }
    }

    public static async void DeleteFiles()
    {
        List<FileClass> itemsToDelete;
        if (Data.FileActions.IsRecycleBin && !Data.SelectedFiles.Any())
        {
            itemsToDelete = [.. Data.DirList.FileList.Where(f => f.Extension != AdbExplorerConst.RECYCLE_INDEX_SUFFIX)];
        }
        else
        {
            itemsToDelete = [.. Data.DevicesObject.Current.Root != AbstractDevice.RootStatus.Enabled
                ? Data.SelectedFiles.Where(file => file.Type is FileType.File or FileType.Folder)
                : Data.SelectedFiles];
        }

        string deletedString;
        if (itemsToDelete.Count == 1)
            deletedString = FileHelper.DisplayName(itemsToDelete.First());
        else
        {
            deletedString = $"{itemsToDelete.Count} ";
            if (itemsToDelete.All(item => item.IsDirectory))
                deletedString += Strings.Resources.S_MENU_FOLDERS;
            else if (itemsToDelete.All(item => !item.IsDirectory))
                deletedString += Strings.Resources.S_MENU_FILES;
            else
                deletedString += Strings.Resources.S_BROWSER_ITEMS_PLURAL;
        }

        var result = await DialogService.ShowConfirmation(
            string.Format(Data.FileActions.IsRecycleBin
                ? Strings.Resources.S_DELETE_PERMANENT
                : Strings.Resources.S_DELETE_CONFIRMATION, deletedString),
            Strings.Resources.S_DEL_CONF_TITLE,
            Strings.Resources.S_DELETE_ACTION,
            checkBoxText: Data.Settings.EnableRecycle && !Data.FileActions.IsRecycleBin ? Strings.Resources.S_PERM_DEL : "",
            icon: DialogService.DialogIcon.Delete);

        if (result.Item1 is not ContentDialogResult.Primary)
            return;

        if (!Data.FileActions.IsRecycleBin && Data.Settings.EnableRecycle && !result.Item2)
        {
            await ShellFileOperation.MakeDir(Data.CurrentADBDevice, AdbExplorerConst.RECYCLE_PATH);

            await ShellFileOperation.MoveItems(Data.CurrentADBDevice,
                                         itemsToDelete,
                                         AdbExplorerConst.RECYCLE_PATH,
                                         Data.CurrentPath,
                                         Data.DirList.FileList,
                                         App.Current.Dispatcher);
        }
        else
        {
            ShellFileOperation.DeleteItems(Data.CurrentADBDevice, itemsToDelete, App.Current.Dispatcher);

            if (Data.FileActions.IsRecycleBin)
            {
                var remainingItems = Data.DirList.FileList.Except(itemsToDelete).ToList();
                TrashHelper.EnableRecycleButtons(remainingItems);

                // Clear all remaining files if none of them are indexed
                if (!remainingItems.Any(item => item.TrashIndex is not null))
                {
                    _ = Task.Run(() => ShellFileOperation.SilentDelete(Data.CurrentADBDevice, remainingItems));
                }
            }
        }
    }

    public static async Task RefreshDrives(bool asyncClassify = false, bool updateCounts = true)
    {
        var currentDevice = Data.DevicesObject.Current;
        var currentAdbDevice = Data.CurrentADBDevice;

        if (currentDevice is null || currentAdbDevice is null || currentDevice.Status is not AbstractDevice.DeviceStatus.Ok || currentAdbDevice.Status is not AbstractDevice.DeviceStatus.Ok)
            return;

        if (!asyncClassify && currentDevice.Drives?.Count > 0 && !Data.FileActions.IsExplorerVisible)
            asyncClassify = true;

        string deviceId = currentDevice.ID;
        bool refreshTopology;
        lock (DriveRefreshLock)
        {
            if (ActiveDriveRefreshes.Contains(deviceId))
                return;

            refreshTopology = !LastDriveRefresh.TryGetValue(deviceId, out DateTime lastRefresh)
                || currentDevice.Drives?.Count is not > 0
                || DateTime.Now - lastRefresh >= AdbExplorerConst.DRIVE_UPDATE_INTERVAL;

            if (refreshTopology)
                ActiveDriveRefreshes.Add(deviceId);
        }

        if (refreshTopology)
        {
            try
            {
                var drives = await Task.Run(() => currentAdbDevice.Status is AbstractDevice.DeviceStatus.Ok
                    ? currentAdbDevice.GetDrives()
                    : null);

                if (drives is null
                    || !ReferenceEquals(Data.CurrentADBDevice, currentAdbDevice)
                    || !ReferenceEquals(Data.DevicesObject.Current, currentDevice)
                    || App.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
                {
                    return;
                }

                Task<bool> updateTask = await dispatcher.InvokeAsync(
                    () => currentDevice.UpdateDrives(drives, dispatcher, asyncClassify),
                    DispatcherPriority.Background);
                await updateTask;

                if (!ReferenceEquals(Data.CurrentADBDevice, currentAdbDevice)
                    || !ReferenceEquals(Data.DevicesObject.Current, currentDevice))
                {
                    return;
                }

                await dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(Data.CurrentADBDevice, currentAdbDevice)
                        || !ReferenceEquals(Data.DevicesObject.Current, currentDevice))
                    {
                        return;
                    }

                    Data.RuntimeSettings.FilterDrives = true;
                    FolderHelper.CombineDisplayNames();
                }, DispatcherPriority.Background);

                lock (DriveRefreshLock)
                {
                    LastDriveRefresh[deviceId] = DateTime.Now;
                }
            }
            catch (Exception e)
            {
                Data.AddCommandLog($"@ADB Explorer: failed to refresh drives: {e.Message}");
            }
            finally
            {
                lock (DriveRefreshLock)
                {
                    ActiveDriveRefreshes.Remove(deviceId);
                }
            }
        }

        if (!updateCounts
            || !ReferenceEquals(Data.CurrentADBDevice, currentAdbDevice)
            || !ReferenceEquals(Data.DevicesObject.Current, currentDevice))
        {
            return;
        }

        bool isRecovery = currentDevice.Type is AbstractDevice.DeviceType.Recovery;
        bool hasTrashDrive = currentDevice.Drives?.Any(d => d.Type is AbstractDrive.DriveType.Trash) == true;
        bool hasTempDrive = currentDevice.Drives?.Any(d => d.Type is AbstractDrive.DriveType.Temp) == true;
        bool hasPackageDrive = currentDevice.Drives?.Any(d => d.Type is AbstractDrive.DriveType.Package) == true;

        if (isRecovery)
        {
            if (App.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
                return;

            await dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(Data.DevicesObject.Current, currentDevice))
                    return;

                foreach (var item in currentDevice.Drives?.OfType<VirtualDriveViewModel>() ?? [])
                {
                    item.SetItemsCount(item.Type is AbstractDrive.DriveType.Package ? -1 : null);
                }
            }, DispatcherPriority.Background);
            return;
        }

        if (Data.Settings.EnableRecycle && hasTrashDrive)
            await TrashHelper.UpdateRecycledItemsCount();

        if (!ReferenceEquals(Data.CurrentADBDevice, currentAdbDevice)
            || !ReferenceEquals(Data.DevicesObject.Current, currentDevice))
        {
            return;
        }

        if (Data.Settings.EnableApk && hasTempDrive)
            await UpdateInstallersCount();

        if (!ReferenceEquals(Data.CurrentADBDevice, currentAdbDevice)
            || !ReferenceEquals(Data.DevicesObject.Current, currentDevice))
        {
            return;
        }

        if (Data.Settings.EnableApk && hasPackageDrive)
            await UpdatePackagesCount();
    }

    public static async Task UpdateInstallersCount()
    {
        var currentDevice = Data.DevicesObject.Current;
        if (currentDevice is null)
            return;

        string deviceId = currentDevice.ID;
        ulong count;
        try
        {
            count = await Task.Run(() => ADBService.CountPackages(deviceId));
        }
        catch
        {
            return;
        }

        if (App.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
            return;

        await dispatcher.InvokeAsync(() =>
        {
            if (Data.DevicesObject.Current?.ID != deviceId)
                return;

            var temp = Data.DevicesObject.Current.Drives.Find(d => d.Type is AbstractDrive.DriveType.Temp);
            ((VirtualDriveViewModel)temp)?.SetItemsCount(
                count <= (ulong)long.MaxValue ? (long)count : null);
        }, DispatcherPriority.Background);
    }

    public static async Task UpdatePackagesCount()
    {
        var currentDevice = Data.DevicesObject.Current;
        var currentAdbDevice = Data.CurrentADBDevice;
        if (currentDevice is null || currentAdbDevice is null)
            return;

        string deviceId = currentDevice.ID;
        ulong? count;
        try
        {
            count = await Task.Run(() => ShellFileOperation.GetPackagesCount(currentAdbDevice));
        }
        catch
        {
            return;
        }

        if (count is null
            || Data.DevicesObject.Current?.ID != deviceId
            || App.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
        {
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            if (Data.DevicesObject.Current?.ID != deviceId)
                return;

            var package = Data.DevicesObject.Current.Drives.Find(d => d.Type is AbstractDrive.DriveType.Package);
            ((VirtualDriveViewModel)package)?.SetItemsCount(
                count.Value <= (ulong)long.MaxValue ? (long)count.Value : null);
        }, DispatcherPriority.Background);
    }

    public static void UpdatePackages(bool updateExplorer = false)
    {
        int requestVersion = Interlocked.Increment(ref packageRefreshVersion);
        var currentDevice = Data.DevicesObject.Current;
        var currentAdbDevice = Data.CurrentADBDevice;
        if (currentDevice is null || currentAdbDevice is null)
        {
            if (updateExplorer)
                Data.FileActions.ListingInProgress = false;
            return;
        }

        if (updateExplorer)
            Data.FileActions.ListingInProgress = true;

        string deviceId = currentDevice.ID;
        var version = currentDevice.AndroidVersion;
        bool showSystemPackages = Data.Settings.ShowSystemPackages;
        var packageTask = Task.Run(() => ShellFileOperation.GetPackages(
            currentAdbDevice,
            showSystemPackages,
            version is not null && version >= AdbExplorerConst.MIN_PKG_UID_ANDROID_VER));

        packageTask.ContinueWith((t) =>
        {
            if (App.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
                return;

            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (requestVersion != Volatile.Read(ref packageRefreshVersion)
                    || Data.DevicesObject.Current?.ID != deviceId)
                {
                    return;
                }

                try
                {
                    if (!t.IsCompletedSuccessfully)
                    {
                        if (t.Exception is not null)
                            Data.AddCommandLog($"@ADB Explorer: failed to list packages: {t.Exception.GetBaseException().Message}");
                        return;
                    }

                    Data.Packages = t.Result;
                    if (updateExplorer)
                        Data.RuntimeSettings.ExplorerSource = Data.Packages;

                    if (!updateExplorer && Data.DevicesObject.Current is not null)
                    {
                        var package = Data.DevicesObject.Current.Drives.Find(d => d.Type is AbstractDrive.DriveType.Package);
                        ((VirtualDriveViewModel)package)?.SetItemsCount(Data.Packages.Count);
                    }
                }
                finally
                {
                    if (updateExplorer)
                        Data.FileActions.ListingInProgress = false;
                }
            }));
        });
    }

    public static void ClearExplorer(bool clearDevice = true)
    {
        Interlocked.Increment(ref packageRefreshVersion);
        Data.FileActions.ListingInProgress = false;
        Data.DirList?.FileList?.Clear();
        Data.Packages.Clear();
        Data.SelectedFiles = [];
        Data.SelectedPackages = [];

        Data.FileActions.PushFilesFoldersEnabled =
        Data.FileActions.PullEnabled =
        Data.FileActions.DeleteEnabled =
        Data.FileActions.RenameEnabled =
        Data.FileActions.HomeEnabled =
        Data.FileActions.NewEnabled =
        Data.FileActions.PasteEnabled =
        Data.FileActions.IsUninstallVisible.Value =
        Data.FileActions.CutEnabled =
        Data.FileActions.CopyEnabled =
        Data.FileActions.IsExplorerVisible =
        Data.FileActions.PackageActionsEnabled =
        Data.FileActions.IsCopyItemPathEnabled =
        Data.FileActions.UpdateModifiedEnabled =
        Data.FileActions.IsFollowLinkEnabled =
        Data.RuntimeSettings.IsExplorerLoaded =
        Data.FileActions.ParentEnabled = false;

        Data.FileActions.ExplorerFilter = "";

        if (clearDevice)
        {
            Data.CurrentDisplayNames.Clear();
            Data.CurrentPath = null;
            Data.RuntimeSettings.CurrentDevice = null;
            Data.RuntimeSettings.ClearNavBox = true;

            UpdateFileActions();
        }

        Data.RuntimeSettings.FilterActions = true;
    }

    public static void UpdateFileActions()
    {
        Data.FileActions.IsApkActionsVisible.Value = Data.Settings.EnableApk && Data.DevicesObject?.Current;
        Data.FileActions.PushPackageEnabled = Data.FileActions.IsApkActionsVisible && Data.DevicesObject.Current.Type is not AbstractDevice.DeviceType.Recovery;

        Data.FileActions.UninstallPackageEnabled = Data.FileActions.IsAppDrive && Data.SelectedPackages.Any();
        Data.FileActions.ContextPushPackagesEnabled = Data.FileActions.IsAppDrive && !Data.SelectedPackages.Any();

        Data.FileActions.IsRefreshEnabled = Data.FileActions.IsDriveViewVisible || Data.FileActions.IsExplorerVisible;
        Data.FileActions.IsCopyCurrentPathEnabled = Data.FileActions.IsExplorerVisible && !Data.FileActions.IsRecycleBin && !Data.FileActions.IsAppDrive;

        Data.FileActions.IsOpenApkLocationEnabled = Data.FileActions.IsAppDrive && Data.SelectedPackages.Count() == 1;
        Data.FileActions.IsApkWebSearchEnabled = Data.FileActions.IsOpenApkLocationEnabled && !string.IsNullOrEmpty(Data.RuntimeSettings.DefaultBrowserPath);

        Data.FileActions.IsRegularItem = !Data.SelectedFiles.Any() || Data.RuntimeSettings.IsRootActive
            || Data.SelectedFiles.AnyAll(item => item.Type is FileType.File or FileType.Folder);

        Data.FileActions.IsFollowLinkEnabled = !Data.FileActions.IsRecycleBin
                                               && Data.SelectedFiles.Count() == 1
                                               && Data.SelectedFiles.First().IsLink
                                               && Data.SelectedFiles.First().Type is not FileType.BrokenLink;

        if (Data.FileActions.IsRecycleBin)
        {
            TrashHelper.EnableRecycleButtons(Data.SelectedFiles.Any() ? Data.SelectedFiles : Data.DirList.FileList);
        }
        else
        {
            Data.FileActions.DeleteEnabled = Data.SelectedFiles.Any() && Data.FileActions.IsRegularItem
                && (!Data.FileActions.IsFollowLinkEnabled || Data.RuntimeSettings.IsRootActive);

            Data.FileActions.RestoreEnabled = false;
        }

        Data.FileActions.PullDescription.Value = Data.FileActions.IsFollowLinkEnabled ? Strings.Resources.S_PULL_ACTION_LINK : Strings.Resources.S_PULL_ACTION;
        Data.FileActions.DeleteDescription.Value = Data.FileActions.IsRecycleBin && !Data.SelectedFiles.Any() ? Strings.Resources.S_EMPTY_TRASH : Strings.Resources.S_DELETE_ACTION;
        Data.FileActions.RestoreDescription.Value = Data.FileActions.IsRecycleBin && !Data.SelectedFiles.Any() ? Strings.Resources.S_RESTORE_ALL : Strings.Resources.S_RESTORE_ACTION;

        Data.FileActions.IsSelectionIllegalOnWindows = Data.SelectedFiles.Any() && !FileHelper.FileNameLegal(Data.SelectedFiles, FileHelper.RenameTarget.Windows);
        Data.FileActions.IsSelectionIllegalOnFuse = Data.SelectedFiles.Any() && !FileHelper.FileNameLegal(Data.SelectedFiles, FileHelper.RenameTarget.FUSE);
        Data.FileActions.IsSelectionIllegalOnWinRoot = Data.SelectedFiles.Any() && !FileHelper.FileNameLegal(Data.SelectedFiles, FileHelper.RenameTarget.WinRoot);
        Data.FileActions.IsSelectionConflictingOnFuse = Data.SelectedFiles.Select(f => f.FullName)
            .Distinct(StringComparer.InvariantCultureIgnoreCase)
            .Count() != Data.SelectedFiles.Count();

        Data.FileActions.PullEnabled = !Data.FileActions.IsRecycleBin
                                       && Data.SelectedFiles.AnyAll(f => f.Type is not FileType.BrokenLink)
                                       && Data.FileActions.IsRegularItem
                                       && !Data.FileActions.IsSelectionIllegalOnWindows
                                       && !Data.FileActions.IsSelectionConflictingOnFuse;

        Data.FileActions.ContextPushEnabled = !Data.FileActions.IsRecycleBin && !Data.FileActions.IsAppDrive && (!Data.SelectedFiles.Any() || (Data.SelectedFiles.Count() == 1 && Data.SelectedFiles.First().IsDirectory));

        Data.FileActions.RenameEnabled = !Data.FileActions.IsRecycleBin
                                         && Data.SelectedFiles.Count() == 1
                                         && Data.FileActions.IsRegularItem
                                         && (!Data.FileActions.IsFollowLinkEnabled || Data.RuntimeSettings.IsRootActive);

        var allSelectedAreCut = false;
        if (Data.CopyPaste.IsSelf && Data.CopyPaste.Files.Length == Data.SelectedFiles.Count())
        {
            var selectedPaths = Data.SelectedFiles.Select(file => file.FullPath).ToHashSet();
            allSelectedAreCut = Data.CopyPaste.Files.All(selectedPaths.Contains);
        }
        
        Data.FileActions.CutEnabled = Data.SelectedFiles.AnyAll(f => f.Type is not FileType.BrokenLink)
                                      && !(allSelectedAreCut && Data.CopyPaste.PasteState is DragDropEffects.Move)
                                      && Data.FileActions.IsRegularItem
                                      && (!Data.FileActions.IsFollowLinkEnabled || Data.RuntimeSettings.IsRootActive);

        if (Data.FileActions.IsAppDrive)
        {
            Data.FileActions.CopyEnabled = Data.SelectedPackages.Any();
        }
        else
        {
            Data.FileActions.CopyEnabled = Data.SelectedFiles.AnyAll(f => f.Type is not FileType.BrokenLink)
                                           && !(allSelectedAreCut && Data.CopyPaste.PasteState is DragDropEffects.Copy)
                                           && Data.FileActions.IsRegularItem
                                           && !Data.FileActions.IsRecycleBin;
        }
        
        IsPasteEnabled();

        // APK enabled in settings
        // All selected files are installable
        // Not in trash
        // If recovery, only enabled outside temp drive (to enable copy to temp, but install is disabled, even in temp drive)
        Data.FileActions.PackageActionsEnabled = Data.Settings.EnableApk
                                                 && Data.SelectedFiles.AnyAll(file => file.IsInstallApk)
                                                 && !Data.FileActions.IsRecycleBin
                                                 && !(Data.DevicesObject?.Current?.Type is AbstractDevice.DeviceType.Recovery
                                                 && Data.FileActions.IsTemp);

        Data.FileActions.IsCopyItemPathEnabled = Data.FileActions.IsAppDrive
            ? Data.SelectedPackages.Count() == 1
            : Data.SelectedFiles.Count() == 1 && !Data.FileActions.IsRecycleBin;

        Data.FileActions.ContextNewEnabled = !Data.SelectedFiles.Any() && !Data.FileActions.IsRecycleBin && !Data.FileActions.IsAppDrive;

        Data.FileActions.SubmenuUninstallEnabled = Data.CurrentDrive?.IsFUSE is not true
            && Data.SelectedFiles.AnyAll(file => file.IsInstallApk)
            && Data.DevicesObject?.Current?.Type is not AbstractDevice.DeviceType.Recovery;

        Data.FileActions.UpdateModifiedEnabled = !Data.FileActions.IsRecycleBin
            && Data.SelectedFiles.AnyAll(file => file.Type is FileType.File && !file.IsApk && !file.IsLink);

        Data.FileActions.EditFileEnabled = !Data.FileActions.IsRecycleBin
            && Data.SelectedFiles.Count() == 1
            && Data.SelectedFiles.First().Type is FileType.File
            && !Data.SelectedFiles.First().IsApk
            && !Data.SelectedFiles.First().IsLink
            && Data.SelectedFiles.First().Size < Data.Settings.EditorMaxFileSize;

        Data.FileActions.IsPasteLinkEnabled = Data.CurrentDrive?.IsFUSE is not true
            && Data.RuntimeSettings.IsRootActive
            && Data.CopyPaste.Files.Length == 1
            && Data.CopyPaste.IsSelf
            && Data.CopyPaste.PasteState is DragDropEffects.Copy
            && (!Data.SelectedFiles.Any() ||
            (Data.SelectedFiles.Count() == 1 && Data.SelectedFiles.First().IsDirectory));

        Data.FileActions.InstallPackageEnabled = Data.DevicesObject?.Current?.Type is not AbstractDevice.DeviceType.Recovery
            && Data.CurrentDrive?.IsFUSE is not true;

        if (!Data.CopyPaste.IsDrag)
            Data.RuntimeSettings.FilterActions = true;
    }

    public static void ScheduleUpdateFileActions()
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        if (Interlocked.Exchange(ref fileActionsRefreshScheduled, 1) == 1)
            return;

        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                UpdateFileActions();
            }
            finally
            {
                Interlocked.Exchange(ref fileActionsRefreshScheduled, 0);
            }
        }), DispatcherPriority.Background);
    }

    public static void PushItems(bool isFolderPicker, bool isContextMenu)
    {
        Data.RuntimeSettings.IsPathBoxFocused = false;

        string targetPath, targetName = "";
        string title = "";
        if (isContextMenu && Data.SelectedFiles.Count() == 1)
        {
            targetPath = Data.SelectedFiles.First().FullPath;
            targetName = Data.SelectedFiles.First().FullName;

            title = isFolderPicker
                ? Strings.Resources.S_SELECT_FOLDER_PUSH_DESTINATION
                : Strings.Resources.S_SELECT_FILE_PUSH_DESTINATION;
        }
        else
        {
            targetPath = Data.CurrentPath;

            title = isFolderPicker
                ? Strings.Resources.S_SELECT_FOLDER_PUSH
                : Strings.Resources.S_SELECT_FILE_PUSH;
        }
        
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = isFolderPicker,
            Multiselect = true,
            DefaultDirectory = Data.Settings.DefaultFolder,
            Title = title,
        };

        if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            return;

        _ = CopyPasteService.VerifyAndPush(targetPath, dialog.FileNames, Data.CurrentADBDevice);
    }

    public static async Task<FileSyncOperation> PushShellObject(string itemPath, string targetPath, ADBService.AdbDevice device, DragDropEffects dropEffects = DragDropEffects.Copy, ShellItem originalShellItem = null)
    {
        FileSyncOperation pushOperation = null;
        SyncFile source = null;

        try
        {
            source = await Task.Run(() => SyncFile.FromWindowsPath(itemPath));

            var target = new SyncFile(FileHelper.ConcatPaths(targetPath, source.FullName),
                source.IsDirectory ? FileType.Folder : FileType.File)
                { Size = source.Size };

            void addPushOperation()
            {
                pushOperation = FileSyncOperation.PushFile(source, target, device, App.Current.Dispatcher);
                pushOperation.DropEffects = dropEffects;
                pushOperation.OriginalShellItem = originalShellItem;
                pushOperation.PropertyChanged += PushOperation_PropertyChanged;
                Data.FileOpQ.AddOperation(pushOperation);
            }

            if (App.Current.Dispatcher.CheckAccess())
                addPushOperation();
            else
                await App.Current.Dispatcher.InvokeAsync(addPushOperation);
        }
        catch
        {
            source?.ClearAll();
            throw;
        }

        return pushOperation;
    }

    public static async Task<IReadOnlyList<FileSyncOperation>> PushShellObjects(
        IEnumerable<string> itemPaths,
        string targetPath,
        ADBService.AdbDevice device,
        DragDropEffects dropEffects = DragDropEffects.Copy)
    {
        var paths = itemPaths.ToList();
        List<(string Path, string Message)> failures = [];
        var syncItems = await Task.Run(() =>
        {
            List<SyncFile> sources = [];
            foreach (var path in paths)
            {
                SyncFile source = null;

                try
                {
                    source = SyncFile.FromWindowsPath(path);

                    sources.Add(source);
                    source = null;
                }
                catch (Exception e)
                {
                    source?.ClearAll();
                    failures.Add((path, e.Message));
                }
            }

            List<(SyncFile source, SyncFile target, bool isBatch)> items = [];
            foreach (var group in sources.GroupBy(source => source.ParentPath, StringComparer.OrdinalIgnoreCase))
            {
                var groupItems = group.ToList();
                if (groupItems.Count == 1)
                {
                    var source = groupItems[0];
                    var target = new SyncFile(FileHelper.ConcatPaths(targetPath, source.FullName),
                        source.IsDirectory ? FileType.Folder : FileType.File)
                        { Size = source.Size };
                    items.Add((source, target, false));
                    continue;
                }

                SyncFile batchSource = new(group.Key, FileType.Folder)
                {
                    PathType = FilePathType.Windows,
                };
                batchSource.Children.AddRange(groupItems);
                items.Add((batchSource, new SyncFile(targetPath, FileType.Folder), true));
            }

            return items;
        });

        if (failures.Count > 0)
        {
            Data.AddCommandLog($"@Windows: failed to prepare {failures.Count} item(s) for upload. "
                + $"{failures[0].Path}: {failures[0].Message}");
        }

        if (syncItems.Count == 0)
            return [];

        List<FileSyncOperation> queuedOperations = [];
        void addPushOperations()
        {
            queuedOperations = syncItems.Select(item =>
            {
                var pushOperation = FileSyncOperation.PushFile(item.source, item.target, device, App.Current.Dispatcher);
                pushOperation.DropEffects = dropEffects;
                pushOperation.IsBatch = item.isBatch;
                pushOperation.PropertyChanged += PushOperation_PropertyChanged;
                return pushOperation;
            }).ToList();

            Data.FileOpQ.AddOperations(queuedOperations);
        }

        try
        {
            if (App.Current.Dispatcher.CheckAccess())
                addPushOperations();
            else
                await App.Current.Dispatcher.InvokeAsync(addPushOperations);
        }
        catch (Exception e)
        {
            syncItems.ForEach(item => item.source.ClearAll());
            Data.AddCommandLog($"@ADB Explorer: failed to queue upload: {e.Message}");
            return [];
        }

        return queuedOperations;
    }

    private static void PushOperation_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        var op = sender as FileSyncOperation;

        if (e.PropertyName != nameof(FileOperation.Status)
            || op.Status is FileOperation.OperationStatus.Waiting
            or FileOperation.OperationStatus.InProgress)
            return;

        try
        {
            // If operation was cancelled or had failed - don't delete the source.
            if (op.Status is not FileOperation.OperationStatus.Completed)
                return;

            var hasSkippedFiles = op.StatusInfo is CompletedSyncProgressViewModel { FilesSkipped: > 0 };
            if (hasSkippedFiles)
            {
                if (op.Device.ID == Data.CurrentADBDevice?.ID
                    && (op.IsBatch ? op.TargetPath.FullPath : op.TargetPath.ParentPath) == Data.CurrentPath)
                {
                    Data.RuntimeSettings.Refresh = true;
                }
            }
            else
            {
                SchedulePushedFileUpdate(op);
            }

            // In push we can delete the source once the operation has completed
            if (op.DropEffects is DragDropEffects.Move && !hasSkippedFiles)
            {
                List<(string FullPath, bool IsDirectory)> sources = op.IsBatch
                    ? op.FilePath.Children.Select(file => (file.FullPath, file.IsDirectory)).ToList()
                    : [(op.FilePath.FullPath, op.FilePath.IsDirectory)];

                _ = Task.Run(() =>
                {
                    op.WaitForCompletion();

                    foreach (var (sourcePath, isDirectory) in sources)
                    {
                        try
                        {
                            if (isDirectory)
                                Directory.Delete(sourcePath, true);
                            else
                                File.Delete(sourcePath);
                        }
                        catch
                        { }
                    }
                });
            }
        }
        finally
        {
            op.PropertyChanged -= PushOperation_PropertyChanged;
        }
    }

    private static void SchedulePushedFileUpdate(FileSyncOperation op)
    {
        IEnumerable<(
            (string DeviceId, string ParentPath, string FullName) Key,
            (string FullPath, FileType Type, long? Size, DateTime? Modified) Value)> updates = op.IsBatch
            ? op.FilePath.Children.Select(file => (
                Key: (op.Device.ID, op.TargetPath.FullPath, file.FullName),
                Value: (
                    FileHelper.ConcatPaths(op.TargetPath.FullPath, file.FullName),
                    file.IsDirectory ? FileType.Folder : FileType.File,
                    file.Size,
                    file.DateModified)))
            : [(
                Key: (op.Device.ID, op.TargetPath.ParentPath, op.TargetPath.FullName),
                Value: (
                    op.TargetPath.FullPath,
                    op.TargetPath.IsDirectory ? FileType.Folder : FileType.File,
                    op.TargetPath.Size,
                    op.FilePath.DateModified))];

        lock (PushedFilesLock)
        {
            foreach (var update in updates)
                PendingPushedFiles[update.Key] = update.Value;

            if (PushedFilesUpdateScheduled)
                return;

            PushedFilesUpdateScheduled = true;
        }

        var dispatcher = op.Dispatcher;
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);

            KeyValuePair<
                (string DeviceId, string ParentPath, string FullName),
                (string FullPath, FileType Type, long? Size, DateTime? Modified)>[] updates;

            lock (PushedFilesLock)
            {
                updates = [.. PendingPushedFiles];
                PendingPushedFiles.Clear();
                PushedFilesUpdateScheduled = false;
            }

            var preparedFiles = updates.Select(update => (
                update.Key,
                File: new FileClass(
                    update.Key.FullName,
                    update.Value.FullPath,
                    update.Value.Type,
                    size: update.Value.Size,
                    modifiedTime: update.Value.Modified,
                    loadIcon: false))).ToArray();

            if (dispatcher.HasShutdownStarted)
                return;

            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (Data.CurrentADBDevice is null || Data.DirList is null)
                    return;

                HashSet<string> existingNames = [.. Data.DirList.FileList.Select(file => file.FullName)];
                var filesToAdd = preparedFiles
                    .Where(update => update.Key.DeviceId == Data.CurrentADBDevice.ID
                        && update.Key.ParentPath == Data.CurrentPath
                        && existingNames.Add(update.Key.FullName))
                    .Select(update => update.File)
                    .ToList();

                Data.DirList.FileList.AddRange(filesToAdd);
            }), DispatcherPriority.Background);
        });
    }

    // Pull where we know the actual target path
    public static void PullFiles(string targetPath = "")
    {
        Data.RuntimeSettings.IsPathBoxFocused = false;

        var pullItems = Data.SelectedFiles;
        ShellItem path;

        if (!string.IsNullOrEmpty(targetPath))
        {
            path = ShellItem.Open(targetPath);
        }
        else
        {
            var dialog = new CommonOpenFileDialog()
            {
                IsFolderPicker = true,
                Multiselect = false,
                DefaultDirectory = Data.Settings.DefaultFolder,
                Title = pullItems.Count() > 1
                    ? Strings.Resources.S_ITEM_DESTINATION_PLURAL
                    : string.Format(Strings.Resources.S_ITEM_DESTINATION, pullItems.First()),
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                return;
            
            path = ShellItem.Open(dialog.FileName);
        }

        _ = PullFiles(path, pullItems, true);
    }

    private static async Task PullFiles(ShellItem path, IEnumerable<FileClass> pullItems, bool notify = false)
    {
        try
        {
            var pullItemList = pullItems?.ToList();
            if (pullItemList is null || pullItemList.Count == 0)
                return;

            var device = Data.CurrentADBDevice;
            if (device is null)
                return;

            string targetPath = path.ParsingName;
            var dispatcher = App.Current.Dispatcher;
            var match = AdbRegEx.RE_WINDOWS_DRIVE_ROOT().Match(targetPath);
            var invalidFiles = pullItemList
                .Where(f => AdbExplorerConst.INVALID_WINDOWS_ROOT_PATHS.Contains(f.FullName))
                .ToList();

            if (match.Success && invalidFiles.Count > 0)
            {
                var result = await DialogService.ShowConfirmation(string.Format(Strings.Resources.S_WIN_ROOT_ILLEGAL, invalidFiles.Count),
                                                     Strings.Resources.S_WIN_ROOT_ILLEGAL_TITLE,
                                                     primaryText: Strings.Resources.S_SKIP,
                                                     icon: DialogService.DialogIcon.Exclamation);

                if (result.Item1 is not ContentDialogResult.Primary)
                    return;

                pullItemList = pullItemList.Except(invalidFiles).ToList();
            }

            string directoryError = await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(targetPath);
                    return null;
                }
                catch (Exception e)
                {
                    return e.Message;
                }
            });

            if (directoryError is not null)
            {
                DialogService.ShowMessage(directoryError, Strings.Resources.S_DEST_ERR,
                    DialogService.DialogIcon.Critical, copyToClipboard: true);
                return;
            }

            var files = await CopyPasteService.MergeFiles(
                pullItemList.Select(f => f.FullPath), targetPath, device);
            var fileSet = files.ToHashSet();
            if (fileSet.Count < pullItemList.Count)
                pullItemList = pullItemList.Where(f => fileSet.Contains(f.FullPath)).ToList();

            var operations = await Task.Run(() => GeneratePullOps(
                targetPath, pullItemList, device, dispatcher, notify));
            await dispatcher.InvokeAsync(() => Data.FileOpQ.AddOperations(operations));

            static List<FileSyncOperation> GeneratePullOps(
                string targetPath,
                IEnumerable<FileClass> pullItems,
                ADBService.AdbDevice device,
                Dispatcher dispatcher,
                bool notify)
            {
                List<FileSyncOperation> operations = [];
                var itemList = pullItems.ToList();
                var treesBySource = FileHelper.GetFolderTrees(
                    itemList.Where(file => file.IsDirectory).Select(file => file.FullPath),
                    device);

                foreach (var group in itemList.GroupBy(file => file.ParentPath, StringComparer.Ordinal))
                {
                    var sources = group.Select(file => new SyncFile(
                        file,
                        file.IsDirectory && treesBySource.TryGetValue(file.FullPath, out var tree)
                            ? tree.Skip(1)
                            : null)).ToList();
                    FileSyncOperation fileOp;
                    if (sources.Count == 1)
                    {
                        var source = sources[0];
                        fileOp = FileSyncOperation.PullFile(
                            source,
                            SyncFile.MergeToWindowsPath(source, targetPath),
                            device,
                            dispatcher);
                    }
                    else
                    {
                        SyncFile batchSource = new(group.Key, FileType.Folder);
                        batchSource.Children.AddRange(sources);
                        SyncFile batchTarget = new(targetPath, FileType.Folder)
                        {
                            PathType = FilePathType.Windows,
                        };
                        fileOp = FileSyncOperation.PullFile(batchSource, batchTarget, device, dispatcher);
                        fileOp.IsBatch = true;
                    }

                    if (notify)
                        fileOp.PropertyChanged += PullOperation_PropertyChanged;

                    operations.Add(fileOp);
                }

                return operations;
            }
        }
        catch (Exception e)
        {
            Data.AddCommandLog($"@ADB Explorer: failed to prepare download: {e.Message}");
        }
        finally
        {
            try
            {
                path?.Dispose();
            }
            catch
            { }
        }
    }

    private static readonly object explorerRefreshLock = new();
    private static readonly HashSet<string> pendingExplorerRefreshes = [];
    private static bool explorerRefreshScheduled;

    private static void PullOperation_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FileOperation.Status))
            return;

        var op = sender as FileSyncOperation;
        if (op.Status is FileOperation.OperationStatus.Waiting or FileOperation.OperationStatus.InProgress)
            return;

        op.PropertyChanged -= PullOperation_PropertyChanged;
        if (op.Status is not FileOperation.OperationStatus.Completed)
            return;

        lock (explorerRefreshLock)
        {
            pendingExplorerRefreshes.Add(op.IsBatch
                ? op.TargetPath.FullPath
                : op.TargetPath.ParentPath);
            if (explorerRefreshScheduled)
                return;

            explorerRefreshScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);

            string[] directories;
            lock (explorerRefreshLock)
            {
                directories = [.. pendingExplorerRefreshes];
                pendingExplorerRefreshes.Clear();
                explorerRefreshScheduled = false;
            }

            directories.ForEach(NativeMethods.RefreshExplorerDirectory);
        });
    }

    public static void ToggleFileOpQ()
    {
        Data.FileOpQ.IsAutoPlayOn ^= true;

        if (Data.FileOpQ.IsAutoPlayOn)
            Data.FileOpQ.Start();
        else
            Data.FileOpQ.Stop();

        Data.RuntimeSettings.RefreshFileOpControls = true;
    }

    private static int fileOpControlsRefreshScheduled;

    public static void UpdateFileOpControls()
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        if (Interlocked.Exchange(ref fileOpControlsRefreshScheduled, 1) == 1)
            return;

        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var changed = false;
                var plural = Data.FileActions.SelectedFileOps.Value.Count() != 1;
                var opString = plural
                    ? Strings.Resources.S_ACTION_OPERATION_PLURAL
                    : Strings.Resources.S_ACTION_OPERATION;

                var removeAction = string.Format(Strings.Resources.S_REM_DEVICE_TITLE, opString);
                if (Data.FileActions.RemoveFileOpDescription.Value != removeAction)
                {
                    Data.FileActions.RemoveFileOpDescription.Value = removeAction;
                    changed = true;
                }

                var validateAction = string.Format(Strings.Resources.S_ACTION_VALIDATE, opString);
                if (Data.FileActions.ValidateDescription.Value != validateAction)
                {
                    Data.FileActions.ValidateDescription.Value = validateAction;
                    changed = true;
                }

                if (changed)
                    Data.RuntimeSettings.RefreshFileOpControls = true;
            }
            finally
            {
                Interlocked.Exchange(ref fileOpControlsRefreshScheduled, 0);
            }
        }), DispatcherPriority.Background);
    }

    public static async void ResetAppSettings()
    {
        var result = await DialogService.ShowConfirmation(
                        Strings.Resources.S_RESET_SETTINGS,
                        Strings.Resources.S_RESET_SETTINGS_TITLE,
                        primaryText: Strings.Resources.S_CONFIRM,
                        cancelText: Strings.Resources.S_CANCEL,
                        icon: DialogService.DialogIcon.Exclamation);

        if (result.Item1 == ContentDialogResult.None)
            return;

        Data.RuntimeSettings.ResetAppSettings = true;
    }

    public static void ToggleSettingsSort()
    {
        Data.RuntimeSettings.SortedView ^= true;
        Data.FileActions.IsExpandSettingsVisible.Value ^= true;

        Data.RuntimeSettings.RefreshSettingsControls = true;
    }

    public static void ToggleSettingsExpand()
    {
        Data.RuntimeSettings.GroupsExpanded ^= true;

        Data.RuntimeSettings.RefreshSettingsControls = true;
    }

    public static async void FollowLink()
    {
        var target = Data.SelectedFiles.First().LinkTarget;

        if (string.IsNullOrEmpty(target))
            return;

        if (FileHelper.GetParentPath(target) != Data.CurrentPath)
        {
            Data.RuntimeSettings.LocationToNavigate = new(target + "/..");
        }

        await AsyncHelper.WaitUntil(() => !Data.DirList.InProgress, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(20), new());

        var file = Data.DirList.FileList.FirstOrDefault(f => f.FullPath == target);
        if (file is not null)
            Data.FileActions.ItemToSelect = file;
    }

    public static void OpenApkLocation(Package apk = null)
    {
        apk ??= Data.SelectedPackages.First();

        Data.RuntimeSettings.LocationToNavigate = new(FileHelper.GetParentPath(apk.Path));
    }

    public static void ApkWebSearch()
    {
        var apk = Data.SelectedPackages.First();
        
        Process.Start(Data.RuntimeSettings.DefaultBrowserPath, $"\"? {apk.Name}\"");
    }

    public static void RemoveFileOps()
    {
        var ops = Data.FileActions.SelectedFileOps.Value;
        if (!ops.Any())
            ops = Data.FileOpQ.Operations;

        Data.FileOpQ.Operations.RemoveAll(ops);
    }
}
