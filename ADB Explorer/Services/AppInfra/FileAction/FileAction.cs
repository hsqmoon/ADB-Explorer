using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;
using static ADB_Explorer.Services.FileAction;

namespace ADB_Explorer.Services;

public static class AppActions
{
    public static void RaiseCanExecuteChanged(params FileActionType[] actions)
    {
        HashSet<FileActionType> requested = actions.Length == 0 ? null : [.. actions];
        foreach (var action in List)
        {
            if (requested is null || requested.Contains(action.Name))
                action.Command.RaiseCanExecuteChanged();
        }

        foreach (var action in ToggleActions)
        {
            if (requested is null || requested.Contains(action.FileAction.Name))
                action.FileAction.Command.RaiseCanExecuteChanged();
        }
    }

    private static readonly Dictionary<FileActionType, KeyGesture> Gestures = new()
    {
        { FileActionType.Home, new(Key.H, ModifierKeys.Alt) },
        { FileActionType.Filter, new(Key.F, ModifierKeys.Control) },
        { FileActionType.Cut, new(Key.X, ModifierKeys.Control) },
        { FileActionType.Copy, new(Key.C, ModifierKeys.Control) },
        { FileActionType.Restore, new(Key.R, ModifierKeys.Control) },
        { FileActionType.Delete, new(Key.Delete) },
        { FileActionType.Edit, new(Key.E, ModifierKeys.Control) },
        { FileActionType.Uninstall, new(Key.F11, ModifierKeys.Shift) },
        { FileActionType.PushPackages, new(Key.I, ModifierKeys.Alt) },
        { FileActionType.OpenSettings, new(Key.D0, ModifierKeys.Alt) },
        { FileActionType.Paste, new(Key.V, ModifierKeys.Control) },
    };

    public static readonly Dictionary<FileActionType, string> Icons = new()
    {
        { FileActionType.OpenFileOps, "\uF16A" },
        { FileActionType.PushFolders, "\uE8B7" },
        { FileActionType.NewFile, "\uE8A5" },
        { FileActionType.Package, "\uE7B8" },
        { FileActionType.New, "\uECC8" },
        { FileActionType.Cut, "\uE8C6" },
        { FileActionType.Copy, "\uE8C8" },
        { FileActionType.Paste, "\uE77F" },
        { FileActionType.Rename, "\uE8AC" },
        { FileActionType.Restore, "\uE845" },
        { FileActionType.Delete, "\uE74D" },
        { FileActionType.Uninstall, "\uE25B" },
        { FileActionType.More, "\uE712" },
        { FileActionType.UpdateModified, "\uE787" },
        { FileActionType.Edit, "\uE70F" },
        { FileActionType.Install, "\uE896" },
        { FileActionType.CopyToTemp, "\uF413" },
        { FileActionType.FileOpRemove, "\uE711" },
        { FileActionType.PauseLogs, "\uE769" },
        { FileActionType.FollowLink, "\uE838" },
        { FileActionType.PasteLink, "\uE1A5" },
        { FileActionType.HideSettings, "\uE761" },
        { FileActionType.SearchApkOnWeb, "\uF6FA" },
        { FileActionType.Home, "\uE80F" },
        { FileActionType.Refresh, "\uE72C" },
    };

    public static List<ToggleMenu> ToggleActions { get; } =
    [
        new(FileActionType.FileOpFilter,
            () => !App.ActiveFileOperations.IsActive,
            Strings.Resources.S_FILTER_FILE_OPS,
            "\uF16C",
            () => { },
            toggleOnClick: false,
            children: FileOpFilters.List.Select(f => new GeneralSubMenu(f.CheckBox))),
        new(FileActionType.FileOpStop,
            () => true,
            Strings.Resources.S_ENABLE_AUTO_PLAY,
            "\uE768",
            FileActionLogic.ToggleFileOpQ,
            Strings.Resources.S_AUTO_PLAY_DISABLE,
            Icons[FileActionType.PauseLogs]),
        new(FileActionType.PauseLogs,
            () => true,
            Strings.Resources.S_LOG_UPDATES_PAUSE,
            Icons[FileActionType.PauseLogs],
            () => App.RuntimeSettings.IsLogPaused ^= true,
            Strings.Resources.S_LOG_UPDATES_ALT),
        new(FileActionType.SortSettings,
            () => true,
            Strings.Resources.S_EXIT_SEARCH,
            Icons[FileActionType.FileOpRemove],
            FileActionLogic.ToggleSettingsSort,
            Strings.Resources.S_SEARCH_VIEW,
            "\uE721"),
        new(FileActionType.ExpandSettings,
            () => true,
            Strings.Resources.S_COLLAPSE_ALL,
            "\uE16A",
            FileActionLogic.ToggleSettingsExpand,
            Strings.Resources.S_EXPAND_ALL,
            "\uE169",
            isVisible: App.FileActions.IsExpandSettingsVisible),
        new(FileActionType.LogToggle,
            () => true,
            Strings.Resources.S_BUTTON_LOG,
            "\uE9A4",
            () => App.RuntimeSettings.IsLogOpen ^= true,
            isVisible: App.FileActions.IsLogToggleVisible),
        new(FileActionType.TerminalToggle,
            () => true,
            Strings.Resources.S_TERMINAL,
            "\uE756",
            () => App.RuntimeSettings.IsTerminalOpen ^= true),
    ];

