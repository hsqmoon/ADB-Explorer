using ADB_Explorer.Controls;
using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Resources;
using ADB_Explorer.Services;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Xml;
using static ADB_Explorer.Helpers.VisibilityHelper;
using static ADB_Explorer.Models.AbstractFile;
using static ADB_Explorer.Models.AdbExplorerConst;
using static ADB_Explorer.App;
using static ADB_Explorer.Services.FileAction;

namespace ADB_Explorer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int MAX_LOG_TEXT_LENGTH = 200_000;

    private readonly DispatcherTimer SelectionTimer = new() { Interval = SELECTION_CHANGED_DELAY };
    private IUiWorkScheduler UiScheduler;
    private int dragIconVersion;
    private readonly ThemeService ThemeService = new();
    private DirectorySession CurrentDirectorySession => ((App)Application.Current).CurrentDirectorySession;
    private FileOperationQueue FileOpQ => App.ActiveFileOperations;
    private Devices DevicesObject => App.ActiveDevices;
    private string CurrentPath => App.ExplorerState.CurrentPath;
    private DriveViewModel CurrentDrive => App.ExplorerState.CurrentDrive;
    private IEnumerable<FileClass> SelectedFiles
    {
        get => App.ExplorerState.SelectedFiles;
        set => App.ExplorerState.SelectedFiles = value;
    }
    private IEnumerable<Package> SelectedPackages
    {
        get => App.ExplorerState.SelectedPackages;
        set => App.ExplorerState.SelectedPackages = value;
    }

    private static Point NullPoint => new(-1, -1);
    private bool HasSavedWindowBounds =>
        TryGetStoredDouble(AppSettings.SystemVals.windowLeft, out _)
        && TryGetStoredDouble(AppSettings.SystemVals.windowTop, out _)
        && TryGetStoredDouble(AppSettings.SystemVals.windowWidth, out _)
        && TryGetStoredDouble(AppSettings.SystemVals.windowHeight, out _);

    private double? RowHeight { get; set; }
    private double ColumnHeaderHeight => (double)FindResource("DataGridColumnHeaderHeight") + ScrollContentPresenterMargin;
    private double ScrollContentPresenterMargin => RuntimeSettings.UseFluentStyles ? ((Thickness)FindResource("DataGridScrollContentPresenterMargin")).Top : 0;
    private double DataGridContentWidth
        => StyleHelper.FindDescendant<ItemsPresenter>(ExplorerGrid) is ItemsPresenter presenter ? presenter.ActualWidth : 0;

    public string SelectedFilesTotalSize => (SelectedFiles is not null && FileHelper.TotalSize(SelectedFiles) is long size and > 0) ? size.BytesToSize() : "";
    public string SelectedFilesCount => $"{ExplorerGrid.SelectedItems.Count}";

    private int ClickCount = 0;
    private bool WasSelected;
    private bool WasEditing;
    private bool WasDragging;
    private Point MouseDownPoint;
    private DateTime appDataClick;
    private DragWindow dw;
    private readonly Queue<Log> pendingLogEntries = new();
    private readonly object pendingLogEntriesLock = new();
    private bool rebuildLogRequested;
    private DeviceViewModel[] pendingVisibleDevices = [];
    private long visibleDevicesVersion;
    private long applyingVisibleDevicesVersion = -1;
    private int visibleDeviceIndex;
    private object[] pendingExplorerSelection = [];
    private long explorerSelectionVersion;
    private long applyingExplorerSelectionVersion = -1;
    private int explorerSelectionIndex;
    private bool selectExplorerItems;
    private bool suppressExplorerSelectionChanged;
    private int explorerSelectionResetVersion;
    private SubMenu[] pendingExplorerContextItems = [];
    private long explorerContextVersion;
    private long applyingExplorerContextVersion = -1;
    private int explorerContextIndex;
    private CancellationTokenSource packageViewCancellation;
    private Package[] pendingVisiblePackages = [];
    private long packageViewVersion;
    private long applyingPackageViewVersion = -1;
    private int packageViewIndex;
    private string packageSortProperty = nameof(Package.Type);
    private ListSortDirection packageSortDirection = ListSortDirection.Descending;

    private bool IsInEditMode
    {
        get
        {
            if (FileActions.IsAppDrive || !ExplorerGrid.SelectedCells.Any())
                return false;

            var cell = CellConverter.GetDataGridCell(ExplorerGrid.SelectedCells[1]);
            return cell switch
            {
                null => false,
                _ => cell.IsEditing
            };
        }
        set
        {
            var cell = CellConverter.GetDataGridCell(ExplorerGrid.SelectedCells[1]);
            if (cell is not null)
            {
                cell.IsEditing = value;
                FileActions.IsExplorerEditing = value;
            }
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public MainWindow()
    { }

    internal void InitializeView()
    {
        var app = (App)Application.Current;
        UiScheduler = app.AttachRuntime(this);
        app.StartRuntime();
        InitializeComponent();

        KeyDown += new KeyEventHandler(OnButtonKeyDown);
        PreviewTextInput += new TextCompositionEventHandler(MainWindow_PreviewTextInput);

        DevicesList.ItemsSource = DevicesObject.VisibleDevices;

        ApplyFileOperationFilters();

        SelectionTimer.Tick += SelectionTimer_Tick;

        Settings.PropertyChanged += Settings_PropertyChanged;
        RuntimeSettings.UiChangeRequested += RuntimeSettings_UiChangeRequested;
        ThemeService.PropertyChanged += ThemeService_PropertyChanged;
        CommandLog.CollectionChanged += CommandLog_CollectionChanged;
        FileOpQ.PropertyChanged += FileOperationQueue_PropertyChanged;
        FileActions.PropertyChanged += FileActions_PropertyChanged;
        DevicesObject.UIList.CollectionChanged += UIList_CollectionChanged;
        FileOpFilters.CheckedFilterCount.PropertyChanged += CheckedFilterCount_PropertyChanged;

        AppActions.Bindings.ForEach(binding =>
        {
            InputBindings.Add(binding);
            ExplorerGrid.InputBindings.Add(binding);
        });

        UpperProgressBar.DataContext = FileOpQ;
        CurrentOperationDetailedDataGrid.ItemsSource = FileOpQ.VisibleOperations;
        ((DataGrid)FindResource("CurrentOperationDataGrid")).ItemsSource = FileOpQ.VisibleOperations;
        UpdateFileOp();

        NativeMethods.InterceptClipboard.Init(this, CopyPaste.GetClipboardPasteItems, IpcService.AcceptIpcMessage);

#if DEBUG
        DeviceHelper.TestDevices();
#endif
    }

    internal void InitializeTheme() => SetTheme();

    internal void InitializeFont() => SettingsHelper.SetSymbolFont();

    internal void InitializeRenderMode() => SetRenderMode();

    internal Task StartThemeWatcherAsync(CancellationToken cancellationToken) =>
        ThemeService.StartWatchingAsync(cancellationToken);

    internal void InitializeSettingsSources()
    {
        AdbHelper.InitializeMdns();
        SettingsList.ItemsSource = UISettings.GroupedSettings;
        SortedSettings.ItemsSource = UISettings.SortSettings;
        NotificationsList.ItemsSource = UISettings.Notifications;
    }

    internal void InitializeToolbars()
    {
        NavigationToolBar.ItemsSource = Services.NavigationToolBar.List;
        MainToolBar.ItemsSource = Services.MainToolBar.List;
    }

    internal void InitializeSettingsState()
    {
        FileActionLogic.UpdateFileActions();
        ScheduleFilterDevices();

        FileActions.IsLogToggleVisible.Value = Settings.EnableLog;
        Settings.UnrootOnDisconnect ??= false;
    }

    internal void InitializeFileOperationModel() => FileOpColumns.Init();

    internal void InitializeFileOperationColumns() =>
        FileOpColumns.List.ForEach(c => CurrentOperationDetailedDataGrid.Columns.Add(c.Column));

    internal void InitializeFileOperationFilter() => UpdateFileOpFilterCheck();

    internal void ActivateRuntime()
    {
        RuntimeSettings.IsDevicesPaneOpen = true;
        RuntimeSettings.IsWindowLoaded = true;

        SearchBoxMaxWidth();
        AppActions.RaiseCanExecuteChanged();
    }

    internal void ShowAuxiliaryWindows()
    {
        dw = new();
        dw.Show();
    }

    internal void SetAdbRuntimeAvailable(bool adbReady)
    {
        if (adbReady)
            DeviceHelper.UpdateWsaPkgStatus();
    }

    private void MainWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (RuntimeSettings.IsDevicesPaneOpen
            || RuntimeSettings.IsSettingsPaneOpen
            || SearchBox.IsFocused
            || IsTerminalFocused()
            || NavigationBox.Mode is NavigationBox.ViewMode.Path
            || FileActions.IsExplorerEditing)
            return;

        var selected = ExplorerGrid.SelectedItems.Count;
        var selectedIndex = ExplorerGrid.SelectedIndex;
        object altItem = null;

        for (int i = 0; i < ExplorerGrid.Items.Count; i++)
        {
            var item = ExplorerGrid.Items[i];
            var name = item.ToString();

            if (name.StartsWith(e.Text, StringComparison.OrdinalIgnoreCase))
            {
                if (selected != 1 || selectedIndex < i)
                {
                    SelectExplorerItem(item);
                    break;
                }
                else 
                    altItem ??= item;
            }
        }

        if (selectedIndex == ExplorerGrid.SelectedIndex && altItem is not null)
            SelectExplorerItem(altItem);
    }

    private void CheckedFilterCount_PropertyChanged(object sender, PropertyChangedEventArgs<int> e)
    {
        UpdateFileOpFilterCheck();

        ApplyFileOperationFilters();
    }

    private static void UpdateFileOpFilterCheck()
    {
        AppActions.ToggleActions.Find(a => a.FileAction.Name is FileActionType.FileOpFilter).Button.IsChecked
            = FileOpFilters.CheckedFilterCount + 1 < FileOpFilters.List.Count;
    }

    private void UIList_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        ScheduleFilterDevices();
    }

    internal void RefreshServerHealth()
    {
        RuntimeSettings.RefreshServerResponseStatus();

        if (MdnsService?.State is MDNS.MdnsState.Running
            && DateTime.Now.Subtract(RuntimeSettings.LastServerResponse) > MDNS_FORCE_CONNECT_TIME)
        {
            MdnsService.State = MDNS.MdnsState.Disabled;
            UpdateMdns();
        }
    }

    private void RuntimeSettings_UiChangeRequested(RuntimeUiChange change)
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        void HandlePropertyChange()
        {
            switch (change)
            {
                case RuntimeUiChange.MdnsExpander:
                    if (MdnsService?.State is not MDNS.MdnsState.Disabled)
                    {
                        DeviceHelper.CollapseDevices();

                        if (RuntimeSettings.IsMdnsExpanderOpen)
                            UpdateQrClass();
                    }
                    break;

                case RuntimeUiChange.SearchText:
                    FilterSettings();
                    break;

                case RuntimeUiChange.PathBoxFocus:
                    IsPathBoxFocused(RuntimeSettings.IsPathBoxFocused
                                     ?? NavigationBox.Mode
                                     is NavigationBox.ViewMode.Breadcrumbs);
                    break;

                case RuntimeUiChange.SearchBoxFocus:
                    if (!RuntimeSettings.IsSearchBoxFocused && SearchBox.IsKeyboardFocusWithin)
                        SettingsSplitView.Focus();
                    break;

                case RuntimeUiChange.TerminalVisibility:
                    if (RuntimeSettings.IsTerminalOpen)
                        _ = TerminalControl.OpenAsync(CurrentTerminalDeviceId);
                    else
                        _ = TerminalControl.CloseAsync();
                    break;

                case RuntimeUiChange.CurrentDevice:
                    if (RuntimeSettings.IsTerminalOpen)
                        _ = TerminalControl.SetDeviceAsync(CurrentTerminalDeviceId);
                    break;

                case RuntimeUiChange.SettingsGroups:
                    SettingsAboutExpander.IsExpanded = RuntimeSettings.GroupsExpanded;
                    break;

                case RuntimeUiChange.Cursor:
                    Cursor = RuntimeSettings.MainCursor;
                    break;

                case RuntimeUiChange.PaneVisibility:
                    DeviceHelper.CollapseDevices();
                    if (RuntimeSettings.IsDevicesPaneOpen)
                        ScheduleFilterDevices();
                    break;

            }
        }

        UiScheduler.EnqueueLatest(
            $"runtime-change.{change}",
            $"runtime-change.{change}",
            HandlePropertyChange);
    }

    private void ApplyFileOperationFilters() => FileOpQ.SetVisibleFilters(
        FileOpFilters.List
            .Where(filter => filter.IsChecked is true)
            .Select(filter => filter.Type));

    internal void SetExplorerSource(System.Collections.IEnumerable source)
    {
        if (!ReferenceEquals(ExplorerGrid.ItemsSource, source))
            ExplorerGrid.ItemsSource = source;

        UiScheduler.EnqueueLatest(
            "explorer.filter",
            "explorer.filter",
            FilterExplorerItems);
        if (App.ExplorerState.Packages.Count == 0
            && App.ExplorerState.VisiblePackages.Count > 0)
        {
            SchedulePackageViewRefresh();
        }
    }

    internal void SelectExplorerItem(object item)
    {
        if (item is null)
        {
            ExplorerGrid.UnselectAll();
            return;
        }

        ExplorerGrid.ScrollIntoView(item);
        ExplorerGrid.SelectedItem = item;
    }

    internal void ExecuteUiCommand(UiCommand command)
    {
        switch (command)
        {
            case UiCommand.NewFolder:
                NewItem(true);
                break;
            case UiCommand.NewFile:
                NewItem(false);
                break;
            case UiCommand.Rename:
                IsInEditMode ^= true;
                break;
            case UiCommand.SelectAll:
                ScheduleToggleExplorerSelection();
                break;
            case UiCommand.FilterDrives:
                FilterDrives();
                break;
            case UiCommand.FilterDevices:
                ScheduleFilterDevices();
                break;
            case UiCommand.FilterActions:
                ScheduleExplorerContextMenuRefresh();
                break;
            case UiCommand.ClearNavigation:
                ClearNavBox();
                break;
            case UiCommand.AutoHideSearch:
                if (Width < MAX_WINDOW_WIDTH_FOR_SEARCH_AUTO_COLLAPSE)
                    RuntimeSettings.IsSearchBoxFocused = false;
                if (NavigationBox.Mode is not NavigationBox.ViewMode.Path)
                    SettingsSplitView.Focus();
                break;
            case UiCommand.ClearLogs:
                ClearLogs();
                break;
            case UiCommand.SortFileOperations:
                FileOpQ.RefreshView();
                break;
            case UiCommand.RefreshBreadcrumbs:
                NavigationBox.Refresh();
                break;
        }
    }

    private void ClearNavBox()
    {
        NavigationBox.Path = null;
        NavigationBox.Mode = NavigationBox.ViewMode.None;
    }

    private void SettingsSearchBox_FocusChanged(object sender, RoutedEventArgs e)
    {
        if (SettingsSearchBox.IsFocused)
            RuntimeSettings.IsOperationsViewOpen = false;

        if (!Settings.DisableAnimation)
            Settings.IsAnimated = !SettingsSearchBox.IsFocused;
    }

    private void FileActions_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FileActionsEnable.ExplorerFilter):
                FilterExplorerItems();

                break;

            case nameof(FileActionsEnable.PasteEnabled):
                ScheduleExplorerContextMenuRefresh();

                break;
        }
    }

    private void FileOperationQueue_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (FileOperationQueue.NotifyProperties.Contains(e.PropertyName))
        {
            UpdateFileOp();

            if (e.PropertyName == nameof(FileOperationQueue.IsActive))
            {
                CurrentOperationDetailedDataGrid.UnselectAll();
                FileOpQ.RefreshView();

                AppActions.RaiseCanExecuteChanged();
            }
        }

        if (e.PropertyName is nameof(FileOperationQueue.CurrentChanged))
        {
            FileOpQ.RefreshView();
            FileActionLogic.UpdateFileOpControls();
        }

        if (!FileOpQ.IsActive)
            UpdateSelectedFileOp();
    }

    private void UpdateFileOp(bool onlyProgress = true)
    {
        if (!onlyProgress)
            FileActionLogic.UpdateFileOpControls();

        if (FileOpQ.AnyFailedOperations)
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Error;
        else if (FileOpQ.IsActive)
        {
            if (FileOpQ.Progress == 0)
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
            else
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
        }
        else
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
    }

    private void CommandLog_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        lock (pendingLogEntriesLock)
        {
            if (e.Action is NotifyCollectionChangedAction.Add && e.NewItems is not null && !RuntimeSettings.IsLogPaused)
            {
                foreach (var item in e.NewItems.Cast<Log>())
                {
                    pendingLogEntries.Enqueue(item);
                }
            }
            else if (e.Action is NotifyCollectionChangedAction.Reset)
            {
                pendingLogEntries.Clear();
                rebuildLogRequested = true;
            }
        }

        ScheduleLogUiUpdate();
    }

    private void ScheduleLogUiUpdate()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        UiScheduler.EnqueueLatest("command-log.view", "command-log.view", ProcessPendingLogUiUpdates);
    }

    private void ProcessPendingLogUiUpdates()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        Queue<Log> logBatch = new();
        bool shouldRebuild;

        lock (pendingLogEntriesLock)
        {
            while (pendingLogEntries.Count > 0 && logBatch.Count < 8)
            {
                logBatch.Enqueue(pendingLogEntries.Dequeue());
            }

            shouldRebuild = rebuildLogRequested;
            rebuildLogRequested = false;
        }

        AppActions.RaiseCanExecuteChanged(FileActionType.ClearLogs);

        if (shouldRebuild)
        {
            RebuildLogTextBox();
        }
        else if (logBatch.Count > 0)
        {
            string text = string.Join(Environment.NewLine, logBatch.Select(item => item.ToString()));
            if (!string.IsNullOrEmpty(text))
                LogTextBox.AppendText($"{text}{Environment.NewLine}");

            TrimLogTextBox();
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
            LogTextBox.ScrollToEnd();
        }

        bool shouldScheduleAnotherPass;
        lock (pendingLogEntriesLock)
        {
            shouldScheduleAnotherPass = rebuildLogRequested || pendingLogEntries.Count > 0;
        }

        if (shouldScheduleAnotherPass)
            ScheduleLogUiUpdate();
    }

    private void TrimLogTextBox()
    {
        if (LogTextBox.Text.Length <= MAX_LOG_TEXT_LENGTH)
            return;

        int startIndex = LogTextBox.Text.Length - MAX_LOG_TEXT_LENGTH;
        int lineStart = LogTextBox.Text.IndexOf(Environment.NewLine, startIndex, StringComparison.Ordinal);

        LogTextBox.Text = lineStart >= 0
            ? LogTextBox.Text[(lineStart + Environment.NewLine.Length)..]
            : LogTextBox.Text[startIndex..];
    }

    private void RebuildLogTextBox()
    {
        LogTextBox.Text = string.Join(Environment.NewLine, CommandLog.Select(log => log.ToString()));

        if (LogTextBox.Text.Length > 0)
            LogTextBox.AppendText(Environment.NewLine);

        TrimLogTextBox();
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
        LogTextBox.ScrollToEnd();
    }

    private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ForceFluentStyles):
                SettingsHelper.SetSymbolFont();
                NavigationBox.Refresh();
                RowHeight = null;
                break;
            case nameof(AppSettings.Theme):
                SetTheme(Settings.Theme);
                break;
            case nameof(AppSettings.EnableMdns):
                AdbHelper.EnableMdns();
                break;
            case nameof(AppSettings.ShowHiddenItems):
                FilterExplorerItems();
                break;
            case nameof(AppSettings.ShowExtensions):
                CurrentDirectorySession?.RefreshDisplayNames();
                break;
            case nameof(AppSettings.ShowSystemPackages):
                if (DevicesObject.Current is null)
                    return;

                if (FileActions.IsAppDrive)
                    FileActionLogic.UpdatePackages(true);
                else if (!FileActions.IsExplorerVisible)
                    FileActionLogic.UpdatePackages();
                break;
            case nameof(AppSettings.EnableLog):
                FileActions.IsLogToggleVisible.Value = Settings.EnableLog;

                if (!Settings.EnableLog)
                {
                    AppActions.ToggleActions.Find(a => a.FileAction.Name is FileAction.FileActionType.LogToggle).Toggle(false);
                    ClearLogs();
                }

                break;
            case nameof(AppSettings.SwRender):
                SetRenderMode();
                break;
            case nameof(AppSettings.EnableRecycle) or nameof(AppSettings.EnableApk):
                if (DevicesObject.Current is null)
                    return;

                FileActionLogic.UpdateFileActions();

                if (NavHistory.Current.Location is Navigation.SpecialLocation.DriveView)
                    _ = FileActionLogic.RefreshDrives();

                FilterDrives();

                break;
            case nameof(AppSettings.SaveDevices):
                if (Settings.SaveDevices && !DevicesObject.HistoryDeviceViewModels.Any())
                    ((App)Application.Current).LoadHistoryDevices();

                ScheduleFilterDevices();
                break;
        }
    }

    private void ThemeService_PropertyChanged(object sender, PropertyChangedEventArgs e) =>
        UiScheduler.EnqueueLatest("theme.apply", "theme.apply", SetTheme);

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        DeviceHelper.CancelCurrentDeviceWork();
        FileOpQ.Stop();
        if (TerminalControl is not null)
            _ = TerminalControl.ShutdownAsync();

        Settings.PropertyChanged -= Settings_PropertyChanged;
        RuntimeSettings.UiChangeRequested -= RuntimeSettings_UiChangeRequested;
        ThemeService.PropertyChanged -= ThemeService_PropertyChanged;
        CommandLog.CollectionChanged -= CommandLog_CollectionChanged;
        FileOpQ.PropertyChanged -= FileOperationQueue_PropertyChanged;
        FileActions.PropertyChanged -= FileActions_PropertyChanged;
        DevicesObject.UIList.CollectionChanged -= UIList_CollectionChanged;
        FileOpFilters.CheckedFilterCount.PropertyChanged -= CheckedFilterCount_PropertyChanged;

        packageViewCancellation?.Cancel();
        packageViewCancellation?.Dispose();
        packageViewCancellation = null;
        ThemeService.Dispose();

        dw?.Close();
        NativeMethods.InterceptClipboard.Close();
        StoreClosingValues();
        ((App)Application.Current).StopRuntime();
    }

    internal void UpdateFileOperationView() => UpdateFileOp();

    private void StoreClosingValues()
    {
        Storage.StoreValue(AppSettings.SystemVals.windowMaximized, WindowState == WindowState.Maximized);

        var bounds = GetWindowBoundsToStore();
        Storage.StoreValue(AppSettings.SystemVals.windowLeft, bounds.Left);
        Storage.StoreValue(AppSettings.SystemVals.windowTop, bounds.Top);
        Storage.StoreValue(AppSettings.SystemVals.windowWidth, bounds.Width);
        Storage.StoreValue(AppSettings.SystemVals.windowHeight, bounds.Height);

        var detailedVisible = RuntimeSettings.IsOperationsViewOpen && Settings.EnableCompactView;
        Storage.StoreValue(AppSettings.SystemVals.detailedVisible, detailedVisible);
        if (detailedVisible)
            Storage.StoreValue(AppSettings.SystemVals.detailedHeight, FileOpDetailedGrid.Height);
    }

    private Rect GetWindowBoundsToStore()
    {
        var bounds = WindowState is WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        var width = Math.Max(MinWidth, bounds.Width);
        var height = Math.Max(MinHeight, bounds.Height);

        return new(bounds.Left, bounds.Top, width, height);
    }

    private static bool TryGetStoredDouble(AppSettings.SystemVals key, out double value)
    {
        value = Storage.RetrieveValue(key) switch
        {
            double doubleValue when double.IsFinite(doubleValue) => doubleValue,
            float floatValue when float.IsFinite(floatValue) => floatValue,
            decimal decimalValue => (double)decimalValue,
            long longValue => longValue,
            int intValue => intValue,
            string stringValue when double.TryParse(stringValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedValue) => parsedValue,
            string stringValue when double.TryParse(stringValue, out var parsedValue) => parsedValue,
            _ => double.NaN,
        };

        return double.IsFinite(value);
    }

    private bool TryRestoreWindowBounds()
    {
        if (!TryGetStoredDouble(AppSettings.SystemVals.windowLeft, out var left)
            || !TryGetStoredDouble(AppSettings.SystemVals.windowTop, out var top)
            || !TryGetStoredDouble(AppSettings.SystemVals.windowWidth, out var width)
            || !TryGetStoredDouble(AppSettings.SystemVals.windowHeight, out var height))
        {
            return false;
        }

        var minWidth = Math.Max(MinWidth, 1);
        var minHeight = Math.Max(MinHeight, 1);
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenWidth = Math.Max(SystemParameters.VirtualScreenWidth, minWidth);
        var screenHeight = Math.Max(SystemParameters.VirtualScreenHeight, minHeight);

        width = Math.Clamp(width, minWidth, screenWidth);
        height = Math.Clamp(height, minHeight, screenHeight);

        var maxLeft = screenLeft + screenWidth - width;
        var maxTop = screenTop + screenHeight - height;

        left = Math.Clamp(left, screenLeft, maxLeft);
        top = Math.Clamp(top, screenTop, maxTop);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;

        return true;
    }

    private void ApplyDefaultLaunchSize()
    {
        var height = Math.Max(MinHeight, SystemParameters.PrimaryScreenHeight * WINDOW_HEIGHT_RATIO);
        Height = height;
        Width = Math.Max(MinWidth, Height / WINDOW_WIDTH_RATIO);
    }

    private void SetTheme() => SetTheme(Settings.Theme);

    private void SetTheme(AppSettings.AppTheme theme) => ThemeService.SetTheme(theme);

    private void IsPathBoxFocused(bool isFocused)
    {
        if (isFocused)
            _focusPathBox();
        else
            _unfocusPathBox();

        void _focusPathBox()
        {
            NavigationBox.Mode = NavigationBox.ViewMode.Path;
        }

        void _unfocusPathBox()
        {
            if (NavigationBox.Mode is NavigationBox.ViewMode.None)
                return;

            NavigationBox.Mode = NavigationBox.ViewMode.Breadcrumbs;
        }
    }

    private void DataGridRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !FileActions.IsAppDrive && SelectedFiles.Count() == 1 && !IsInEditMode)
            DoubleClick(ExplorerGrid.SelectedItem);
    }

    private void DoubleClick(object source)
    {
        if (FileActions.IsRecycleBin)
            return;

        if (source is not FileClass file)
        {
            if (source is Package apk && !FileActions.ListingInProgress)
                FileActionLogic.OpenApkLocation(apk);

            return;
        }

        if (file.Type is FileType.Folder)
        {
            if (!FileActions.ListingInProgress)
            {
                ((App)Application.Current).RequestFileNavigation(file);
            }

            return;
        }
        else if (file.Type is not FileType.File)
            return;

        if (Settings.DoubleClick is AppSettings.DoubleClickAction.pull
            && Settings.IsPullOnDoubleClickEnabled
            && FileActions.PullEnabled)
        {
            FileActionLogic.PullFiles(Settings.DefaultFolder);
        }
        else if (Settings.DoubleClick is AppSettings.DoubleClickAction.edit)
            FileActionLogic.OpenEditor();
    }

    private void ExplorerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressExplorerSelectionChanged)
            return;

        if (ExplorerGrid.SelectedItems.Count > 0 && !RuntimeSettings.IsExplorerLoaded)
        {
            ExplorerGrid.UnselectAll();
            return;
        }

        if (!SelectionHelper.GetSelectionInProgress(ExplorerGrid))
        {
            if (ExplorerGrid.SelectedItems.Count == 1)
            {
                SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, ExplorerGrid.SelectedIndex);
                if (SelectionHelper.GetFirstSelectedIndex(ExplorerGrid) < 0
                    || Keyboard.Modifiers is not ModifierKeys.Control and not ModifierKeys.Shift)
                {
                    SelectionHelper.SetFirstSelectedIndex(ExplorerGrid, ExplorerGrid.SelectedIndex);
                }
            }
            else if (ExplorerGrid.SelectedItems.Count > 1 && e.AddedItems.Count == 1)
            {
                SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, ExplorerGrid.Items.IndexOf(e.AddedItems[0]));
            }
        }

        SelectionTimer.Stop();
        SelectionTimer.Start();
    }

    internal int BeginExplorerSelectionReset()
    {
        var version = unchecked(++explorerSelectionResetVersion);
        suppressExplorerSelectionChanged = true;
        SelectionTimer.Stop();
        SelectionHelper.SetFirstSelectedIndex(ExplorerGrid, -1);
        SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, -1);
        return version;
    }

    internal bool RemoveLastExplorerSelection(int version)
    {
        if (version != explorerSelectionResetVersion || ExplorerGrid.SelectedItems.Count == 0)
            return false;

        ExplorerGrid.SelectedItems.RemoveAt(ExplorerGrid.SelectedItems.Count - 1);
        return true;
    }

    internal bool AddExplorerSelection(int version, object item)
    {
        if (version != explorerSelectionResetVersion
            || item is null
            || !ExplorerGrid.Items.Contains(item))
        {
            return false;
        }

        ExplorerGrid.SelectedItems.Add(item);

        return true;
    }

    internal void EndExplorerSelectionReset(int version, bool synchronizeSelection = false)
    {
        if (version != explorerSelectionResetVersion)
            return;

        suppressExplorerSelectionChanged = false;
        if (synchronizeSelection)
        {
            if (ExplorerGrid.SelectedItems.Count > 0)
            {
                var firstItem = ExplorerGrid.SelectedItems[0];
                var lastItem = ExplorerGrid.SelectedItems[ExplorerGrid.SelectedItems.Count - 1];
                SelectionHelper.SetFirstSelectedIndex(ExplorerGrid, ExplorerGrid.Items.IndexOf(firstItem));
                SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, ExplorerGrid.Items.IndexOf(lastItem));
                ExplorerGrid.ScrollIntoView(firstItem);
            }

            SelectionTimer.Start();
            return;
        }

        SelectedFiles = [];
        SelectedPackages = [];
        OnPropertyChanged(nameof(SelectedFilesTotalSize));
        OnPropertyChanged(nameof(SelectedFilesCount));
        FileActions.SelectedItemsCount = 0;
    }

    private void ExplorerGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is FileClass file)
            file.LoadIconAsync();
    }

    private void ExplorerGrid_UnloadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is FileClass file)
            file.CancelIconLoad();
    }

    private void SelectionTimer_Tick(object sender, EventArgs e)
    {
        SelectedFiles = FileActions.IsAppDrive ? [] : ExplorerGrid.SelectedItems.OfType<FileClass>().ToList();
        SelectedPackages = FileActions.IsAppDrive ? ExplorerGrid.SelectedItems.OfType<Package>().ToList() : [];
        OnPropertyChanged(nameof(SelectedFilesTotalSize));
        OnPropertyChanged(nameof(SelectedFilesCount));
        FileActions.SelectedItemsCount = FileActions.IsAppDrive ? SelectedPackages.Count() : SelectedFiles.Count();

        FileActionLogic.UpdateFileActions();
        PasteGrid.Visibility = Visibility.Visible;

        SelectionTimer.Stop();
    }

    private void ScheduleExplorerContextMenuRefresh()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        UiScheduler.EnqueueLatest(
            "explorer.context-menu.prepare",
            "explorer.context-menu.prepare",
            PrepareExplorerContextMenu);
    }

    private void PrepareExplorerContextMenu()
    {
        ExplorerContextMenu.UpdateSeparators();
        pendingExplorerContextItems = ExplorerContextMenu.List
            .Where(menu => menu.Children is null
                ? menu.Action.Command.IsEnabled
                : menu.Action.Command.IsEnabled
                    && menu.Children.Any(child => child.Action.Command.IsEnabled))
            .ToArray();
        explorerContextVersion++;
        UiScheduler.EnqueueLatest(
            "explorer.context-menu.view",
            "explorer.context-menu.view",
            ApplyNextExplorerContextItem);
    }

    private void ApplyNextExplorerContextItem()
    {
        if (applyingExplorerContextVersion != explorerContextVersion)
        {
            applyingExplorerContextVersion = explorerContextVersion;
            explorerContextIndex = 0;
        }

        var visibleItems = ExplorerContextMenu.VisibleList;
        if (explorerContextIndex < pendingExplorerContextItems.Length)
        {
            var expected = pendingExplorerContextItems[explorerContextIndex];
            if (explorerContextIndex >= visibleItems.Count
                || !ReferenceEquals(visibleItems[explorerContextIndex], expected))
            {
                int existingIndex = visibleItems.IndexOf(expected);
                if (existingIndex < 0)
                    visibleItems.Insert(explorerContextIndex, expected);
                else
                    visibleItems.Move(existingIndex, explorerContextIndex);
            }

            explorerContextIndex++;
        }
        else if (visibleItems.Count > pendingExplorerContextItems.Length)
        {
            visibleItems.RemoveAt(visibleItems.Count - 1);
        }
        else
        {
            return;
        }

        UiScheduler.EnqueueLatest(
            "explorer.context-menu.view",
            "explorer.context-menu.view",
            ApplyNextExplorerContextItem);
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        RuntimeSettings.IsPathBoxFocused = false;
    }

    private void ScheduleFilterDevices()
    {
        if (!RuntimeSettings.IsDevicesPaneOpen)
            return;

        UiScheduler.EnqueueLatest("devices.filter.snapshot", "devices.filter.snapshot", PrepareVisibleDevices);
    }

    private void PrepareVisibleDevices()
    {
        pendingVisibleDevices = DeviceHelper.GetVisibleDevices(DevicesObject.UIList);
        visibleDevicesVersion++;
        UiScheduler.EnqueueLatest("devices.filter", "devices.filter", ApplyNextVisibleDevice);
    }

    private void ApplyNextVisibleDevice()
    {
        if (applyingVisibleDevicesVersion != visibleDevicesVersion)
        {
            applyingVisibleDevicesVersion = visibleDevicesVersion;
            visibleDeviceIndex = 0;
        }

        var visibleDevices = DevicesObject.VisibleDevices;
        if (visibleDeviceIndex < pendingVisibleDevices.Length)
        {
            var expected = pendingVisibleDevices[visibleDeviceIndex];
            if (visibleDeviceIndex >= visibleDevices.Count
                || !ReferenceEquals(visibleDevices[visibleDeviceIndex], expected))
            {
                int existingIndex = visibleDevices.IndexOf(expected);
                if (existingIndex < 0)
                    visibleDevices.Insert(visibleDeviceIndex, expected);
                else
                    visibleDevices.Move(existingIndex, visibleDeviceIndex);
            }

            visibleDeviceIndex++;
        }
        else if (visibleDevices.Count > pendingVisibleDevices.Length)
        {
            visibleDevices.RemoveAt(visibleDevices.Count - 1);
        }
        else
        {
            return;
        }

        UiScheduler.EnqueueLatest("devices.filter", "devices.filter", ApplyNextVisibleDevice);
    }

    private void ScheduleToggleExplorerSelection()
    {
        selectExplorerItems = ExplorerGrid.Items.Count != ExplorerGrid.SelectedItems.Count;
        pendingExplorerSelection = selectExplorerItems
            ? (ExplorerGrid.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().ToArray() ?? []
            : ExplorerGrid.SelectedItems.Cast<object>().ToArray();
        explorerSelectionVersion++;
        UiScheduler.EnqueueLatest("explorer.selection", "explorer.selection", ApplyNextExplorerSelection);
    }

    private void ApplyNextExplorerSelection()
    {
        if (applyingExplorerSelectionVersion != explorerSelectionVersion)
        {
            applyingExplorerSelectionVersion = explorerSelectionVersion;
            explorerSelectionIndex = 0;
        }

        if (explorerSelectionIndex >= pendingExplorerSelection.Length)
            return;

        var item = pendingExplorerSelection[explorerSelectionIndex++];
        if (selectExplorerItems)
            ExplorerGrid.SelectedItems.Add(item);
        else
            ExplorerGrid.SelectedItems.Remove(item);

        UiScheduler.EnqueueLatest("explorer.selection", "explorer.selection", ApplyNextExplorerSelection);
    }

    private void SetRenderMode()
    {
        if (Settings.SwRender)
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        else if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly)
            RenderOptions.ProcessRenderMode = RenderMode.Default;
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (FileActions.ListingInProgress && e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2)
        {
            e.Handled = true;
            return;
        }

        e.Handled = e.ChangedButton switch
        {
            MouseButton.XButton1 => NavHistory.NavigateBF(Navigation.SpecialLocation.Back),
            MouseButton.XButton2 => NavHistory.NavigateBF(Navigation.SpecialLocation.Forward),
            _ => false,
        };
    }

    private void DataGridRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (RuntimeSettings.IsSettingsPaneOpen || RuntimeSettings.IsDevicesPaneOpen)
            return;

        var key = e.Key;
        switch (key)
        {
            case Key.Enter when IsInEditMode:
                return;
            case Key.Enter:
            {
                if (ExplorerGrid.SelectedItems.Count == 1 && ExplorerGrid.SelectedItem is FilePath { IsDirectory: true })
                    DoubleClick(ExplorerGrid.SelectedItem);
                break;
            }
            case Key.Back:
                NavHistory.NavigateBF(Navigation.SpecialLocation.Back);
                break;

            case Key.Delete when FileActions.DeleteEnabled:
                FileActionLogic.DeleteFiles();
                break;

            case Key.Up or Key.Down when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                ExplorerGrid.MultiSelect(key);
                break;

            case Key.Up or Key.Down:
                ExplorerGrid.SingleSelect(key);
                break;

            case Key.F2:
                AppActions.List.First(action => action.Name is FileActionType.Rename).Command.Execute();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void OnButtonKeyDown(object sender, KeyEventArgs e)
    {
        if (RuntimeSettings.IsSettingsPaneOpen || RuntimeSettings.IsDevicesPaneOpen || IsTerminalFocused())
            return;

        if (Keyboard.IsKeyDown(Key.LeftAlt)
            || Keyboard.IsKeyDown(Key.RightAlt)
            || !NAVIGATION_KEYS.Contains(e.Key))
            return;

        bool handle = false;

        if (FileActions.IsExplorerVisible)
        {
            handle |= ExplorerGridKeyNavigation(e.Key);
        }
        else if (FileActions.IsDriveViewVisible)
        {
            handle |= DriveViewKeyNavigation(e.Key);
        }

        e.Handled = handle;
    }

    private bool DriveViewKeyNavigation(Key key)
    {
        if (DriveList.Items.Count == 0 || RuntimeSettings.IsSettingsPaneOpen || RuntimeSettings.IsDevicesPaneOpen)
            return false;

        if (DriveList.SelectedItems.Count == 0)
        {
            switch (key)
            {
                case Key.Left or Key.Up:
                    DriveList.SelectedIndex = DriveList.Items.Count - 1;
                    break;

                case Key.Right or Key.Down:
                    DriveList.SelectedIndex = 0;
                    break;

                default:
                    return false;
            }

            SelectionHelper.GetListViewItemContainer(DriveList).Focus();
            return true;
        }

        switch (key)
        {
            case Key.Enter:
                ((DriveViewModel)DriveList.SelectedItem).BrowseCommand.Execute();
                return true;

            case Key.Escape:
                // Should've been clear selected drives, but causes inconsistent behavior
                return true;

            default:
                return false;
        }
    }

    private bool ExplorerGridKeyNavigation(Key key)
    {
        if (ExplorerGrid.Items.Count < 1 || RuntimeSettings.IsSettingsPaneOpen || RuntimeSettings.IsDevicesPaneOpen)
            return false;

        switch (key)
        {
            case Key.Down or Key.Up or Key.Home or Key.End:
                if (((App)Application.Current).ConsumeBackForwardNavigation())
                {
                    SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, ExplorerGrid.SelectedIndex);
                }

                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    ExplorerGrid.MultiSelect(key);
                else
                    ExplorerGrid.SingleSelect(key);
                break;
            case Key.Enter:
                if (ExplorerGrid.SelectedCells.Count < 1 || IsInEditMode)
                    return false;

                if (ExplorerGrid.SelectedItems.Count == 1 && ((FilePath)ExplorerGrid.SelectedItem).IsDirectory)
                    DoubleClick(ExplorerGrid.SelectedItem);
                break;
            case Key.Apps:
                ExplorerGrid.ContextMenu.IsOpen = true;
                break;
            default:
                return false;
        }

        return true;
    }

    private void DataGridRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not MouseButton.Left)
            return;

        if (e.OriginalSource is Border)
        {
            ClickCount = -1;
            return;
        }

        WasDragging = false;
        var row = sender as DataGridRow;

        CopyPaste.DragStatus = e.OriginalSource is TextBlock or Image || row.IsSelected
            ? CopyPasteService.DragState.Pending
            : CopyPasteService.DragState.None;

        SelectionHelper.SetIndexSingle(ExplorerGrid, row.GetIndex());
    }

    private void ExplorerGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var point = Mouse.GetPosition(ExplorerGrid);
        if (point.Y < ColumnHeaderHeight || WasDragging)
        {
            WasDragging = false;

            SelectionHelper.SetIsMenuOpen(ExplorerGrid.ContextMenu, false);
            e.Handled = true;
            return;
        }

        SelectionHelper.SetIsMenuOpen(ExplorerGrid.ContextMenu, true);
        FileActionLogic.UpdateFileActions();
    }

    private void ExplorerGrid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not MouseButton.Left and not MouseButton.Right)
            return;

        if (RowHeight is null && ExplorerGrid.ItemContainerGenerator.ContainerFromIndex(0) is DataGridRow row)
            RowHeight = row.ActualHeight;

        WasDragging = false;
        CopyPaste.DragStatus = e.OriginalSource is TextBlock or Image
                     ? CopyPasteService.DragState.Pending
                     : CopyPasteService.DragState.None;

        var point = e.GetPosition(ExplorerGrid);
        MouseDownPoint = point;

        int selectionIndex = ExplorerGrid.SelectedIndex;

        var actualRowWidth = ExplorerGrid.Columns
            .Where(col => col.Visibility == Visibility.Visible)
            .Sum(item => item.ActualWidth);

        if (point.Y > (ExplorerGrid.Items.Count * RowHeight + ColumnHeaderHeight)
            || point.Y > (ExplorerGrid.ActualHeight - StyleHelper.FindDescendant<ItemsPresenter>(ExplorerGrid)?.ActualHeight % RowHeight)
            || point.Y < ColumnHeaderHeight + ScrollContentPresenterMargin
            || point.X > actualRowWidth
            || point.X > DataGridContentWidth)
        {
            if (ExplorerGrid.SelectedItems.Count > 0 && IsInEditMode)
                IsInEditMode = false;

            if (Keyboard.Modifiers is not ModifierKeys.Control and not ModifierKeys.Shift)
            {
                ExplorerGrid.UnselectAll();
                selectionIndex = -1;
            }
        }

        SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, selectionIndex);

        if (SelectionHelper.GetFirstSelectedIndex(ExplorerGrid) < 0
            || Keyboard.Modifiers is not ModifierKeys.Control and not ModifierKeys.Shift)
        {
            SelectionHelper.SetFirstSelectedIndex(ExplorerGrid, selectionIndex);
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeDetailedView();
        EnableSplitViewAnimation();

        if (!RuntimeSettings.IsWindowLoaded)
            return;

        SearchBoxMaxWidth();

        Size maximizedSize = new(SystemParameters.MaximizedPrimaryScreenWidth, SystemParameters.MaximizedPrimaryScreenHeight);
        if (e.NewSize != maximizedSize && e.PreviousSize != maximizedSize)
            FileActions.IsEditorOpen = false;
    }

    private void SearchBoxMaxWidth()
    {
        RuntimeSettings.MaxSearchBoxWidth = WindowState switch
        {
            WindowState.Maximized => SystemParameters.PrimaryScreenWidth * MAX_SEARCH_WIDTH_RATIO,
            _ => Width * MAX_SEARCH_WIDTH_RATIO,
        };

        SearchBox.Refresh();
    }

    private void EnableSplitViewAnimation()
    {
        // Read value to force IsAnimated to update
        _ = Settings.DisableAnimation;

        bool enableAnimation = Settings.IsAnimated
            && (NativeMethods.MonitorInfo.IsPrimaryMonitor(this) is true
            || WindowState is not WindowState.Maximized);

        StyleHelper.SetActivateAnimation(SettingsSplitView, enableAnimation);
        StyleHelper.SetActivateAnimation(DevicesSplitView, enableAnimation);
    }

    private void ResizeDetailedView()
    {
        double windowHeight = WindowState == WindowState.Maximized ? ActualHeight : Height;

        if (DetailedViewSize() is var val && val == -1)
        {
            FileOpDetailedGrid.Height = windowHeight * MIN_PANE_HEIGHT_RATIO;
        }
        else if (val == 1)
        {
            FileOpDetailedGrid.Height = windowHeight * MAX_PANE_HEIGHT_RATIO;
        }
    }

    private void FilterDrives()
    {
        var collectionView = CollectionViewSource.GetDefaultView(DriveList.ItemsSource);
        if (collectionView is null)
            return;

        if (collectionView.Filter is not null)
        {
            collectionView.Refresh();
            return;
        }

        Predicate<object> predicate = d =>
        {
            var drive = (DriveViewModel)d;

            return drive.Type switch
            {
                AbstractDrive.DriveType.Trash => Settings.EnableRecycle,
                AbstractDrive.DriveType.Temp or AbstractDrive.DriveType.Package => Settings.EnableApk,
                _ => true,
            };
        };

        collectionView.Filter = predicate;

        if (collectionView.SortDescriptions.All(d => d.PropertyName != nameof(DriveViewModel.Type)))
            collectionView.SortDescriptions.Add(new(nameof(DriveViewModel.Type), ListSortDirection.Ascending));
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollViewer scroller = StyleHelper.FindDescendant<ScrollViewer>((DependencyObject)sender, true);
        if (scroller is null)
            return;

        scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void DataGridCell_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (e.OriginalSource is DataGridCell && e.TargetRect == Rect.Empty)
        {
            e.Handled = true;
        }
    }

    private void GridBackgroundBlock_MouseDown(object sender, MouseButtonEventArgs e)
    {
        RuntimeSettings.IsPathBoxFocused = false;
        DriveHelper.ClearSelectedDrives();
    }

    private void FilterExplorerItems()
    {
        if (!FileActions.IsExplorerVisible)
            return;

        if (FileActions.IsAppDrive)
        {
            ExplorerGrid.Columns[8].SortDirection ??= ListSortDirection.Descending;
            SchedulePackageViewRefresh();
        }
        else
        {
            ExplorerGrid.Columns[1].SortDirection ??= ListSortDirection.Ascending;
            CurrentDirectorySession?.ApplyFilter(Settings.ShowHiddenItems, FileActions.ExplorerFilter);
        }
    }

    private void SchedulePackageViewRefresh()
    {
        var source = App.ExplorerState.Packages;
        string filter = FileActions.ExplorerFilter ?? "";
        bool showSystemPackages = Settings.ShowSystemPackages;
        string sortProperty = packageSortProperty;
        var sortDirection = packageSortDirection;
        long version = Interlocked.Increment(ref packageViewVersion);
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref packageViewCancellation, cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        _ = PreparePackageViewAsync(
            source,
            filter,
            showSystemPackages,
            sortProperty,
            sortDirection,
            version,
            cancellation.Token);
    }

    private async Task PreparePackageViewAsync(
        IReadOnlyCollection<Package> source,
        string filter,
        bool showSystemPackages,
        string sortProperty,
        ListSortDirection sortDirection,
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(
                () => BuildPackageView(
                    source,
                    filter,
                    showSystemPackages,
                    sortProperty,
                    sortDirection,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

            UiScheduler.EnqueueLatest(
                "packages.view.prepare",
                "packages.view.prepare",
                () =>
                {
                    if (version != Volatile.Read(ref packageViewVersion))
                        return;

                    pendingVisiblePackages = result;
                    UiScheduler.EnqueueLatest(
                        "packages.view",
                        "packages.view",
                        ApplyNextVisiblePackage);
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "packages.view");
        }
    }

    internal static Package[] BuildPackageView(
        IReadOnlyCollection<Package> source,
        string filter,
        bool showSystemPackages,
        string sortProperty,
        ListSortDirection sortDirection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packages = source
            .Where(package => showSystemPackages || package.Type is Package.PackageType.User)
            .Where(package => string.IsNullOrEmpty(filter)
                || package.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Array.Sort(packages, (left, right) =>
        {
            int comparison = sortProperty switch
            {
                nameof(Package.Name) => string.Compare(
                    left.Name,
                    right.Name,
                    StringComparison.CurrentCultureIgnoreCase),
                nameof(Package.Uid) => Nullable.Compare(left.Uid, right.Uid),
                nameof(Package.Version) => Nullable.Compare(left.Version, right.Version),
                _ => left.Type.CompareTo(right.Type),
            };
            if (sortDirection is ListSortDirection.Descending)
                comparison = -comparison;
            return comparison != 0
                ? comparison
                : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        cancellationToken.ThrowIfCancellationRequested();
        return packages;
    }

    private void ApplyNextVisiblePackage()
    {
        long version = Volatile.Read(ref packageViewVersion);
        if (applyingPackageViewVersion != version)
        {
            applyingPackageViewVersion = version;
            packageViewIndex = 0;
        }

        var visiblePackages = App.ExplorerState.VisiblePackages;
        if (packageViewIndex < pendingVisiblePackages.Length)
        {
            var expected = pendingVisiblePackages[packageViewIndex];
            if (packageViewIndex >= visiblePackages.Count
                || !ReferenceEquals(visiblePackages[packageViewIndex], expected))
            {
                int existingIndex = visiblePackages.IndexOf(expected);
                if (existingIndex < 0)
                    visiblePackages.Insert(packageViewIndex, expected);
                else
                    visiblePackages.Move(existingIndex, packageViewIndex);
            }

            packageViewIndex++;
        }
        else if (visiblePackages.Count > pendingVisiblePackages.Length)
        {
            visiblePackages.RemoveAt(visiblePackages.Count - 1);
        }
        else
        {
            return;
        }

        UiScheduler.EnqueueLatest(
            "packages.view",
            "packages.view",
            ApplyNextVisiblePackage);
    }

    private void GridSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // -1 + 1 or 1 + -1 (0 + 0 shouldn't happen)
        if (SimplifyNumber(e.VerticalChange) + DetailedViewSize() == 0)
            return;

        if (FileOpDetailedGrid.Height is double.NaN)
            FileOpDetailedGrid.Height = FileOpDetailedGrid.ActualHeight;

        FileOpDetailedGrid.Height -= e.VerticalChange;

        static sbyte SimplifyNumber(double num) => num switch
        {
            < 0 => -1,
            > 0 => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Compares the size of the detailed file op view to its limits
    /// </summary>
    /// <returns>0 if within limits, 1 if exceeds upper limits, -1 if exceeds lower limits</returns>
    private sbyte DetailedViewSize()
    {
        double height = FileOpDetailedGrid.ActualHeight;
        if (height == 0 && FileOpDetailedGrid.Height > 0)
            height = FileOpDetailedGrid.Height;

        if (height > ActualHeight * MAX_PANE_HEIGHT_RATIO)
            return 1;

        if (ActualHeight == 0 || height < ActualHeight * MIN_PANE_HEIGHT_RATIO && height < MIN_PANE_HEIGHT)
            return -1;

        return 0;
    }

    private void FileOpDetailedGrid_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ResizeDetailedView();
    }

    internal void UpdateMdns()
    {
        if (MdnsService.State == MDNS.MdnsState.Disabled)
        {
            MdnsService.State = MDNS.MdnsState.InProgress;
            AdbHelper.MdnsCheck();
        }
        else
        {
            MdnsService.State = MDNS.MdnsState.Disabled;
        }

        if (RuntimeSettings.IsMdnsExpanderOpen)
        {
            UpdateQrClass();
        }
    }

    private void UpdateQrClass() => PairingQrImage.Source = QrClass.Image;

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        if (!HasSavedWindowBounds || !TryRestoreWindowBounds())
            ApplyDefaultLaunchSize();

        if (Storage.RetrieveBool(AppSettings.SystemVals.windowMaximized) == true)
            WindowState = WindowState.Maximized;

        if (Storage.RetrieveBool(AppSettings.SystemVals.detailedVisible) is bool and true)
        {
            RuntimeSettings.IsOperationsViewOpen = true;
        }

        if (double.TryParse(Storage.RetrieveValue(AppSettings.SystemVals.detailedHeight)?.ToString(), out double detailedHeight))
        {
            FileOpDetailedGrid.Height = detailedHeight;
            ResizeDetailedView();
        }
    }

    private async void RestartAdbButton_Click(object sender, RoutedEventArgs e)
    {
        await ((App)Application.Current).RestartAdbServerAsync();
    }

    private void Border_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((MenuItem)(FindResource("DeviceActionsMenu") as Menu).Items[0]).IsSubmenuOpen)
            return;

        DeviceHelper.CollapseDevices();
    }

    private void CurrentOperationDetailedDataGrid_ColumnDisplayIndexChanged(object sender, DataGridColumnEventArgs e)
        => FileOpColumns.UpdateColumnIndexes();

    private void NameColumnEdit_Loaded(object sender, RoutedEventArgs e)
    {
        var textBox = sender as TextBox;
        textBox.Focus();
        
        var editPoint = textBox.TranslatePoint(new(), ExplorerCanvas);
        Canvas.SetTop(RenameTooltip, editPoint.Y - RenameTooltip.ActualHeight - 4);
        Canvas.SetLeft(RenameTooltip, editPoint.X + 4);
        RenameTooltip.Visibility = Visibility.Visible;
    }

    private void NameColumnEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        FileActionLogic.Rename(sender as TextBox);
        RenameTooltip.Visibility = Visibility.Hidden;
    }

    private void NameColumnEdit_KeyDown(object sender, KeyEventArgs e)
    {
        var cell = CellConverter.GetDataGridCell(ExplorerGrid.SelectedCells[1]);
        var textBox = sender as TextBox;

        if (e.Key is Key.Escape or Key.F2)
        {
            var name = FileHelper.DisplayName(textBox);
            if (string.IsNullOrEmpty(name))
            {
                CurrentDirectorySession.RemoveItem(ExplorerGrid.SelectedItem as FileClass);
            }
            else
            {
                textBox.Text = FileHelper.DisplayName(sender as TextBox);
            }

            AppActions.List.First(action => action.Name is FileActionType.Rename).Command.Execute();
        }
        else if (e.Key is not Key.Enter)
            return;

        e.Handled = true;

        if (ExplorerGrid.SelectedCells.Count > 0)
        {
            cell.IsEditing = false;
            FileActions.IsExplorerEditing = false;
        }
    }

    private void DataGridCell_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not MouseButton.Left and not MouseButton.Right)
            return;

        if (e.OriginalSource is Border)
        {
            ClickCount = -1;
            return;
        }
        
        WasDragging = false;
        
        var cell = sender as DataGridCell;
        WasEditing = cell.IsEditing;

        if (WasEditing)
            return;

        var row = DataGridRow.GetRowContainingElement(cell);
        var current = row.GetIndex();

        WasSelected = row.IsSelected;

        if (e.ChangedButton is MouseButton.Right && !WasSelected)
        {
            ExplorerGrid.UnselectAll();
            row.IsSelected = true;
            e.Handled = true;
            return;
        }

        CopyPaste.DragStatus = e.OriginalSource is TextBlock or Image || row.IsSelected
                     ? CopyPasteService.DragState.Pending
                     : CopyPasteService.DragState.None;

        MouseDownPoint = e.GetPosition(ExplorerGrid);
        e.Handled = true;
        ClickCount = e.ClickCount;

        if (ClickCount > 1)
        {
            DoubleClick(cell.DataContext);
            return;
        }

        RuntimeSettings.IsPathBoxFocused = false;

        if (!row.IsSelected
            && CopyPaste.DragStatus is not CopyPasteService.DragState.None
            && Keyboard.Modifiers is not ModifierKeys.Control and not ModifierKeys.Shift)
        {
            ExplorerGrid.UnselectAll();
            row.IsSelected = true;
        }

        SelectionHelper.SetNextSelectedIndex(ExplorerGrid, current);
        SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, current);
        if (ExplorerGrid.SelectedItems.Count < 1)
            SelectionHelper.SetFirstSelectedIndex(ExplorerGrid, current);
    }

    private void NameColumnEdit_TextChanged(object sender, TextChangedEventArgs e)
    {
        var textBox = sender as TextBox;
        var file = (FileClass)textBox.DataContext;
        textBox.FilterString(CurrentDrive.IsFUSE
            ? INVALID_NTFS_CHARS
            : INVALID_UNIX_CHARS);

        FileActions.IsRenameUnixLegal = FileHelper.FileNameLegal(textBox.Text, FileHelper.RenameTarget.Unix);
        FileActions.IsRenameFuseLegal = FileHelper.FileNameLegal(textBox.Text, FileHelper.RenameTarget.FUSE);
        FileActions.IsRenameWindowsLegal = FileHelper.FileNameLegal(textBox.Text, FileHelper.RenameTarget.Windows);
        FileActions.IsRenameDriveRootLegal = FileHelper.FileNameLegal(textBox.Text, FileHelper.RenameTarget.WinRoot);

        var fullName = Settings.ShowExtensions
            ? textBox.Text
            : textBox.Text + file.Extension;

        var comparison = CurrentDrive.IsFUSE
            ? StringComparison.InvariantCultureIgnoreCase
            : StringComparison.InvariantCulture;

        FileActions.IsRenameUnique = !CurrentDirectorySession.FileList.Except([file]).Any(f => f.FullName.Equals(fullName, comparison));
    }

    private void NewItem(bool isFolder)
    {
        var fileName = FileHelper.DuplicateFile(CurrentDirectorySession.FileList, isFolder
            ? Strings.Resources.S_NEW_FOLDER
            : Strings.Resources.S_NEW_ITEM);

        FileClass newItem = new(fileName, FileHelper.ConcatPaths(CurrentPath, fileName), isFolder ? FileType.Folder : FileType.File, isTemp: true);
        CurrentDirectorySession.InsertItem(0, newItem);

        ExplorerGrid.ScrollIntoView(newItem);
        ExplorerGrid.SelectedItem = newItem;

        IsInEditMode = true;
        if (!IsInEditMode) // in case cell was not acquired
            FileActionLogic.CreateNewItem(newItem);
    }

    private void ClearLogs()
    {
        CommandLog.Clear();
        LogTextBox.Clear();
    }

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
        => ((App)Application.Current).RefreshDevices();

    private void AndroidRobotLicense_Click(object sender, RoutedEventArgs e)
        => SettingsHelper.ShowAndroidRobotLicense();

    private void ExplorerGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (FileActions.IsAppDrive)
        {
            packageSortDirection = ListHelper.Invert(e.Column.SortDirection);
            packageSortProperty = ReferenceEquals(e.Column, PackageName)
                ? nameof(Package.Name)
                : ReferenceEquals(e.Column, PackageUid)
                    ? nameof(Package.Uid)
                    : ReferenceEquals(e.Column, PackageVersion)
                        ? nameof(Package.Version)
                        : nameof(Package.Type);
            foreach (var column in new[] { PackageName, PackageType, PackageUid, PackageVersion })
            {
                if (!ReferenceEquals(column, e.Column))
                    column.SortDirection = null;
            }
            e.Column.SortDirection = packageSortDirection;
            SchedulePackageViewRefresh();
            e.Handled = true;
            return;
        }

        var sortDirection = ListHelper.Invert(e.Column.SortDirection);
        e.Column.SortDirection = sortDirection;
        CurrentDirectorySession?.Sort(e.Column.SortMemberPath, sortDirection);

        e.Handled = true;
    }

    private void ExplorerGrid_ContextMenuClosing(object sender, ContextMenuEventArgs e)
    {
        SelectionHelper.SetIsMenuOpen(ExplorerGrid.ContextMenu, false);
    }

    private void FilterSettings()
    {
        var collectionView = CollectionViewSource.GetDefaultView(SortedSettings.ItemsSource);
        if (collectionView is null)
            return;

        if (string.IsNullOrEmpty(RuntimeSettings.SearchText))
            collectionView.Filter = null;
        else
        {
            collectionView.Filter = sett => ((AbstractSetting)sett).Description.Contains(RuntimeSettings.SearchText, StringComparison.OrdinalIgnoreCase)
                                            || (sett is EnumSetting enumSett && enumSett.Buttons.Any(button => button.Name.Contains(RuntimeSettings.SearchText, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void MainWin_StateChanged(object sender, EventArgs e)
    {
        if (RuntimeSettings.IsWindowLoaded)
            SearchBoxMaxWidth();
    }

    private void SortedSettings_MouseMove(object sender, MouseEventArgs e)
    {
        // Prevent focus from being set on the ComboBox when it is open
        // this only works for the first combobox
        // when we'll have more, this will need to be adjusted
        var combobox = StyleHelper.FindDescendant<ComboBox>(SortedSettings);
        if (combobox is not null && 
            (combobox.IsDropDownOpen || e.LeftButton is MouseButtonState.Pressed && combobox.IsMouseOver))
            return;

        SortedSettings.Focus();
    }

    private void MdnsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateMdns();
    }

    private void ExplorerGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (Mouse.LeftButton is MouseButtonState.Released)
            CopyPaste.ClearDrag();

        var point = e.GetPosition(ExplorerCanvas);
        bool withinEditingCell = false;
        DataGridCell cell = ExplorerGrid.SelectedCells.Count > 0
                            ? CellConverter.GetDataGridCell(ExplorerGrid.SelectedCells[1])
                            : null;

        if (IsInEditMode)
        {
            withinEditingCell = VisualTreeHelper.GetDescendantBounds(cell).Contains(e.GetPosition(cell));
        }

        var abortDrag = e.LeftButton == MouseButtonState.Released
            || !RuntimeSettings.IsExplorerLoaded
            || MouseDownPoint == NullPoint
            || withinEditingCell
            || SelectionHelper.GetIsMenuOpen(ExplorerGrid.ContextMenu);

        if (CopyPaste.DragStatus is CopyPasteService.DragState.Pending && (MouseDownPoint - point).LengthSquared >= 25)
        {
            if (ExplorerGrid.SelectedItems.Count > 0
                && ExplorerGrid.SelectedItems[0] is FileClass or Package
                && !abortDrag)
            {
                CopyPaste.DragStatus = CopyPasteService.DragState.Active;
                WasDragging = true;

                IEnumerable<FileClass> selectedItems;
                VirtualFileDataObject vfdo;
                if (FileActions.IsAppDrive)
                {
                    vfdo = VirtualFileDataObject.PrepareTransfer(ExplorerGrid.SelectedItems.Cast<Package>());
                    selectedItems = VirtualFileDataObject.SelfFiles;
                }
                else
                {
                    selectedItems = ExplorerGrid.SelectedItems.Cast<FileClass>();
                    vfdo = VirtualFileDataObject.PrepareTransfer(selectedItems, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
                }

                if (vfdo is not null)
                {
                    CopyPaste.UpdateSelfVFDO(true);
                    var previewItem = selectedItems.First();
                    RuntimeSettings.DragBitmap = previewItem.Icon;
                    LoadDragPreview(previewItem);

                    vfdo.SendObjectToShell(VirtualFileDataObject.DataObjectMethod.DragDrop, cell, vfdo.PreferredDropEffect.Value);
                }
            }
            else
                CopyPaste.DragStatus = CopyPasteService.DragState.None;
        }

        if (abortDrag || CopyPaste.DragStatus is not CopyPasteService.DragState.None || WasDragging)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            return;
        }
        var scroller = StyleHelper.FindDescendant<ScrollViewer>(ExplorerGrid);
        var horizontal = scroller.ComputedHorizontalScrollBarVisibility is Visibility.Visible ? 1 : 0;
        var vertical = scroller.ComputedVerticalScrollBarVisibility is Visibility.Visible ? 1 : 0;

        if (!SelectionRect.IsVisible
            && ((point.Y > ExplorerCanvas.ActualHeight - SystemParameters.HorizontalScrollBarHeight * horizontal)
            || (point.X > ExplorerCanvas.ActualWidth - SystemParameters.VerticalScrollBarWidth * vertical)))
        {
            MouseDownPoint = point;
        }

        if (MouseDownPoint.Y > ExplorerCanvas.ActualHeight - SystemParameters.HorizontalScrollBarHeight * horizontal
            || MouseDownPoint.X > ExplorerCanvas.ActualWidth - SystemParameters.VerticalScrollBarWidth * vertical)
            return;

        SelectionRect.Visibility = Visibility.Visible;
        if (point.Y > MouseDownPoint.Y)
        {
            Canvas.SetTop(SelectionRect, MouseDownPoint.Y);
        }
        else
        {
            Canvas.SetTop(SelectionRect, point.Y);
        }
        if (point.X > MouseDownPoint.X)
        {
            Canvas.SetLeft(SelectionRect, MouseDownPoint.X);
        }
        else
        {
            Canvas.SetLeft(SelectionRect, point.X);
        }

        SelectionRect.Height = Math.Abs(MouseDownPoint.Y - point.Y);
        SelectionRect.Width = Math.Abs(MouseDownPoint.X - point.X);

        SelectRows(point);
    }

    private void SelectRows(Point mousePosition)
    {
        Rect selection = new(Canvas.GetLeft(SelectionRect),
                             Canvas.GetTop(SelectionRect),
                             SelectionRect.Width,
                             SelectionRect.Height);

        for (int i = 0; i < ExplorerGrid.ItemContainerGenerator.Items.Count; i++)
        {
            if (ExplorerGrid.ItemContainerGenerator.ContainerFromIndex(i) is not DataGridRow row)
                continue;

            Rect rowRect = new(row.TranslatePoint(new(), ExplorerGrid), row.DesiredSize);
            row.IsSelected = rowRect.IntersectsWith(selection);

            rowRect.Inflate(double.PositiveInfinity, 0);
            if (rowRect.Contains(mousePosition))
                SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, row.GetIndex());
        }

        if (ExplorerGrid.SelectedItems.Count == 1
            && (SelectionHelper.GetFirstSelectedIndex(ExplorerGrid) < 0
            || Keyboard.Modifiers is not ModifierKeys.Control and not ModifierKeys.Shift))
        {
            SelectionHelper.SetFirstSelectedIndex(ExplorerGrid, ExplorerGrid.SelectedIndex);
        }
    }

    private void ExplorerCanvas_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        SelectionRect.Visibility = Visibility.Collapsed;
        
        if (SelectionHelper.GetFirstSelectedIndex(ExplorerGrid) < 0
            || Keyboard.Modifiers is not ModifierKeys.Control and not ModifierKeys.Shift)
        {
            SelectionHelper.SetFirstSelectedIndex(ExplorerGrid, SelectionHelper.GetNextSelectedIndex(ExplorerGrid));
        }
    }

    private void CurrentOperationDetailedDataGrid_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape)
        {
            ((DataGrid)sender).UnselectAll();
        }
    }

    private void CurrentOperationDetailedDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedFileOp();
    }

    private void UpdateSelectedFileOp()
    {
        FileActions.SelectedFileOps.Value = CurrentOperationDetailedDataGrid.SelectedItems.OfType<FileOperation>();
    }

    private async void KillAdbButton_Click(object sender, RoutedEventArgs e)
    {
        await ((App)Application.Current).KillAdbProcessAsync();
    }

    private async void DisconnectCurrentDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        await DeviceHelper.DisconnectCurrentDeviceAsync();
    }

    private void Grid_MouseEnter(object sender, MouseEventArgs e)
    {
        MouseDownPoint = NullPoint;
    }

    private void DataGridRow_Drop(object sender, DragEventArgs e)
    {
        CopyPaste.AcceptDataObject(e, (FrameworkElement)sender);
        e.Handled = true;
    }

    private void DataGridCell_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not MouseButton.Left || ClickCount < 0)
            return;

        e.Handled = CellMouseUp(sender, e);

        CopyPaste.DragStatus = CopyPasteService.DragState.None;
    }

    private bool CellMouseUp(object sender, MouseButtonEventArgs e)
    {
        DataGridCell cell;
        DataGridRow row;

        if (CopyPaste.DragStatus is CopyPasteService.DragState.Active || WasDragging)
            return false;

        switch (sender)
        {
            case DataGridCell c:
            {
                cell = c;
                row = DataGridRow.GetRowContainingElement(cell);

                if (cell.IsEditing)
                    return false;
                break;
            }
            case DataGridRow r:
                row = r;
                cell = null;
                break;
            default:
                return false;
        }

        var current = row.GetIndex();
        SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, current);

        if (MultiRowSelect(row))
            return true;

        if (SelectionHelper.GetFirstSelectedIndex(ExplorerGrid) < 0
            || Keyboard.Modifiers is not ModifierKeys.Control and not ModifierKeys.Shift)
        {
            SelectionHelper.SetFirstSelectedIndex(ExplorerGrid, current);
        }

        if (!row.IsSelected || ExplorerGrid.SelectedItems?.Count != 1)
        {
            ExplorerGrid.UnselectAll();
            row.IsSelected = true;
            return true;
        }

        if (cell?.Column == NameColumn)
            MouseUpOnName(cell);

        return true;
    }

    private async void MouseUpOnName(DataGridCell cell)
    {
        if (cell.IsReadOnly
            || (DevicesObject.Current.Root is not AbstractDevice.RootStatus.Enabled
                && ((FileClass)cell.DataContext).Type is not (FileType.File or FileType.Folder)))
            return;

        var path = ((FileClass)ExplorerGrid.SelectedItem).FullPath;

        if (ExplorerGrid.SelectedItems.Count == 1 && WasSelected && !WasEditing)
        {
            var start = DateTime.Now;

            while (true)
            {
                await Task.Delay(100);

                if (DateTime.Now - start > RENAME_CLICK_DELAY)
                    break;

                var currentPath = ((FileClass)ExplorerGrid.SelectedItem)?.FullPath;
                if (ClickCount > 1 || currentPath != path)
                    return;
            }

            cell.IsEditing = true;
            FileActions.IsExplorerEditing = true;
        }
    }

    private bool MultiRowSelect(DataGridRow row)
    {
        var current = row.GetIndex();

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ExplorerGrid.UnselectAll();

            var firstSelected = SelectionHelper.GetFirstSelectedIndex(ExplorerGrid);
            int firstUnselected = firstSelected, lastUnselected = current + 1;
            if (current < firstSelected)
            {
                firstUnselected = current;
                lastUnselected = firstSelected + 1;
            }

            for (int i = firstUnselected; i < lastUnselected; i++)
            {
                ExplorerGrid.SelectedItems.Add(ExplorerGrid.Items[i]);
            }

            return true;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            row.IsSelected = !row.IsSelected;
            return true;
        }

        return false;
    }

    private void AppDataHyperlink_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.Now - appDataClick < LINK_CLICK_DELAY)
            return;

        appDataClick = DateTime.Now;
        Process.Start("explorer.exe", AppDataPath);
    }

    private void ExplorerGrid_DragOver(object sender, DragEventArgs e)
    {
        var allowed = CopyPaste.GetAllowedDragEffects(e.Data, (FrameworkElement)sender);

        if (allowed.HasFlag(DragDropEffects.Move) && CopyPaste.IsSelf && !e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) && !e.KeyStates.HasFlag(DragDropKeyStates.AltKey))
        {
            e.Effects = DragDropEffects.Move;
        }
        else if (allowed.HasFlag(DragDropEffects.Move) && e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey))
        {
            e.Effects = DragDropEffects.Move;
        }
        else if (allowed.HasFlag(DragDropEffects.Link) && e.KeyStates.HasFlag(DragDropKeyStates.AltKey))
        {
            e.Effects = DragDropEffects.Link;
        }
        else if (allowed.HasFlag(DragDropEffects.Copy)) // copy is the default and does not require Ctrl to be activated
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
            e.Effects = allowed;

        if ((!allowed.HasFlag(DragDropEffects.Copy) && e.KeyStates.HasFlag(DragDropKeyStates.ControlKey))
            || (!allowed.HasFlag(DragDropEffects.Move) && e.KeyStates.HasFlag(DragDropKeyStates.ShiftKey))
            || (!allowed.HasFlag(DragDropEffects.Link) && e.KeyStates.HasFlag(DragDropKeyStates.AltKey)))
        {
            e.Effects = DragDropEffects.None;
        }

        CopyPaste.DropEffect =
        CopyPaste.CurrentDropEffect = e.Effects;

        var previewItem = RuntimeSettings.DragBitmap is null
            ? CopyPaste.GetPreviewFile()
            : null;
        if (previewItem is not null)
        {
            RuntimeSettings.DragBitmap = previewItem.Icon;
            LoadDragPreview(previewItem);
        }

        e.Handled = true;
    }

    private int GetDragPreviewIconSize()
    {
        double logicalSize = Math.Max(SystemParameters.IconWidth, SystemParameters.IconHeight);
        double dpiScale = RuntimeSettings.DpiScalingFactor > 0
            ? 1d / RuntimeSettings.DpiScalingFactor
            : 1d;

        return (int)Math.Clamp(Math.Round(logicalSize * dpiScale), 32, 64);
    }

    private void LoadDragPreview(FilePath file)
    {
        if (Application.Current is not App app)
            return;

        int version = Interlocked.Increment(ref dragIconVersion);
        string fullPath = file.FullPath;
        _ = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                var icon = await app.GetPreviewIconAsync(
                    file.FullName,
                    file.SpecialType,
                    GetDragPreviewIconSize()).ConfigureAwait(false);
                if (icon is null)
                    return;

                app.EnqueueUiLatest("drag.preview-icon", "drag.preview-icon", () =>
                {
                    if (version == Volatile.Read(ref dragIconVersion)
                        && CopyPaste.IsDrag
                        && CopyPaste.GetPreviewFile()?.FullPath == fullPath)
                    {
                        RuntimeSettings.DragBitmap = icon;
                    }
                });
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            { }
            catch (Exception ex)
            {
                App.ReportBackgroundFailure(ex, "drag.preview-icon");
            }
        }
    }

    private void MainWin_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CopyPaste.IsDrag && CopyPaste.DragStatus is not CopyPasteService.DragState.Active && e.Key is Key.Escape)
            RuntimeSettings.DragBitmap = null;
    }

    private void MainWindow_OnPreviewQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed)
            RuntimeSettings.DragBitmap = null;
    }

    private void MainWindow_OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.System && CopyPaste.IsDrag)
            e.Handled = true;
    }

    private void SponsorButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(RuntimeSettings.DefaultBrowserPath, $"\"{Links.SPONSOR}\"");
    }

    private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ComboBox comboBox && !comboBox.IsDropDownOpen)
        {
            e.Handled = true;

            // Re-raise the event to scroll the parent ScrollViewer
            var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = sender
            };

            var parent = VisualTreeHelper.GetParent(comboBox) as UIElement;
            parent?.RaiseEvent(eventArg);
        }
    }

    private void EmptyNonRootTextBlock_Loaded(object sender, RoutedEventArgs e)
    {
        var textBlock = sender as TextBlock;
        var altText = TextHelper.GetAltText(textBlock);

        string xamlString = $"<Span xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xml:space=\"preserve\"{altText[5..]}";

        Span parsedSpan;
        using (StringReader stringReader = new(xamlString))
        using (XmlReader xmlReader = XmlReader.Create(stringReader))
        {
            parsedSpan = (Span)XamlReader.Load(xmlReader);
        }

        textBlock.Inlines.Clear();
        textBlock.Inlines.Add(parsedSpan);
    }

    private void MenuItem_MouseEnter(object sender, MouseEventArgs e)
    {
        var menuitem = sender as MenuItem;

        FlyoutBase.ShowAttachedFlyout(menuitem);
    }

    private static string CurrentTerminalDeviceId => App.ActiveDevices.Current?.Status is AbstractDevice.DeviceStatus.Ok
        ? App.ActiveDevices.Current.ID
        : "";

    private bool IsTerminalFocused() => TerminalControl.IsTerminalFocused;
}
