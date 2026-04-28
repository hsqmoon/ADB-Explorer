using ADB_Explorer.Controls;
using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Resources;
using ADB_Explorer.Services;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;
using Microsoft.Web.WebView2.Core;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Xml;
using static ADB_Explorer.Helpers.VisibilityHelper;
using static ADB_Explorer.Models.AbstractFile;
using static ADB_Explorer.Models.AdbExplorerConst;
using static ADB_Explorer.Models.Data;
using static ADB_Explorer.Services.FileAction;

namespace ADB_Explorer;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int MAX_LOG_TEXT_LENGTH = 200_000;
    private const int MAX_PENDING_TERMINAL_CHUNKS = 512;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_CONTROL = 0x11;

    private readonly DispatcherTimer ServerWatchdogTimer = new() { Interval = RESPONSE_TIMER_INTERVAL };
    private readonly DispatcherTimer ConnectTimer = new() { Interval = CONNECT_TIMER_INIT };
    private readonly DispatcherTimer SelectionTimer = new() { Interval = SELECTION_CHANGED_DELAY };
    private readonly DispatcherTimer DiskUsageTimer = new() { Interval = DISK_USAGE_INTERVAL_ACTIVE };

    private readonly SemaphoreSlim DiskUsageMutex = new(1, 1);
    private readonly SemaphoreSlim DeviceRefreshMutex = new(1, 1);
    private readonly SemaphoreSlim ConnectTimerMutex = new(1, 1);
    private readonly ThemeService ThemeService = new();

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

    private bool isTerminalSearchVisible;
    public bool IsTerminalSearchVisible
    {
        get => isTerminalSearchVisible;
        private set
        {
            if (isTerminalSearchVisible == value)
                return;

            isTerminalSearchVisible = value;
            OnPropertyChanged(nameof(IsTerminalSearchVisible));
        }
    }

    private string terminalSearchQuery = "";
    public string TerminalSearchQuery
    {
        get => terminalSearchQuery;
        set
        {
            value ??= "";
            if (terminalSearchQuery == value)
                return;

            terminalSearchQuery = value;
            OnPropertyChanged(nameof(TerminalSearchQuery));
            PostTerminalSearchQuery();
        }
    }

    private string terminalSearchCountText = "0/0";
    public string TerminalSearchCountText
    {
        get => terminalSearchCountText;
        private set
        {
            if (terminalSearchCountText == value)
                return;

            terminalSearchCountText = value;
            OnPropertyChanged(nameof(TerminalSearchCountText));
        }
    }

    private string prevPath = "";

    /// <summary>
    /// Back / Forward Navigation
    /// </summary>
    private bool bfNavigation;

    private int ClickCount = 0;
    private bool WasSelected;
    private bool WasEditing;
    private bool WasDragging;
    private Point MouseDownPoint;
    private FileToIconConverter FileToIcon;
    private DateTime appDataClick;
    private readonly DragWindow dw = new();
    private readonly Queue<Log> pendingLogEntries = new();
    private readonly Queue<string> pendingTerminalChunks = new();
    private readonly HashSet<Key> activeTerminalShortcutKeys = [];
    private readonly object pendingLogEntriesLock = new();
    private const string TERMINAL_HOST_NAME = "terminal.adb-explorer.invalid";
    private bool isTerminalWebViewInitialized;
    private bool isTerminalPageReady;
    private bool isTerminalFocused;
    private bool isDeviceFilterRefreshScheduled;
    private bool isExplorerContextMenuRefreshScheduled;
    private bool isFileOpControlsRefreshScheduled;
    private bool isLogRefreshScheduled;
    private bool isMainToolBarRefreshScheduled;
    private bool rebuildLogRequested;
    private bool isSettingsControlsRefreshScheduled;
    private bool terminalShortcutArmed;
    private string terminalSelectionCache = "";
    private string TerminalHostFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
    private string TerminalHostPath => Path.Combine(TerminalHostFolder, "index.html");
    private Uri TerminalHostUri => new($"https://{TERMINAL_HOST_NAME}/index.html");
    private readonly LowLevelKeyboardProc keyboardHookProc;
    private IntPtr keyboardHookHandle;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

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
    {
        InitializeComponent();
        keyboardHookProc = KeyboardHookCallback;

        KeyDown += new KeyEventHandler(OnButtonKeyDown);
        PreviewTextInput += new TextCompositionEventHandler(MainWindow_PreviewTextInput);
        Terminal.OutputChunkReceived += Terminal_OutputChunkReceived;
        Terminal.Cleared += Terminal_Cleared;
        TerminalWebView.GotFocus += TerminalWebView_GotFocus;
        TerminalWebView.LostFocus += TerminalWebView_LostFocus;
        TerminalWebView.PreviewMouseDown += TerminalWebView_PreviewMouseDown;
        TerminalWebView.PreviewMouseUp += TerminalWebView_PreviewMouseUp;
        TerminalWebView.NavigationCompleted += TerminalWebView_NavigationCompleted;
        TerminalWebView.PreviewKeyDown += TerminalWebView_PreviewKeyDown;

        DevicesObject = new();
        DevicesList.ItemsSource = DevicesObject.UIList;

        FileOpQ = new();
        Task launchTask = Task.Run(LaunchSequence);

        ConnectTimer.Tick += ConnectTimer_Tick;
        ServerWatchdogTimer.Tick += ServerWatchdogTimer_Tick;
        DiskUsageTimer.Tick += DiskUsageTimer_Tick;
        SelectionTimer.Tick += SelectionTimer_Tick;

        Settings.PropertyChanged += Settings_PropertyChanged;
        RuntimeSettings.PropertyChanged += RuntimeSettings_PropertyChanged;
        ThemeService.PropertyChanged += ThemeService_PropertyChanged;
        CommandLog.CollectionChanged += CommandLog_CollectionChanged;
        FileOpQ.PropertyChanged += FileOperationQueue_PropertyChanged;
        FileActions.PropertyChanged += FileActions_PropertyChanged;
        DevicesObject.PropertyChanged += DevicesObject_PropertyChanged;
        DevicesObject.UIList.CollectionChanged += UIList_CollectionChanged;
        FileOpFilters.CheckedFilterCount.PropertyChanged += CheckedFilterCount_PropertyChanged;

        AppActions.Bindings.ForEach(binding =>
        {
            InputBindings.Add(binding);
            ExplorerGrid.InputBindings.Add(binding);
        });

        UpperProgressBar.DataContext = FileOpQ;
        CurrentOperationDetailedDataGrid.ItemsSource = FileOpQ.Operations;
        ((DataGrid)FindResource("CurrentOperationDataGrid")).ItemsSource = FileOpQ.Operations;
        UpdateFileOp();

        NativeMethods.InterceptClipboard.Init(this, CopyPaste.GetClipboardPasteItems, IpcService.AcceptIpcMessage);

#if DEBUG
        DeviceHelper.TestDevices();
#endif

        Task.Run(() =>
        {
            SetTheme(AppSettings.AppTheme.light);
            SettingsHelper.SplashScreenTask();

            launchTask.Wait();
            RuntimeSettings.IsWindowLoaded = true;

            Dispatcher.Invoke(() =>
            {
                dw.Show();

                App.Current.MainWindow = this;
            });
        });
    }

    private void FinalizeSplash()
    {
        SetTheme();

        RuntimeSettings.IsSplashScreenVisible = false;
        RuntimeSettings.IsDevicesPaneOpen = true;

        ConnectTimer.Start();
        ServerWatchdogTimer.Start();
        DiskUsageTimer.Start();

        SettingsHelper.InitNotifications();
    }

    private void DiskUsageTimer_Tick(object sender, EventArgs e)
    {
        if (!DiskUsageMutex.Wait(0))
            return;

        Task.Run(DiskUsageHelper.GetAdbDiskUsage).ContinueWith(t =>
        {
            DiskUsageMutex.Release();
        });

        DiskUsageTimer.Interval = FileOpQ.IsActive
            ? DISK_USAGE_INTERVAL_ACTIVE
            : DISK_USAGE_INTERVAL_IDLE;
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
                    FileActions.ItemToSelect = item;
                    break;
                }
                else 
                    altItem ??= item;
            }
        }

        if (selectedIndex == ExplorerGrid.SelectedIndex && altItem is not null)
            FileActions.ItemToSelect = altItem;
    }

    private void CheckedFilterCount_PropertyChanged(object sender, PropertyChangedEventArgs<int> e)
    {
        UpdateFileOpFilterCheck();

        SortFileOps();
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

    private void DevicesObject_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DevicesObject.UIList))
            ScheduleFilterDevices();
    }

    private void ServerWatchdogTimer_Tick(object sender, EventArgs e)
    {
        RuntimeSettings.LastServerResponse = RuntimeSettings.LastServerResponse;

        if (Settings.PollDevices
            && MdnsService?.State is MDNS.MdnsState.Running
            && DateTime.Now.Subtract(RuntimeSettings.LastServerResponse) > MDNS_FORCE_CONNECT_TIME)
        {
            MdnsService.State = MDNS.MdnsState.Disabled;
            UpdateMdns();
        }
    }

    private void RuntimeSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        void HandlePropertyChange()
        {
            switch (e.PropertyName)
            {
                case nameof(AppRuntimeSettings.IsMdnsExpanderOpen):
                    if (MdnsService?.State is not MDNS.MdnsState.Disabled)
                    {
                        DeviceHelper.CollapseDevices();

                        if (RuntimeSettings.IsMdnsExpanderOpen)
                            UpdateQrClass();
                    }
                    break;

                case nameof(AppRuntimeSettings.SearchText):
                    FilterSettings();
                    break;

                case nameof(AppRuntimeSettings.BrowseDrive) when RuntimeSettings.BrowseDrive:
                    InitNavigation(RuntimeSettings.BrowseDrive.Path);
                    break;

                case nameof(AppRuntimeSettings.PathBoxNavigation):
                    if (RuntimeSettings.PathBoxNavigation == "-")
                    {
                        bfNavigation = true;
                        NavigateToLocation(NavHistory.GoBack());
                    }
                    else
                    {
                        if (FileActions.IsExplorerVisible)
                            NavigateToLocation(new(RuntimeSettings.PathBoxNavigation));
                        else
                        {
                            if (!InitNavigation(RuntimeSettings.PathBoxNavigation))
                            {
                                DriveViewNav();
                            }
                        }
                    }
                    break;

                case nameof(AppRuntimeSettings.LocationToNavigate):
                    if (RuntimeSettings.LocationToNavigate is null)
                        return;

                    switch (RuntimeSettings.LocationToNavigate.Location)
                    {
                        case Navigation.SpecialLocation.Back:
                            bfNavigation = true;
                            NavigateToLocation(NavHistory.GoBack());
                            break;
                        case Navigation.SpecialLocation.Forward:
                            bfNavigation = true;
                            NavigateToLocation(NavHistory.GoForward());
                            break;
                        case Navigation.SpecialLocation.Up:
                            bfNavigation = false;
                            NavigateToPath(ParentPath);
                            break;
                        default:
                            bfNavigation = false;
                            if (FileActions.IsDriveViewVisible && RuntimeSettings.LocationToNavigate.Location is Navigation.SpecialLocation.DriveView)
                                FileActionLogic.RefreshDrives(true);
                            else
                                NavigateToLocation(RuntimeSettings.LocationToNavigate);
                            break;
                    }
                    break;

                case nameof(AppRuntimeSettings.NewFolder):
                    NewItem(true);
                    break;

                case nameof(AppRuntimeSettings.NewFile):
                    NewItem(false);
                    break;

                case nameof(AppRuntimeSettings.Rename):
                    IsInEditMode ^= true;
                    break;

                case nameof(AppRuntimeSettings.SelectAll):
                    if (ExplorerGrid.Items.Count == ExplorerGrid.SelectedItems.Count)
                        ExplorerGrid.UnselectAll();
                    else
                        ExplorerGrid.SelectAll();
                    break;

                case nameof(AppRuntimeSettings.Refresh):
                    RefreshLocation();
                    break;

                case nameof(AppRuntimeSettings.IsPathBoxFocused):
                    IsPathBoxFocused(RuntimeSettings.IsPathBoxFocused
                                     ?? NavigationBox.Mode
                                     is NavigationBox.ViewMode.Breadcrumbs);

                    RuntimeSettings.AutoHideSearchBox = true;
                    break;

                case nameof(AppRuntimeSettings.IsSearchBoxFocused):
                    if (!RuntimeSettings.IsSearchBoxFocused)
                        SettingsSplitView.Focus();
                    break;

                case nameof(AppRuntimeSettings.IsTerminalOpen):
                    if (RuntimeSettings.IsTerminalOpen)
                    {
                        Terminal.EnableMainDeviceFollow();
                        _ = EnsureTerminalWebViewReadyAsync();
                        _ = Terminal.EnsureConnectedToCurrentDeviceAsync();
                        _ = Dispatcher.BeginInvoke(FocusTerminalInput);
                    }
                    else
                    {
                        CloseTerminalSearch(clearQuery: false, focusTerminal: false);
                    }
                    break;

                case nameof(AppRuntimeSettings.CurrentDevice):
                    if (Terminal.IsFollowingMainDevice)
                        _ = Terminal.EnsureConnectedToCurrentDeviceAsync();
                    break;

                case nameof(AppRuntimeSettings.AutoHideSearchBox):
                    if (Width < MAX_WINDOW_WIDTH_FOR_SEARCH_AUTO_COLLAPSE)
                        RuntimeSettings.IsSearchBoxFocused = false;

                    if (NavigationBox.Mode is not NavigationBox.ViewMode.Path)
                        SettingsSplitView.Focus();
                    break;

                case nameof(AppRuntimeSettings.ExplorerSource):
                    ExplorerGrid.ItemsSource = RuntimeSettings.ExplorerSource;
                    FilterExplorerItems();
                    break;

                case nameof(AppRuntimeSettings.FilterDrives):
                    FilterDrives();
                    break;

                case nameof(AppRuntimeSettings.FilterDevices):
                    ScheduleFilterDevices();
                    break;

                case nameof(AppRuntimeSettings.FilterActions):
                    if (FileActions.IsAppDrive || FileActions.IsRecycleBin || DevicesObject.Current is null)
                        FilterFileActions();
                    ScheduleExplorerContextMenuRefresh();
                    break;

                case nameof(AppRuntimeSettings.ClearNavBox):
                    ClearNavBox();
                    break;

                case nameof(AppRuntimeSettings.InitLister):
                    InitLister();
                    break;

                case nameof(AppRuntimeSettings.DriveViewNav):
                    DriveViewNav();
                    break;

                case nameof(AppRuntimeSettings.GroupsExpanded):
                    SettingsAboutExpander.IsExpanded = RuntimeSettings.GroupsExpanded;
                    break;

                case nameof(AppRuntimeSettings.RefreshFileOpControls):
                    ScheduleFileOpControlsRefresh();
                    break;

                case nameof(AppRuntimeSettings.ClearLogs):
                    ClearLogs();
                    break;

                case nameof(AppRuntimeSettings.RefreshSettingsControls):
                    ScheduleSettingsControlsRefresh();
                    break;

                case nameof(AppRuntimeSettings.SortFileOps):
                    SortFileOps();
                    break;

                case nameof(AppRuntimeSettings.RefreshExplorerSorting):
                    FilterExplorerItems(true);
                    break;

                case nameof(AppRuntimeSettings.FinalizeSplash):
                    FinalizeSplash();
                    break;

                case nameof(AppRuntimeSettings.MainCursor):
                    Cursor = RuntimeSettings.MainCursor;
                    break;

                case nameof(AppRuntimeSettings.RefreshBreadcrumbs):
                    NavigationBox.Refresh();
                    break;
            }
        }

        if (Dispatcher.CheckAccess())
            HandlePropertyChange();
        else
            _ = Dispatcher.BeginInvoke((Action)HandlePropertyChange, DispatcherPriority.Background);
    }

    private void ClearNavBox()
    {
        NavigationBox.Path = null;
        NavigationBox.Mode = NavigationBox.ViewMode.None;
    }

    private void ScheduleFileOpControlsRefresh()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ScheduleFileOpControlsRefresh), DispatcherPriority.Background);
            return;
        }

        if (isFileOpControlsRefreshScheduled)
            return;

        isFileOpControlsRefreshScheduled = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                FileOpControlsMenu.Items.Refresh();
            }
            finally
            {
                isFileOpControlsRefreshScheduled = false;
            }
        }), DispatcherPriority.Background);
    }

    private void ScheduleSettingsControlsRefresh()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ScheduleSettingsControlsRefresh), DispatcherPriority.Background);
            return;
        }

        if (isSettingsControlsRefreshScheduled)
            return;

        isSettingsControlsRefreshScheduled = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                SettingsControlsMenu.Items.Refresh();
            }
            finally
            {
                isSettingsControlsRefreshScheduled = false;
            }
        }), DispatcherPriority.Background);
    }

    private void ScheduleMainToolBarRefresh()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ScheduleMainToolBarRefresh), DispatcherPriority.Background);
            return;
        }

        if (isMainToolBarRefreshScheduled)
            return;

        isMainToolBarRefreshScheduled = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                MainToolBar.Items?.Refresh();
            }
            finally
            {
                isMainToolBarRefreshScheduled = false;
            }
        }), DispatcherPriority.Background);
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
            case nameof(FileActionsEnable.RefreshPackages) when FileActions.RefreshPackages:
                if (FileActions.IsAppDrive)
                {
                    if (Dispatcher.CheckAccess())
                        _navigateToPath(CurrentPath);
                    else
                        _ = Dispatcher.BeginInvoke(new Action(() => _navigateToPath(CurrentPath)));

                    FileActions.RefreshPackages = false;
                }

                break;

            case nameof(FileActionsEnable.ExplorerFilter):
                FilterExplorerItems();

                break;

            case nameof(FileActionsEnable.ItemToSelect):
                if (FileActions.ItemToSelect is null)
                    ExplorerGrid.UnselectAll();
                else
                {
                    ExplorerGrid.ScrollIntoView(FileActions.ItemToSelect);
                    ExplorerGrid.SelectedItem = FileActions.ItemToSelect;
                }

                break;

            case nameof(FileActionsEnable.PasteEnabled):
                FilterFileActions();
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

                RuntimeSettings.RefreshFileOpControls = true;
            }
        }

        if (e.PropertyName is nameof(FileOperationQueue.CurrentChanged))
        {
            SortFileOps();
            FileActionLogic.UpdateFileOpControls();
        }

        if (!FileOpQ.IsActive)
            UpdateSelectedFileOp();
    }

    private void SortFileOps()
    {
        var collectionView = CollectionViewSource.GetDefaultView(CurrentOperationDetailedDataGrid.ItemsSource);
        if (collectionView is null)
            return;

        if (collectionView.Filter is not null)
        {
            collectionView.Refresh();
            return;
        }

        Predicate<object> predicate = op =>
        {
            var fileOp = (FileOperation)op;

            if (FileOpQ.IsActive)
                return fileOp.Filter is FileOpFilter.FilterType.Running;

            return FileOpFilters.List.Find(f => f.Type == fileOp.Filter).IsChecked is true;
        };

        collectionView.Filter = predicate;

        if (collectionView.SortDescriptions.All(d => d.PropertyName != nameof(FileOperation.Filter)))
            collectionView.SortDescriptions.Add(new(nameof(FileOperation.Filter), ListSortDirection.Ascending));
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
            else
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

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ScheduleLogUiUpdate), DispatcherPriority.Background);
            return;
        }

        if (isLogRefreshScheduled)
            return;

        isLogRefreshScheduled = true;
        _ = Dispatcher.BeginInvoke(new Action(ProcessPendingLogUiUpdates), DispatcherPriority.Background);
    }

    private void ProcessPendingLogUiUpdates()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        Queue<Log> logBatch = new();
        bool shouldRebuild;

        lock (pendingLogEntriesLock)
        {
            while (pendingLogEntries.Count > 0)
            {
                logBatch.Enqueue(pendingLogEntries.Dequeue());
            }

            shouldRebuild = rebuildLogRequested;
            rebuildLogRequested = false;
            isLogRefreshScheduled = false;
        }

        LogControlsPanel.Items.Refresh();

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
            shouldScheduleAnotherPass = !isLogRefreshScheduled && (rebuildLogRequested || pendingLogEntries.Count > 0);
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
            case nameof(AppSettings.ShowSystemPackages):
                if (DevicesObject.Current is null)
                    return;

                if (FileActions.IsExplorerVisible)
                    FilterExplorerItems();
                else
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
                    FileActionLogic.RefreshDrives(true);

                FilterDrives();

                break;
            case nameof(AppSettings.SaveDevices):
                if (Settings.SaveDevices && !DevicesObject.HistoryDeviceViewModels.Any())
                    DevicesObject.RetrieveHistoryDevices();

                ScheduleFilterDevices();
                break;
        }
    }

    private void DirectoryLister_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        void HandlePropertyChange()
        {
            switch (e.PropertyName)
            {
                case nameof(DirectoryLister.IsProgressVisible):
                    UnfinishedBlock.Visible(DirList.IsProgressVisible);
                    NavigationBox.IsLoadingProgressVisible = DirList.IsProgressVisible;
                    break;

                case nameof(DirectoryLister.InProgress):
                {
                    bool inProgress = DirList.InProgress;
                    _ = Task.Run(async () =>
                    {
                        if (!inProgress)
                            await Task.Delay(EMPTY_FOLDER_NOTICE_DELAY);

                        _ = Dispatcher.BeginInvoke(new Action(() => FileActions.ListingInProgress = inProgress));
                    });

                    if (inProgress)
                        return;

                    if (FileActions.IsRecycleBin)
                    {
                        TrashHelper.EnableRecycleButtons();
                    }

                    break;
                }
                case nameof(DirectoryLister.IsLinkListingFinished) when ExplorerGrid.Items.Count < 1 || !DirList.IsLinkListingFinished:
                    return;

                case nameof(DirectoryLister.IsLinkListingFinished) when bfNavigation
                    && !string.IsNullOrEmpty(prevPath) && DirList.FileList.FirstOrDefault(item => item.FullPath == prevPath) is var prevItem and not null:
                    FileActions.ItemToSelect = prevItem;
                    break;

                case nameof(DirectoryLister.IsLinkListingFinished):
                {
                    if (ExplorerGrid.Items.Count > 0)
                        ExplorerGrid.ScrollIntoView(ExplorerGrid.Items[0]);
                    break;
                }
            }
        }

        if (Dispatcher.CheckAccess())
            HandlePropertyChange();
        else
            _ = Dispatcher.BeginInvoke((Action)HandlePropertyChange, DispatcherPriority.Background);
    }

    private void ThemeService_PropertyChanged(object sender, PropertyChangedEventArgs e) =>
        Dispatcher.Invoke(SetTheme);

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_ThreadPreprocessMessage;
        RemoveKeyboardHook();
        DirList?.Stop();
        Terminal.Shutdown();

        dw.Close();
        NativeMethods.InterceptClipboard.Close();

        ConnectTimer.Stop();
        ServerWatchdogTimer.Stop();
        DiskUsageTimer.Stop();
        StoreClosingValues();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        LogTerminalUiDiagnostic("window.activated", $"armed={terminalShortcutArmed}; focused={IsTerminalFocused()}");
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        terminalShortcutArmed = false;
        activeTerminalShortcutKeys.Clear();
        LogTerminalUiDiagnostic("window.deactivated", $"armed={terminalShortcutArmed}");
    }

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

            NavigationBox.UnfocusTarget = SettingsSplitView;
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
                bfNavigation = false;
                NavigateToPath(file);
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

    private void SelectionTimer_Tick(object sender, EventArgs e)
    {
        SelectedFiles = FileActions.IsAppDrive ? [] : ExplorerGrid.SelectedItems.OfType<FileClass>();
        SelectedPackages = FileActions.IsAppDrive ? ExplorerGrid.SelectedItems.OfType<Package>() : [];
        OnPropertyChanged(nameof(SelectedFilesTotalSize));
        OnPropertyChanged(nameof(SelectedFilesCount));
        FileActions.SelectedItemsCount = FileActions.IsAppDrive ? SelectedPackages.Count() : SelectedFiles.Count();

        FileActionLogic.UpdateFileActions();
        ScheduleMainToolBarRefresh();
        PasteGrid.Visibility = Visibility.Visible;

        SelectionTimer.Stop();
    }

    /// <summary>
    /// Refresh file actions menu to update their ToolTips
    /// </summary>
    private void FilterFileActions() => ScheduleMainToolBarRefresh();

    private void ScheduleExplorerContextMenuRefresh()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ScheduleExplorerContextMenuRefresh), DispatcherPriority.Background);
            return;
        }

        if (isExplorerContextMenuRefreshScheduled)
            return;

        isExplorerContextMenuRefreshScheduled = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                FilterExplorerContextMenu();
                ExplorerContextMenu.UpdateSeparators();
            }
            finally
            {
                isExplorerContextMenuRefreshScheduled = false;
            }
        }), DispatcherPriority.Background);
    }

    private void FilterExplorerContextMenu()
    {
        var collectionView = CollectionViewSource.GetDefaultView(ExplorerGrid.ContextMenu.ItemsSource);
        if (collectionView is null)
            return;

        Predicate<object> predicate = m =>
        {
            var menu = m as SubMenu;

            if (menu.Children is null)
                return menu.Action.Command.IsEnabled;
            else
                return menu.Action.Command.IsEnabled && menu.Children.Any(child => child.Action.Command.IsEnabled);
        };

        collectionView.Filter = predicate;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        RuntimeSettings.IsPathBoxFocused = false;
    }

    private void LaunchSequence()
    {
        if (!HasSavedWindowBounds)
            _ = Dispatcher.BeginInvoke(ApplyDefaultLaunchSize);

        LoadSettings();
        InitFileOpColumns();

        DeviceHelper.UpdateWsaPkgStatus();

        UpdateFileOpFilterCheck();

        FileToIcon = new();
    }

    private void ScheduleFilterDevices()
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ScheduleFilterDevices), DispatcherPriority.Background);
            return;
        }

        if (isDeviceFilterRefreshScheduled)
            return;

        isDeviceFilterRefreshScheduled = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            isDeviceFilterRefreshScheduled = false;
            FilterDevices();
        }), DispatcherPriority.Background);
    }

    private void FilterDevices() => DeviceHelper.FilterDevices(CollectionViewSource.GetDefaultView(DevicesList.ItemsSource));

    private void InitLister()
    {
        DirList = new(Dispatcher, CurrentADBDevice, FileHelper.ListerFileManipulator);
        DirList.PropertyChanged += DirectoryLister_PropertyChanged;
    }

    private void LoadSettings()
    {
        Dispatcher.Invoke(SettingsHelper.SetSymbolFont);

        SetRenderMode();

        AdbHelper.EnableMdns();

        UISettings.Init();

        Dispatcher.Invoke(() =>
        {
            SettingsList.ItemsSource = UISettings.GroupedSettings;
            SortedSettings.ItemsSource = UISettings.SortSettings;
            NotificationsList.ItemsSource = UISettings.Notifications;

            NavigationToolBar.ItemsSource = Services.NavigationToolBar.List;
            MainToolBar.ItemsSource = Services.MainToolBar.List;
            FileActionLogic.UpdateFileActions();

            ScheduleFilterDevices();

            FileActions.IsLogToggleVisible.Value = Settings.EnableLog;
        });

        Settings.UnrootOnDisconnect ??= false;

        RuntimeSettings.DefaultBrowserPath = Network.GetDefaultBrowser();
    }

    private void SetRenderMode() => Dispatcher.Invoke(() =>
    {
        if (Settings.SwRender)
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        else if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly)
            RenderOptions.ProcessRenderMode = RenderMode.Default;
    });

    private void InitFileOpColumns() => Dispatcher.Invoke(() =>
    {
        FileOpColumns.Init();

        FileOpColumns.List.ForEach(c => CurrentOperationDetailedDataGrid.Columns.Add(c.Column));
    });

    private void RefreshLocation()
    {
        if (FileActions.IsDriveViewVisible)
            FileActionLogic.RefreshDrives(true);
        else
            _navigateToPath(CurrentPath);
    }

    private void DriveViewNav()
    {
        FileActionLogic.ClearExplorer(false);
        FileActions.IsDriveViewVisible = true;
        FileActions.IsExplorerVisible = false;
        UpdateFileOp();

        NavigationBox.Mode = NavigationBox.ViewMode.Breadcrumbs;
        NavigationBox.Path = AdbLocation.StringFromLocation(Navigation.SpecialLocation.DriveView);
        NavHistory.Navigate(Navigation.SpecialLocation.DriveView);
        Settings.LastDevicePath = "";
        Settings.SetLastDevicePath(DevicesObject.Current?.ID, null);

        DriveList.ItemsSource = DevicesObject.Current.Drives;
        CurrentDrive = null;

        if (DriveList.SelectedIndex > -1)
            SelectionHelper.GetListViewItemContainer(DriveList).Focus();

        HomeSavedLocationsList.ItemsSource = NavigationBox.SavedItems;
        if (NavigationBox.SavedItems.Count == 0)
            Settings.HomeLocationsExpanded = false;
    }

    private bool InitNavigation(string path = "")
    {
        if (path is null)
            return true;

        var realPath = FolderHelper.FolderExists(string.IsNullOrEmpty(path) ? DEFAULT_PATH : path);
        if (realPath is null)
            return false;

        FileActions.IsDriveViewVisible = false;
        FileActions.IsExplorerVisible = true;
        FileActions.HomeEnabled = true;
        RuntimeSettings.BrowseDrive = null;

        UpdateFileOp();

        Task.Delay(EXPLORER_NAV_DELAY).ContinueWith(task =>
        {
            _ = Dispatcher.BeginInvoke(new Action(() => RuntimeSettings.IsExplorerLoaded = true));
        });

        return _navigateToPath(realPath);
    }

    private static void ListDevices(IEnumerable<LogicalDevice> devices)
    {
        if (devices is null)
            return;

        var deviceVMs = devices.Select(d => new LogicalDeviceViewModel(d));

        if (!DevicesObject.DevicesChanged(deviceVMs))
            return;

        DeviceHelper.DeviceListSetup(deviceVMs);

        if (!Settings.AutoRoot)
            return;

        foreach (var item in DevicesObject.LogicalDeviceViewModels.Where(device => device.Root is AbstractDevice.RootStatus.Unchecked))
        {
            Task.Run(() => item.EnableRoot(true));
        }
    }

    private void ConnectTimer_Tick(object sender, EventArgs e)
    {
        if (ConnectTimer.Interval == CONNECT_TIMER_INIT)
            ConnectTimer.Interval = CONNECT_TIMER_INTERVAL;

        bool deferBackgroundPolling = RuntimeSettings.IsPollingStopped || FileOpQ.HasRunningSyncOperations;
        bool startedDeviceRefresh = false;

        if (Settings.PollDevices && !deferBackgroundPolling && DeviceRefreshMutex.Wait(0))
        {
            startedDeviceRefresh = true;
            Task.Run(() =>
            {
                try
                {
                    RefreshDevices();
                }
                finally
                {
                    DeviceRefreshMutex.Release();
                }
            });
        }

        Task.Run(() =>
        {
            if (!ConnectTimerMutex.Wait(0))
                return;

            try
            {
                if (deferBackgroundPolling || startedDeviceRefresh || DeviceRefreshMutex.CurrentCount == 0)
                    return;

                if (Settings.PollBattery)
                {
                    DeviceHelper.UpdateDevicesBatInfo();
                }

                if (FileActions.IsDriveViewVisible && Settings.PollDrives)
                {
                    _ = Dispatcher.BeginInvoke(new Action(() => FileActionLogic.RefreshDrives(true)));
                }

                if (RuntimeSettings.IsDevicesPaneOpen)
                {
                    DeviceHelper.UpdateDevicesRootAccess();

                    DeviceHelper.UpdateWsaPkgStatus();
                }
            }
            finally
            {
                ConnectTimerMutex.Release();
            }
        });
    }

    private void RefreshDevices()
    {
        var devices = ADBService.GetDevices();
        _ = Dispatcher.BeginInvoke(new Action<IEnumerable<LogicalDevice>>(ListDevices), devices);

        Task.Run(DeviceHelper.ConnectWsaDevice);

        if (!RuntimeSettings.IsDevicesPaneOpen)
            return;

        _ = Dispatcher.BeginInvoke(new Action(DevicesObject.UpdateLogicalIp));

        if (MdnsService.State is MDNS.MdnsState.Running)
        {
            var services = WiFiPairingService.GetServices();
            _ = Dispatcher.BeginInvoke(new Action<IEnumerable<ServiceDevice>>(DeviceHelper.ListServices), services);
        }
    }

    public bool NavigateToPath(FileClass file)
    {
        if (file is null)
            return false;

        if (!bfNavigation)
            prevPath = file.FullPath;

        string realPath = !string.IsNullOrEmpty(file.LinkTarget)
            ? file.LinkTarget
            : file.FullPath;

        return realPath is not null && _navigateToPath(realPath);
    }

    public bool NavigateToPath(string path)
    {
        if (path is null)
            return false;

        if (!bfNavigation)
            prevPath = path;

        var realPath = FolderHelper.FolderExists(path);
        return realPath is not null && _navigateToPath(realPath);
    }

    private bool _navigateToPath(string realPath)
    {
        PasteGrid.Visibility = Visibility.Collapsed;
        FileActions.ListingInProgress = true;

        FileActions.ExplorerFilter = "";
        NavHistory.Navigate(realPath);

        SelectionHelper.SetFirstSelectedIndex(ExplorerGrid, -1);
        SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, -1);

        ExplorerGrid.Focus();
        CurrentPath = realPath;
        if (DevicesObject.Current is not null)
        {
            Settings.LastDevicePath = realPath;
            Settings.SetLastDevicePath(DevicesObject.Current.ID, realPath);
        }

        NavigationBox.Path = realPath == RECYCLE_PATH ? AdbLocation.StringFromLocation(Navigation.SpecialLocation.RecycleBin) : realPath;
        ParentPath = FileHelper.GetParentPath(CurrentPath);
        CurrentDrive = DriveHelper.GetCurrentDrive(CurrentPath);

        FileActions.IsRecycleBin = CurrentPath == RECYCLE_PATH;
        FileActions.IsAppDrive = CurrentPath == AdbLocation.StringFromLocation(Navigation.SpecialLocation.PackageDrive);
        FileActions.IsTemp = CurrentPath == TEMP_PATH;
        FileActions.ParentEnabled = CurrentPath != ParentPath && !FileActions.IsRecycleBin && !FileActions.IsAppDrive;

        FileActionLogic.IsPasteEnabled();

        if (!RuntimeSettings.IsRootActive && DevicesObject.Current.Root is AbstractDevice.RootStatus.Enabled)
            RuntimeSettings.IsRootActive = true;

        FileActions.PushPackageEnabled = Settings.EnableApk && DevicesObject?.Current?.Type is not AbstractDevice.DeviceType.Recovery;
        FileActions.UninstallPackageEnabled = false;

        FileActions.ContextPushPackagesEnabled =
        FileActions.IsUninstallVisible.Value = FileActions.IsAppDrive;

        FileActions.PushFilesFoldersEnabled =
        FileActions.ContextNewEnabled =
        FileActions.ContextPushEnabled =
        FileActions.NewEnabled = !FileActions.IsRecycleBin && !FileActions.IsAppDrive;

        OriginalPath.Visibility =
        OriginalDate.Visibility = Visible(FileActions.IsRecycleBin);

        PackageName.Visibility =
        PackageType.Visibility =
        PackageUid.Visibility =
        PackageVersion.Visibility = Visible(FileActions.IsAppDrive);

        IconColumn.Visibility =
        NameColumn.Visibility =
        DateColumn.Visibility =
        TypeColumn.Visibility =
        SizeColumn.Visibility = Visible(!FileActions.IsAppDrive);

        FileActions.CopyPathDescription.Value = FileActions.IsAppDrive ? Strings.Resources.S_COPY_APK_NAME : Strings.Resources.S_COPY_PATH;
        
        if (FileActions.IsRecycleBin)
        {
            TrashHelper.ParseIndexersAsync().ContinueWith(_ => DirList.Navigate(realPath));

            FileActions.DeleteDescription.Value = Strings.Resources.S_EMPTY_TRASH;
            FileActions.RestoreDescription.Value = Strings.Resources.S_RESTORE_ALL;
        }
        else
        {
            if (FileActions.IsAppDrive)
            {
                FileActionLogic.UpdatePackages(true);
                FileActionLogic.UpdateFileActions();
                return true;
            }

            DirList.Navigate(realPath);

            FileActions.DeleteDescription.Value = Strings.Resources.S_DELETE_ACTION;
        }

        RuntimeSettings.ExplorerSource = DirList.FileList;
        FileActionLogic.UpdateFileActions();
        return true;
    }

    private void NavigateToLocation(AdbLocation location)
    {
        SelectionHelper.SetIsMenuOpen(ExplorerGrid.ContextMenu, false);

        if (location.Location is Navigation.SpecialLocation.DriveView)
        {
            FileActions.IsRecycleBin = false;
            RuntimeSettings.IsPathBoxFocused = false;
            FileActionLogic.RefreshDrives();
            DriveViewNav();

            FileActionLogic.UpdateFileActions();
        }
        else
        {
            if (!FileActions.IsExplorerVisible)
                InitNavigation(location.DisplayName);
            else
                NavigateToPath(location.DisplayName);
        }
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
                if (bfNavigation)
                {
                    SelectionHelper.SetCurrentSelectedIndex(ExplorerGrid, ExplorerGrid.SelectedIndex);
                    bfNavigation = false;
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
        SearchBoxMaxWidth();

        if (!RuntimeSettings.IsWindowLoaded)
            return;

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

        RuntimeSettings.AutoHideSearchBox = true;
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

    private void FilterExplorerItems(bool refreshOnly = false)
    {
        if (!FileActions.IsExplorerVisible)
            return;

        var collectionView = CollectionViewSource.GetDefaultView(ExplorerGrid.ItemsSource);
        if (collectionView is null)
            return;

        if (refreshOnly)
            collectionView.Refresh();

        if (FileActions.IsAppDrive)
        {
            collectionView.Filter = Settings.ShowSystemPackages
                ? FileHelper.PkgFilter()
                : pkg => ((Package)pkg).Type is Package.PackageType.User;

            if (collectionView.SortDescriptions.All(d => d.PropertyName != nameof(Package.Type)))
            {
                ExplorerGrid.Columns[8].SortDirection = ListSortDirection.Descending;

                collectionView.SortDescriptions.Add(new(nameof(Package.Type), ListSortDirection.Descending));
            }
        }
        else
        {
            collectionView.Filter = !Settings.ShowHiddenItems
                ? FileHelper.HideFiles()
                : file => !FileHelper.IsHiddenRecycleItem((FileClass)file);

            if (!collectionView.SortDescriptions.Any(d => d.PropertyName
                    is nameof(FileClass.IsTemp)
                    or nameof(FileClass.IsDirectory)
                    or nameof(FileClass.SortName)))
            {
                ExplorerGrid.Columns[1].SortDirection = ListSortDirection.Ascending;

                collectionView.SortDescriptions.Add(new(nameof(FileClass.IsTemp), ListSortDirection.Descending));
                collectionView.SortDescriptions.Add(new(nameof(FileClass.IsDirectory), ListSortDirection.Descending));
                collectionView.SortDescriptions.Add(new(nameof(FileClass.SortName), ListSortDirection.Ascending));
            }
        }
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

    private void UpdateMdns()
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
        ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;
        InstallKeyboardHook();
        _ = TryRestoreWindowBounds();

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

    private void RestartAdbButton_Click(object sender, RoutedEventArgs e)
    {
        ADBService.KillAdbServer();
        MdnsService.State = MDNS.MdnsState.Disabled;
        UpdateMdns();
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
                DirList.FileList.Remove(ExplorerGrid.SelectedItem as FileClass);
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

        FileActions.IsRenameUnique = !DirList.FileList.Except([file]).Any(f => f.FullName.Equals(fullName, comparison));
    }

    private void NewItem(bool isFolder)
    {
        var fileName = FileHelper.DuplicateFile(DirList.FileList, isFolder
            ? Strings.Resources.S_NEW_FOLDER
            : Strings.Resources.S_NEW_ITEM);

        FileClass newItem = new(fileName, FileHelper.ConcatPaths(CurrentPath, fileName), isFolder ? FileType.Folder : FileType.File, isTemp: true);
        DirList.FileList.Insert(0, newItem);

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
        => Task.Run(RefreshDevices);

    private void AndroidRobotLicense_Click(object sender, RoutedEventArgs e)
        => SettingsHelper.ShowAndroidRobotLicense();

    private void ExplorerGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (FileActions.IsAppDrive)
            return;

        var collectionView = CollectionViewSource.GetDefaultView(ExplorerGrid.ItemsSource);
        var sortDirection = ListHelper.Invert(e.Column.SortDirection);
        e.Column.SortDirection = sortDirection;

        collectionView.SortDescriptions.Clear();
        collectionView.SortDescriptions.Add(new(nameof(FileClass.IsTemp), ListSortDirection.Descending));
        collectionView.SortDescriptions.Add(new(nameof(FileClass.IsDirectory), ListHelper.Invert(sortDirection)));

        if (e.Column.SortMemberPath != nameof(FileClass.FullName))
            collectionView.SortDescriptions.Add(new(e.Column.SortMemberPath, sortDirection));

        collectionView.SortDescriptions.Add(new(nameof(FileClass.SortName), sortDirection));

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
                    RuntimeSettings.DragBitmap = FileToIconConverter.GetBitmapSource(selectedItems.First(), GetDragPreviewIconSize());

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

    private void KillAdbButton_Click(object sender, RoutedEventArgs e)
    {
        ADBService.KillAdbProcess();
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

    private void MouseUpOnName(DataGridCell cell)
    {
        if (cell.IsReadOnly
            || (DevicesObject.Current.Root is not AbstractDevice.RootStatus.Enabled
                && ((FileClass)cell.DataContext).Type is not (FileType.File or FileType.Folder)))
            return;

        var path = ((FileClass)ExplorerGrid.SelectedItem).FullPath;

        if (ExplorerGrid.SelectedItems.Count == 1 && WasSelected && !WasEditing)
        {
            _ = Task.Run(async () =>
            {
                var start = DateTime.Now;

                while (true)
                {
                    await Task.Delay(100);

                    if (DateTime.Now - start > RENAME_CLICK_DELAY)
                        break;

                    var currentPath = await Dispatcher.InvokeAsync(() => ((FileClass)ExplorerGrid.SelectedItem)?.FullPath);
                    if (ClickCount > 1 || currentPath != path)
                        return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    cell.IsEditing = true;
                    FileActions.IsExplorerEditing = true;
                });
            });
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

        if (CopyPaste.CurrentFiles.Any())
            RuntimeSettings.DragBitmap = FileToIconConverter.GetBitmapSource(CopyPaste.CurrentFiles.First(), GetDragPreviewIconSize());

        e.Handled = true;
    }

    private void MainWin_LocationChanged(object sender, EventArgs e)
    {
        if (!RuntimeSettings.IsWindowLoaded)
            return;
    }

    private int GetDragPreviewIconSize()
    {
        double logicalSize = Math.Max(SystemParameters.IconWidth, SystemParameters.IconHeight);
        double dpiScale = RuntimeSettings.DpiScalingFactor > 0
            ? 1d / RuntimeSettings.DpiScalingFactor
            : 1d;

        return (int)Math.Clamp(Math.Round(logicalSize * dpiScale), 32, 64);
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

    private bool IsTerminalFocused()
    {
        return RuntimeSettings.IsTerminalOpen
               && !(TerminalSearchTextBox?.IsKeyboardFocusWithin ?? false)
               && (isTerminalFocused || TerminalWebView.IsFocused || TerminalWebView.IsKeyboardFocusWithin);
    }

    private bool IsTerminalShortcutContextActive()
    {
        if (!RuntimeSettings.IsTerminalOpen || !terminalShortcutArmed)
            return false;

        if (!IsActive)
            return false;

        IntPtr foregroundWindow = GetForegroundWindow();
        if (new WindowInteropHelper(this).Handle != foregroundWindow)
            return false;

        return !SearchBox.IsFocused
               && NavigationBox.Mode is not NavigationBox.ViewMode.Path
               && !FileActions.IsExplorerEditing;
    }

    private static void LogTerminalUiDiagnostic(string eventName, string details)
    {
        _ = eventName;
        _ = details;
    }

    private void EnqueuePendingTerminalChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        pendingTerminalChunks.Enqueue(chunk);

        while (pendingTerminalChunks.Count > MAX_PENDING_TERMINAL_CHUNKS)
            pendingTerminalChunks.Dequeue();
    }

    private void InstallKeyboardHook()
    {
        if (keyboardHookHandle != IntPtr.Zero)
            return;

        try
        {
            using Process currentProcess = Process.GetCurrentProcess();
            using ProcessModule mainModule = currentProcess.MainModule;
            IntPtr moduleHandle = mainModule is null ? IntPtr.Zero : GetModuleHandle(mainModule.ModuleName);
            keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardHookProc, moduleHandle, 0);
            LogTerminalUiDiagnostic("keyboard_hook.install", $"ok={keyboardHookHandle != IntPtr.Zero}; handle={keyboardHookHandle}");
        }
        catch (Exception ex)
        {
            LogTerminalUiDiagnostic("keyboard_hook.install.failed", ex.Message);
        }
    }

    private void RemoveKeyboardHook()
    {
        if (keyboardHookHandle == IntPtr.Zero)
            return;

        try
        {
            _ = UnhookWindowsHookEx(keyboardHookHandle);
            LogTerminalUiDiagnostic("keyboard_hook.remove", $"handle={keyboardHookHandle}");
        }
        catch (Exception ex)
        {
            LogTerminalUiDiagnostic("keyboard_hook.remove.failed", ex.Message);
        }
        finally
        {
            keyboardHookHandle = IntPtr.Zero;
            activeTerminalShortcutKeys.Clear();
        }
    }

    private async Task EnsureTerminalWebViewReadyAsync()
    {
        if (!IsLoaded || TerminalWebView is null)
            return;

        if (!File.Exists(TerminalHostPath))
        {
            AddCommandLog($"terminal.webview host file missing: {TerminalHostPath}");
            return;
        }

        try
        {
            if (TerminalWebView.CoreWebView2 is null)
                await TerminalWebView.EnsureCoreWebView2Async();

            ConfigureTerminalWebView();

            if (TerminalWebView.Source is null
                || !string.Equals(TerminalWebView.Source.AbsoluteUri, TerminalHostUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                isTerminalPageReady = false;
                TerminalWebView.Source = TerminalHostUri;
            }
        }
        catch (Exception ex)
        {
            AddCommandLog($"terminal.webview init failed: {ex.Message}");
        }
    }

    private void ConfigureTerminalWebView()
    {
        if (isTerminalWebViewInitialized || TerminalWebView.CoreWebView2 is null)
            return;

        CoreWebView2Settings settings = TerminalWebView.CoreWebView2.Settings;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;

        TerminalWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            TERMINAL_HOST_NAME,
            TerminalHostFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);

        TerminalWebView.CoreWebView2.WebMessageReceived += TerminalWebView_WebMessageReceived;
        isTerminalWebViewInitialized = true;
    }

    private void PostTerminalMessage(object message)
    {
        if (!isTerminalPageReady || TerminalWebView.CoreWebView2 is null)
            return;

        TerminalWebView.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(message));
    }

    private void FlushPendingTerminalOutput()
    {
        if (!isTerminalPageReady)
            return;

        if (pendingTerminalChunks.Count == 0)
        {
            if (!string.IsNullOrEmpty(Terminal.OutputText))
                PostTerminalMessage(new { type = "write", data = Terminal.OutputText });

            return;
        }

        while (pendingTerminalChunks.Count > 0)
            PostTerminalMessage(new { type = "write", data = pendingTerminalChunks.Dequeue() });
    }

    private void Terminal_OutputChunkReceived(string chunk, bool isError)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        if (!isTerminalPageReady)
        {
            EnqueuePendingTerminalChunk(chunk);
            return;
        }

        PostTerminalMessage(new { type = "write", data = chunk });
    }

    private void Terminal_Cleared()
    {
        pendingTerminalChunks.Clear();
        terminalSelectionCache = "";
        IsTerminalSearchVisible = false;
        TerminalSearchCountText = "0/0";

        if (isTerminalPageReady)
            PostTerminalMessage(new { type = "clear" });
    }

    private void TerminalWebView_GotFocus(object sender, RoutedEventArgs e)
    {
        isTerminalFocused = true;
        LogTerminalUiDiagnostic("focus.got", $"focused={IsTerminalFocused()}");
    }

    private void TerminalWebView_LostFocus(object sender, RoutedEventArgs e)
    {
        isTerminalFocused = false;
        LogTerminalUiDiagnostic("focus.lost", $"focused={IsTerminalFocused()}");
    }

    private void TerminalWebView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        terminalShortcutArmed = true;
        isTerminalFocused = true;

        LogTerminalUiDiagnostic("mouse.down", $"focused={IsTerminalFocused()}; armed={terminalShortcutArmed}; searchVisible={IsTerminalSearchVisible}; searchBoxFocused={TerminalSearchTextBox?.IsKeyboardFocusWithin == true}");
    }

    private void TerminalWebView_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        terminalShortcutArmed = true;
        isTerminalFocused = true;

        LogTerminalUiDiagnostic("mouse.up", $"focused={IsTerminalFocused()}; armed={terminalShortcutArmed}; searchVisible={IsTerminalSearchVisible}; searchBoxFocused={TerminalSearchTextBox?.IsKeyboardFocusWithin == true}");

        if (!(TerminalSearchTextBox?.IsKeyboardFocusWithin ?? false))
            _ = Dispatcher.BeginInvoke(async () => await FocusTerminalInputAsync(), DispatcherPriority.Input);
    }

    private void TerminalWebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
            return;

        isTerminalPageReady = false;
        AddCommandLog($"terminal.webview navigation failed: {e.WebErrorStatus}");
    }

    private async void TerminalWebView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.C or Key.V or Key.L or Key.F)
            LogTerminalUiDiagnostic("keydown", $"key={e.Key}; modifiers={Keyboard.Modifiers}; focused={IsTerminalFocused()}");

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.F)
        {
            e.Handled = true;
            OpenTerminalSearch();
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        switch (e.Key)
        {
            case Key.C:
                e.Handled = true;
                var selectedText = terminalSelectionCache;
                if (!string.IsNullOrEmpty(selectedText))
                    Clipboard.SetText(selectedText);
                else
                    await Terminal.InterruptAsync();

                break;

            case Key.V:
                e.Handled = true;
                if (Clipboard.ContainsText())
                {
                    var pasteText = Clipboard.GetText()
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\r", "\n", StringComparison.Ordinal);

                    if (!string.IsNullOrEmpty(pasteText))
                        await Terminal.SendLiteralAsync(pasteText);
                }

                break;

            case Key.L:
                e.Handled = true;
                Terminal.Clear();
                break;
        }
    }

    private void ComponentDispatcher_ThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled || !IsTerminalFocused())
            return;

        if (msg.message is not WM_KEYDOWN and not WM_SYSKEYDOWN)
            return;

        if ((GetKeyState(VK_CONTROL) & 0x8000) == 0)
            return;

        Key key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
        if (key is not (Key.C or Key.V or Key.L))
            return;

        handled = true;
        LogTerminalUiDiagnostic("thread_preprocess.suppress", $"key={key}; msg=0x{msg.message:X}; focused={IsTerminalFocused()}");
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || lParam == IntPtr.Zero)
            return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);

        int message = unchecked((int)wParam);
        if (message is not WM_KEYDOWN and not WM_SYSKEYDOWN and not WM_KEYUP and not WM_SYSKEYUP)
            return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        Key key = KeyInterop.KeyFromVirtualKey((int)info.vkCode);
        if (key is not (Key.C or Key.V or Key.L or Key.F))
            return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);

        if (!IsTerminalShortcutContextActive())
            return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);

        bool ctrlPressed = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;

        if (!ctrlPressed && !isKeyUp)
            return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);

        if (key is Key.F && shiftPressed && !isKeyUp)
            return CallNextHookEx(keyboardHookHandle, nCode, wParam, lParam);

        if (isKeyUp)
        {
            activeTerminalShortcutKeys.Remove(key);
            LogTerminalUiDiagnostic("keyboard_hook.keyup", $"key={key}; armed={terminalShortcutArmed}");
            return (IntPtr)1;
        }

        if (!activeTerminalShortcutKeys.Add(key))
            return (IntPtr)1;

        LogTerminalUiDiagnostic("keyboard_hook.keydown", $"key={key}; armed={terminalShortcutArmed}; focused={IsTerminalFocused()}");
        _ = Dispatcher.BeginInvoke(new Action(() => _ = HandleTerminalShortcutAsync(key)));

        return (IntPtr)1;
    }

    private async Task HandleTerminalShortcutAsync(Key key)
    {
        try
        {
            switch (key)
            {
                case Key.C:
                    if (!string.IsNullOrEmpty(terminalSelectionCache))
                        await TrySetClipboardTextAsync(terminalSelectionCache);
                    else
                        await Terminal.InterruptAsync();
                    break;

                case Key.V:
                    var pasteText = TryGetClipboardText()
                        ?.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\r", "\n", StringComparison.Ordinal);

                    if (!string.IsNullOrEmpty(pasteText))
                        await Terminal.SendLiteralAsync(pasteText);
                    break;

                case Key.L:
                    Terminal.Clear();
                    break;

                case Key.F:
                    OpenTerminalSearch();
                    break;
            }
        }
        catch (Exception ex)
        {
            LogTerminalUiDiagnostic("shortcut.failed", $"key={key}; ex={ex.GetType().Name}; msg={ex.Message}");
        }
    }

    private static string TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch
        {
            return "";
        }
    }

    private async Task TrySetClipboardTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        const int maxAttempts = 2;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, false);
                return;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x800401D0)
            {
                LogTerminalUiDiagnostic("clipboard.retry", $"attempt={attempt}; hr=0x{ex.HResult:X8}");
                await Task.Delay(20 * attempt);
            }
        }

        LogTerminalUiDiagnostic("clipboard.giveup", $"len={text.Length}");
    }

    private async void TerminalWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var payload = JToken.Parse(e.WebMessageAsJson);
            if (payload is not JObject message)
                return;

            switch ((string)message["type"])
            {
                case "ready":
                    isTerminalPageReady = true;
                    LogTerminalUiDiagnostic("web.ready", $"focused={IsTerminalFocused()}");
                    FlushPendingTerminalOutput();
                    if (RuntimeSettings.IsTerminalOpen)
                        await FocusTerminalInputAsync();
                    break;

                case "input":
                case "inputBinary":
                    var input = ((string)message["data"] ?? "")
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\r", "\n", StringComparison.Ordinal);
                    await Terminal.SendLiteralAsync(input);
                    break;

                case "interrupt":
                    await Terminal.InterruptAsync();
                    break;

                case "clear":
                    Terminal.Clear();
                    break;

                case "copy":
                    var copiedText = (string)message["data"] ?? "";
                    if (!string.IsNullOrEmpty(copiedText))
                        await TrySetClipboardTextAsync(copiedText);
                    break;

                case "selection":
                    terminalSelectionCache = (string)message["data"] ?? "";
                    break;

                case "search_results":
                    int total = message["total"]?.Value<int>() ?? 0;
                    int current = message["current"]?.Value<int>() ?? 0;
                    TerminalSearchCountText = total <= 0
                        ? "0/0"
                        : $"{current}/{total}";
                    LogTerminalUiDiagnostic("web.search_results", $"current={current}; total={total}");
                    break;

                case "request_search":
                    var requestedSearch = (string)message["data"] ?? "";
                    if (!string.IsNullOrWhiteSpace(requestedSearch) && requestedSearch.IndexOf('\n') < 0)
                        TerminalSearchQuery = requestedSearch;

                    OpenTerminalSearch();
                    break;

                case "paste":
                    var pasteText = TryGetClipboardText()
                        ?.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\r", "\n", StringComparison.Ordinal);

                    if (!string.IsNullOrEmpty(pasteText))
                        await Terminal.SendLiteralAsync(pasteText);
                    break;

                case "paste_data":
                    var pastedData = ((string)message["data"] ?? "")
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace("\r", "\n", StringComparison.Ordinal);
                    if (!string.IsNullOrEmpty(pastedData))
                        await Terminal.SendLiteralAsync(pastedData);
                    break;

                case "open_external":
                    OpenTerminalExternalLink((string)message["data"] ?? "");
                    break;

                case "log":
                    LogTerminalUiDiagnostic("web.message.log", (string)message["data"] ?? "");
                    break;

                case "focus_state":
                    isTerminalFocused = message["focused"]?.Value<bool>() == true;
                    LogTerminalUiDiagnostic("web.focus_state", $"focused={isTerminalFocused}");
                    break;

                case "resize":
                    break;
            }
        }
        catch (Exception ex)
        {
            AddCommandLog($"terminal.webview message failed: {ex.Message}");
        }
    }

    private async void TerminalInterrupt_Click(object sender, RoutedEventArgs e)
    {
        await Terminal.InterruptAsync();
        FocusTerminalInput();
    }

    private async void TerminalFind_Click(object sender, RoutedEventArgs e)
    {
        await OpenTerminalSearchAsync();
    }

    private void TerminalSearchPrev_Click(object sender, RoutedEventArgs e)
    {
        NavigateTerminalSearch(previous: true);
    }

    private void TerminalSearchNext_Click(object sender, RoutedEventArgs e)
    {
        NavigateTerminalSearch(previous: false);
    }

    private void TerminalSearchClose_Click(object sender, RoutedEventArgs e)
    {
        CloseTerminalSearch();
    }

    private void TerminalSearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                NavigateTerminalSearch(previous: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;

            case Key.Escape:
                e.Handled = true;
                CloseTerminalSearch();
                break;

            case Key.F3:
                e.Handled = true;
                NavigateTerminalSearch(previous: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
        }
    }

    private void TerminalClear_Click(object sender, RoutedEventArgs e)
    {
        Terminal.Clear();
        FocusTerminalInput();
    }

    private async Task FocusTerminalInputAsync()
    {
        if (!IsLoaded)
            return;

        await EnsureTerminalWebViewReadyAsync();
        terminalShortcutArmed = true;
        isTerminalFocused = true;
        TerminalWebView.Focus();
        PostTerminalMessage(new { type = "focus" });
        LogTerminalUiDiagnostic("focus.request", $"focused={IsTerminalFocused()}; armed={terminalShortcutArmed}");
    }

    private void FocusTerminalInput()
    {
        _ = FocusTerminalInputAsync();
    }

    private void OpenTerminalSearch()
    {
        _ = OpenTerminalSearchAsync();
    }

    private async Task OpenTerminalSearchAsync()
    {
        if (!IsLoaded)
            return;

        await EnsureTerminalWebViewReadyAsync();
        IsTerminalSearchVisible = true;
        terminalShortcutArmed = true;
        isTerminalFocused = false;
        PostTerminalSearchQuery();
        LogTerminalUiDiagnostic("search.request", $"focused={IsTerminalFocused()}; armed={terminalShortcutArmed}; queryLen={TerminalSearchQuery.Length}");
        await Dispatcher.BeginInvoke(() =>
        {
            TerminalSearchTextBox.Focus();
            TerminalSearchTextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void PostTerminalSearchQuery()
    {
        if (!isTerminalPageReady)
            return;

        PostTerminalMessage(new { type = "search.set", data = TerminalSearchQuery ?? "" });
    }

    private void NavigateTerminalSearch(bool previous)
    {
        if (!isTerminalPageReady)
            return;

        PostTerminalMessage(new { type = previous ? "search.prev" : "search.next" });
    }

    private void CloseTerminalSearch(bool clearQuery = true, bool focusTerminal = true)
    {
        IsTerminalSearchVisible = false;
        TerminalSearchCountText = "0/0";

        if (clearQuery)
            TerminalSearchQuery = "";

        if (isTerminalPageReady)
            PostTerminalMessage(new { type = "search.close" });

        if (focusTerminal)
            FocusTerminalInput();
    }

    private void OpenTerminalExternalLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps
                && uri.Scheme != Uri.UriSchemeFtp))
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(RuntimeSettings.DefaultBrowserPath))
                Process.Start(RuntimeSettings.DefaultBrowserPath, $"\"{uri.AbsoluteUri}\"");
            else
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogTerminalUiDiagnostic("open_external.failed", $"{uri.AbsoluteUri}; ex={ex.GetType().Name}; msg={ex.Message}");
        }
    }
}