    public static List<FileAction> List { get; } =
    [
        new(FileActionType.Home,
            () => App.FileActions.HomeEnabled && !App.FileActions.ListingInProgress,
            () => (Application.Current as App)?.RequestNavigation(new(Navigation.SpecialLocation.DriveView)),
            Strings.Resources.S_BUTTON_DRIVES,
            Gestures[FileActionType.Home]),
        new(FileActionType.KeyboardHome,
            () => App.FileActions.HomeEnabled && !App.FileActions.ListingInProgress && !App.FileActions.IsExplorerEditing,
            () => (Application.Current as App)?.RequestNavigation(new(Navigation.SpecialLocation.DriveView)),
            Strings.Resources.S_BUTTON_DRIVES,
            Gestures[FileActionType.Home],
            true),
        new(FileActionType.Back,
            () => NavHistory.BackAvailable && !App.FileActions.ListingInProgress,
            () => (Application.Current as App)?.RequestNavigation(new(Navigation.SpecialLocation.Back)),
            Strings.Resources.S_BUTTON_BACK,
            new(Key.Back),
            true),
        new(FileActionType.Forward,
            () => NavHistory.ForwardAvailable && !App.FileActions.ListingInProgress,
            () => (Application.Current as App)?.RequestNavigation(new(Navigation.SpecialLocation.Forward)),
            Strings.Resources.S_BUTTON_FORWARD,
            new(Key.Right, ModifierKeys.Alt),
            true),
        new(FileActionType.Up,
            () => App.FileActions.ParentEnabled && !App.FileActions.ListingInProgress,
            () => (Application.Current as App)?.RequestNavigation(new(Navigation.SpecialLocation.Up)),
            Strings.Resources.S_BUTTON_UP,
            new(Key.Up, ModifierKeys.Alt),
            true),
        new(FileActionType.Filter,
            () => App.FileActions.HomeEnabled,
            () => App.RuntimeSettings.IsSearchBoxFocused ^= true,
            Strings.Resources.S_BUTTON_FILTER,
            Gestures[FileActionType.Filter]),
        new(FileActionType.KeyboardFilter,
            () => App.FileActions.HomeEnabled && !App.FileActions.IsExplorerEditing,
            () => App.RuntimeSettings.IsSearchBoxFocused ^= true,
            Strings.Resources.S_BUTTON_FILTER,
            Gestures[FileActionType.Filter],
            true),
        new(FileActionType.OpenDevices,
            () => App.RuntimeSettings.IsWindowLoaded,
            ToggleDevicesPane,
            Strings.Resources.S_BUTTON_DEVICES,
            new(Key.D1, ModifierKeys.Alt),
            true),
        new(FileActionType.Pull,
            () => App.FileActions.PullEnabled,
            () => FileActionLogic.PullFiles(),
            App.FileActions.PullDescription,
            new(Key.C, ModifierKeys.Alt),
            true,
            clearClipboard: true),
        new(FileActionType.Push,
            () => App.FileActions.PushEnabled,
            () => { },
            Strings.Resources.S_BUTTON_PUSH,
            clearClipboard: true),
        new(FileActionType.ContextPush,
            () => App.FileActions.ContextPushEnabled,
            () => { },
            Strings.Resources.S_BUTTON_PUSH,
            clearClipboard: true),
        new(FileActionType.PushFolders,
            () => App.FileActions.PushFilesFoldersEnabled,
            () => FileActionLogic.PushItems(true, false),
            Strings.Resources.S_MENU_FOLDERS,
            clearClipboard: true),
        new(FileActionType.PushFiles,
            () => App.FileActions.PushFilesFoldersEnabled,
            () => FileActionLogic.PushItems(false, false),
            Strings.Resources.S_MENU_FILES,
            new(Key.V, ModifierKeys.Alt),
            true,
            clearClipboard: true),
        new(FileActionType.Refresh,
            () => App.FileActions.IsRefreshEnabled && !App.FileActions.ListingInProgress,
            () => (Application.Current as App)?.RequestUi(UiCommand.RefreshLocation),
            Strings.Resources.S_MENU_REFRESH,
            new(Key.F5),
            true),
        new(FileActionType.CopyCurrentPath,
            () => App.FileActions.IsCopyCurrentPathEnabled,
            () =>
            {
                if (Application.Current is App app)
                    _ = app.SetClipboardTextAsync(App.ExplorerState.CurrentPath);
            },
            Strings.Resources.S_MENU_COPY,
            new(Key.F6),
            true),
        new(FileActionType.More,
            () => App.FileActions.MoreEnabled,
            () => { },
            Strings.Resources.S_MENU_MORE),
        new(FileActionType.EditCurrentPath,
            () => App.FileActions.IsCopyCurrentPathEnabled,
            () => App.RuntimeSettings.IsPathBoxFocused = null,
            Strings.Resources.S_MENU_EDIT,
            new(Key.F6, ModifierKeys.Alt),
            true),
        new(FileActionType.ContextNew,
            () => App.FileActions.ContextNewEnabled,
            () => { },
            Strings.Resources.S_MENU_NEW),
        new(FileActionType.New,
            () => App.FileActions.NewEnabled,
            () => { },
            Strings.Resources.S_MENU_NEW),
        new(FileActionType.NewFolder,
            () => App.FileActions.NewEnabled,
            () => (Application.Current as App)?.RequestUi(UiCommand.NewFolder),
            Strings.Resources.S_MENU_FOLDER,
            clearClipboard: true),
        new(FileActionType.NewFile,
            () => App.FileActions.NewEnabled,
            () => (Application.Current as App)?.RequestUi(UiCommand.NewFile),
            Strings.Resources.S_MENU_FILE,
            clearClipboard: true),
        new(FileActionType.SelectAll,
            () => App.FileActions.IsExplorerVisible && !App.FileActions.IsExplorerEditing,
            () => (Application.Current as App)?.RequestUi(UiCommand.SelectAll),
            Strings.Resources.S_MENU_SELECT_ALL,
            new(Key.A, ModifierKeys.Control),
            true),
        new(FileActionType.KeyboardCut,
            () => App.FileActions.CutEnabled && !App.FileActions.IsExplorerEditing,
            () => FileActionLogic.CutItems(),
            Strings.Resources.S_MENU_CUT,
            Gestures[FileActionType.Cut],
            true),
        new(FileActionType.Cut,
            () => App.FileActions.CutEnabled,
            () => FileActionLogic.CutItems(),
            Strings.Resources.S_MENU_CUT,
            Gestures[FileActionType.Cut]),
        new(FileActionType.KeyboardCopy,
            () => App.FileActions.CopyEnabled && !App.FileActions.IsExplorerEditing,
            () => FileActionLogic.CutItems(true),
            Strings.Resources.S_MENU_COPY,
            Gestures[FileActionType.Copy],
            true),
        new(FileActionType.Copy,
            () => App.FileActions.CopyEnabled,
            () => FileActionLogic.CutItems(true),
            Strings.Resources.S_MENU_COPY,
            Gestures[FileActionType.Copy]),
        new(FileActionType.KeyboardPaste,
            () => App.FileActions.IsKeyboardPasteEnabled && !App.FileActions.IsExplorerEditing,
            () => FileActionLogic.PasteFiles(App.ExplorerState.SelectedFiles),
            App.FileActions.PasteDescription,
            Gestures[FileActionType.Paste],
            true),
        new(FileActionType.Paste,
            () => App.FileActions.PasteEnabled,
            () => FileActionLogic.PasteFiles(App.ExplorerState.SelectedFiles),
            App.FileActions.PasteDescription,
            Gestures[FileActionType.Paste]),
        new(FileActionType.PasteLink,
            () => App.FileActions.IsPasteLinkEnabled,
            () => FileActionLogic.PasteFiles(App.ExplorerState.SelectedFiles, isLink: true),
            Strings.Resources.S_MENU_PASTE_LINK,
            new(Key.L, ModifierKeys.Control),
            true),
        new(FileActionType.Rename,
            () => App.FileActions.RenameEnabled,
            () => (Application.Current as App)?.RequestUi(UiCommand.Rename),
            Strings.Resources.S_MENU_RENAME,
            new(Key.F2),
            true,
            clearClipboard: true),
        new(FileActionType.KeyboardRestore,
            () => App.FileActions.RestoreEnabled && !App.FileActions.IsExplorerEditing,
            FileActionLogic.RestoreItems,
            App.FileActions.RestoreDescription,
            Gestures[FileActionType.Restore],
            true,
            clearClipboard: true),
        new(FileActionType.Restore,
            () => App.FileActions.RestoreEnabled,
            FileActionLogic.RestoreItems,
            App.FileActions.RestoreDescription,
            Gestures[FileActionType.Restore],
            clearClipboard: true),
        new(FileActionType.KeyboardDelete,
            () => App.FileActions.DeleteEnabled && !App.FileActions.IsExplorerEditing,
            FileActionLogic.DeleteFiles,
            App.FileActions.DeleteDescription,
            Gestures[FileActionType.Delete],
            true,
            clearClipboard: true),
        new(FileActionType.Delete,
            () => App.FileActions.DeleteEnabled,
            FileActionLogic.DeleteFiles,
            App.FileActions.DeleteDescription,
            Gestures[FileActionType.Delete],
            clearClipboard: true),
        new(FileActionType.CopyItemPath,
            () => App.FileActions.IsCopyItemPathEnabled,
            FileActionLogic.CopyItemPath,
            App.FileActions.CopyPathDescription,
            new(Key.C, ModifierKeys.Control | ModifierKeys.Shift),
            true),
        new(FileActionType.Package,
            () => App.FileActions.PackageActionsEnabled,
            () => { },
            Strings.Resources.S_MENU_PACKAGE),
        new(FileActionType.UpdateModified,
            () => App.FileActions.UpdateModifiedEnabled,
            FileActionLogic.UpdateModifiedDates,
            Strings.Resources.S_MENU_UPDATE_MODIFIED,
            new(Key.U, ModifierKeys.Control),
            true),
        new(FileActionType.Edit,
            () => App.FileActions.EditFileEnabled,
            FileActionLogic.OpenEditor,
            Strings.Resources.S_SETTINGS_DOUBLE_CLICK_OPEN,
            Gestures[FileActionType.Edit],
            true),
        new(FileActionType.CloseEditor,
            () => true,
            FileActionLogic.OpenEditor,
            Strings.Resources.S_MENU_CLOSE_EDITOR,
            Gestures[FileActionType.Edit],
            true),
        new(FileActionType.SaveEditor,
            () => App.FileActions.IsEditorTextChanged,
            FileActionLogic.SaveEditorText,
            Strings.Resources.S_MENU_SAVE_CHANGES,
            new(Key.S, ModifierKeys.Control),
            true,
            clearClipboard: true),
        new(FileActionType.Install,
            () => App.FileActions.InstallPackageEnabled,
            FileActionLogic.InstallPackages,
            Strings.Resources.S_MENU_INSTALL,
            new(Key.F10, ModifierKeys.Shift),
            true),
        new(FileActionType.Uninstall,
            () => App.FileActions.UninstallPackageEnabled,
            FileActionLogic.UninstallPackages,
            Strings.Resources.S_UNINSTALL,
            Gestures[FileActionType.Uninstall],
            true),
        new(FileActionType.SubMenuUninstall,
            () => App.FileActions.SubmenuUninstallEnabled,
            FileActionLogic.UninstallPackages,
            Strings.Resources.S_UNINSTALL,
            Gestures[FileActionType.Uninstall]),
        new(FileActionType.CopyToTemp,
            () => App.FileActions.CopyToTempEnabled,
            FileActionLogic.CopyToTemp,
            Strings.Resources.S_MENU_COPY_TEMP,
            new(Key.F12, ModifierKeys.Shift),
            true,
            clearClipboard: true),
        new(FileActionType.PushPackages,
            () => App.FileActions.PushPackageEnabled,
            FileActionLogic.PushPackages,
            Strings.Resources.S_PUSH_PKG,
            Gestures[FileActionType.PushPackages],
            true),
        new(FileActionType.ContextPushPackages,
            () => App.FileActions.ContextPushPackagesEnabled,
            FileActionLogic.PushPackages,
            Strings.Resources.S_PUSH_PKG,
            Gestures[FileActionType.PushPackages]),
        new(FileActionType.None,
            new(),
            "",
            new(Key.F10),
            true),
        new(FileActionType.OpenSettings,
            () => App.RuntimeSettings.IsWindowLoaded,
            ToggleSettingsPane,
            Strings.Resources.S_SETTINGS_TITLE,
            Gestures[FileActionType.OpenSettings],
            true),
        new(FileActionType.HideSettings,
            () => true,
            ToggleSettingsPane,
            Strings.Resources.S_ACTION_HIDE,
            Gestures[FileActionType.OpenSettings]),
        new(FileActionType.OpenFileOps,
            () => App.RuntimeSettings.IsWindowLoaded,
            () => App.RuntimeSettings.IsOperationsViewOpen ^= true,
            Strings.Resources.S_FILE_OP_TOOLTIP,
            new(Key.D9, ModifierKeys.Alt),
            true),
        ToggleActions.Find(a => a.FileAction.Name is FileActionType.FileOpStop).FileAction,
        new(FileActionType.FileOpRemove,
            () => !App.ActiveFileOperations.IsActive && App.ActiveFileOperations.Operations.Count > 0,
            FileActionLogic.RemoveFileOps,
            App.FileActions.RemoveFileOpDescription),
        ToggleActions.Find(a => a.FileAction.Name is FileActionType.LogToggle).FileAction,
        ToggleActions.Find(a => a.FileAction.Name is FileActionType.PauseLogs).FileAction,
        new(FileActionType.ClearLogs,
            () => App.CommandLog.Count > 0,
            () => (Application.Current as App)?.RequestUi(UiCommand.ClearLogs),
            Strings.Resources.S_MENU_CLEAR_LOG),
        new(FileActionType.ResetSettings,
            () => true,
            FileActionLogic.ResetAppSettings,
            Strings.Resources.S_RESET_SETTINGS_TITLE),
        ToggleActions.Find(a => a.FileAction.Name is FileActionType.SortSettings).FileAction,
        ToggleActions.Find(a => a.FileAction.Name is FileActionType.ExpandSettings).FileAction,
        new(FileActionType.FileOpValidate,
            () => !App.ActiveFileOperations.IsActive && App.FileActions.SelectedFileOps.Value.AnyAll(op => op.ValidationAllowed),
            Security.ValidateOps,
            App.FileActions.ValidateDescription),
        new(FileActionType.FollowLink,
            () => App.FileActions.IsFollowLinkEnabled,
            FileActionLogic.FollowLink,
            Strings.Resources.S_MENU_OPEN_LOCATION,
            new(Key.Enter, ModifierKeys.Shift),
            true),
        new(FileActionType.SearchApkOnWeb,
            () => App.FileActions.IsApkWebSearchEnabled,
            FileActionLogic.ApkWebSearch,
            Strings.Resources.S_MENU_SEARCH_WEB,
            new(Key.O, ModifierKeys.Control),
            true),
        new(FileActionType.CopyMessageToClipboard,
            () => !string.IsNullOrEmpty(App.FileActions.MessageToCopy),
            () =>
            {
                if (Application.Current is App app)
                    _ = app.SetClipboardTextAsync(App.FileActions.MessageToCopy);
                App.FileActions.MessageToCopy = "";
            },
            Strings.Resources.S_BUTTON_COPY_TO_CLIP),
        new(FileActionType.NavHistory,
            () => NavHistory.MenuHistory.Value.Any() && !App.FileActions.ListingInProgress,
            () => { },
            Strings.Resources.S_NAV_HISTORY),
        new(FileActionType.OpenPackageLocation,
            () => App.FileActions.IsOpenApkLocationEnabled,
            () => FileActionLogic.OpenApkLocation(),
            Strings.Resources.S_MENU_OPEN_LOCATION,
            new(Key.Enter),
            true),
    ];

    public static List<KeyBinding> Bindings =>
        [.. List.Where(a => a.UseForGesture)
            .Select(action => action.KeyBinding)
            .Where(binding => binding is not null)];

    private static void ToggleSettingsPane()
    {
        App.RuntimeSettings.IsSettingsPaneOpen ^= true;

        if (App.RuntimeSettings.IsSettingsPaneOpen)
            App.RuntimeSettings.IsDevicesPaneOpen = false;
    }

    private static void ToggleDevicesPane()
    {
        App.RuntimeSettings.IsDevicesPaneOpen ^= true;

        if (App.RuntimeSettings.IsDevicesPaneOpen)
            App.RuntimeSettings.IsSettingsPaneOpen = false;
    }
}

public class FileAction : ViewModelBase
{
    public enum FileActionType
    {
        None,
        Home,
        KeyboardHome,
        Back,
        Forward,
        Up,
        Refresh,
        CopyCurrentPath,
        EditCurrentPath,
        Filter,
        KeyboardFilter,
        OpenDevices,
        Pull,
        Push,
        ContextPush,
        PushFolders,
        PushFiles,
        ContextNew,
        New,
        NewFolder,
        NewFile,
        SelectAll,
        KeyboardCut,
        Cut,
        KeyboardCopy,
        Copy,
        KeyboardPaste,
        Paste,
        Rename,
        KeyboardRestore,
        Restore,
        KeyboardDelete,
        Delete,
        CopyItemPath,
        More,
        UpdateModified,
        Edit,
        Package,
        Install,
        Uninstall,
        SubMenuUninstall,
        CopyToTemp,
        PushPackages,
        ContextPushPackages,
        OpenSettings,
        HideSettings,
        OpenFileOps,
        CloseEditor,
        SaveEditor,
        FileOpStop,
        FileOpRemove,
        PauseLogs,
        ClearLogs,
        ResetSettings,
        SortSettings,
        ExpandSettings,
        LogToggle,
        FileOpValidate,
        FileOpFilter,
        FollowLink,
        PasteLink,
        SearchApkOnWeb,
        CopyMessageToClipboard,
        NavHistory,
        TerminalToggle,
        OpenPackageLocation,
    }

    public FileActionType Name { get; }

    public BaseAction Command { get; }

    public KeyGesture Gesture { get; }

    public KeyBinding KeyBinding { get; }

    private string description;
    public string Description
    {
        get => description;
        private set => Set(ref description, value);
    }

    public bool UseForGesture { get; }

    public string GestureString
    {
        get
        {
            if (Gesture is null)
                return null;

            string result = "";
            if (Gesture.Modifiers is not ModifierKeys.None)
            {
                result = Gesture.Modifiers.ToString();

                result = result.Replace("Control", "Ctrl");
                result = result.Replace(",", "+");
                result = result.Replace(" ", "");

                result += "+";
            }

            string key = Gesture.Key.ToString();
            if (key.Length > 1 && key[0] == 'D' && char.IsDigit(key[1]))
                key = key[1..];

            result += key;

            result = result.Replace("Delete", "Del");
            result = result.Replace("Return", "Enter");

            return result;
        }
    }

    public FileAction(FileActionType name,
                      BaseAction command,
                      string description,
                      KeyGesture gesture = null,
                      bool useForGesture = false,
                      bool clearClipboard = false)
    {
        Name = name;
        Command = command;
        Gesture = gesture;
        Description = description;

        if (gesture is not null)
            KeyBinding = new(Command.Command, gesture);

        UseForGesture = useForGesture;

        ((CommandHandler)Command.Command).OnExecute.PropertyChanged += (object sender, PropertyChangedEventArgs<bool> e) =>
        {
            if (clearClipboard && App.CopyPaste.IsSelf)
                App.CopyPaste.Clear();
        };
    }

    public FileAction(FileActionType name,
                      Func<bool> canExecute,
                      Action action,
                      string description = "",
                      KeyGesture gesture = null,
                      bool useForGesture = false,
                      bool clearClipboard = false)
        : this(name, new(canExecute, action), description, gesture, useForGesture, clearClipboard)
    { }

    public FileAction(FileActionType name,
                      Func<bool> canExecute,
                      Action action,
                      ObservableProperty<string> description,
                      KeyGesture gesture = null,
                      bool useForGesture = false,
                      bool clearClipboard = false)
        : this(name, new(canExecute, action), description.Value, gesture, useForGesture, clearClipboard)
    {
        description.PropertyChanged += (object sender, PropertyChangedEventArgs<string> e) => Description = e.NewValue;
    }

    public override string ToString()
    {
        return Name.ToString();
    }
}
