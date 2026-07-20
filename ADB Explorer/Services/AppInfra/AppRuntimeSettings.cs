using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using System.Net;

namespace ADB_Explorer.Services;

internal enum RuntimeUiChange
{
    MdnsExpander,
    SearchText,
    PathBoxFocus,
    SearchBoxFocus,
    TerminalVisibility,
    CurrentDevice,
    SettingsGroups,
    Cursor,
    PaneVisibility,
}

public class AppRuntimeSettings : ViewModelBase
{
    internal event Action<RuntimeUiChange> UiChangeRequested;

    internal AppRuntimeSettings(AppSettings settings)
    {
        settings.PropertyChanged += (object sender, PropertyChangedEventArgs e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ForceFluentStyles))
                OnPropertyChanged(nameof(UseFluentStyles));
        };
    }

    public bool ResetAppSettings { get; set; } = false;

    private bool isSettingsPaneOpen = false;
    public bool IsSettingsPaneOpen
    {
        get => isSettingsPaneOpen;
        set
        {
            if (Set(ref isSettingsPaneOpen, value))
                UiChangeRequested?.Invoke(RuntimeUiChange.PaneVisibility);
        }
    }

    private bool isDevicesPaneOpen = false;
    public bool IsDevicesPaneOpen
    {
        get => isDevicesPaneOpen;
        set
        {
            if (Set(ref isDevicesPaneOpen, value))
                UiChangeRequested?.Invoke(RuntimeUiChange.PaneVisibility);
        }
    }

    private bool isMdnsExpanderOpen = false;
    public bool IsMdnsExpanderOpen
    {
        get => isMdnsExpanderOpen;
        set
        {
            if (Set(ref isMdnsExpanderOpen, value))
                UiChangeRequested?.Invoke(RuntimeUiChange.MdnsExpander);
        }
    }

    private bool isOperationsViewOpen = false;
    public bool IsOperationsViewOpen
    {
        get => isOperationsViewOpen;
        set
        {
            if (Set(ref isOperationsViewOpen, value))
            {
                IsDetailedPeekMode = false;
                UiChangeRequested?.Invoke(RuntimeUiChange.PaneVisibility);
            }
        }
    }

    private bool sortedView = false;
    public bool SortedView
    {
        get => sortedView;
        set => Set(ref sortedView, value);
    }

    private bool groupsExpanded = false;
    public bool GroupsExpanded
    {
        get => groupsExpanded;
        set
        {
            if (Set(ref groupsExpanded, value))
                UiChangeRequested?.Invoke(RuntimeUiChange.SettingsGroups);
        }
    }

    private string searchText = "";
    public string SearchText
    {
        get => searchText;
        set
        {
            if (Set(ref searchText, value))
                UiChangeRequested?.Invoke(RuntimeUiChange.SearchText);
        }
    }

    private double maxSearchBoxWidth = AdbExplorerConst.DEFAULT_SEARCH_WIDTH;
    public double MaxSearchBoxWidth
    {
        get => maxSearchBoxWidth;
        set => Set(ref maxSearchBoxWidth, value);
    }

    private LogicalDeviceViewModel deviceToBrowse = null;
    public LogicalDeviceViewModel DeviceToOpen
    {
        get => deviceToBrowse;
        set => Set(ref deviceToBrowse, value);
    }

    private bool isManualPairingInProgress = false;
    public bool IsManualPairingInProgress
    {
        get => isManualPairingInProgress;
        set => Set(ref isManualPairingInProgress, value);
    }

    private DeviceViewModel connectNewDevice = null;
    public DeviceViewModel ConnectNewDevice
    {
        get => connectNewDevice;
        set => Set(ref connectNewDevice, value);
    }

    private DateTime lastServerResponse = DateTime.Now;
    public DateTime LastServerResponse
    {
        get => lastServerResponse;
        set
        {
            lastServerResponse = value;
            RefreshServerResponseStatus();
        }
    }

    public string TimeFromLastResponse => $"{DateTime.Now.Subtract(LastServerResponse).TotalSeconds:0}";

    public bool ServerUnresponsive => DateTime.Now.Subtract(LastServerResponse) > AdbExplorerConst.SERVER_RESPONSE_TIMEOUT;

    internal void RefreshServerResponseStatus()
    {
        if (Application.Current is App app)
        {
            app.EnqueueUiLatest("adb.last-response", "adb.last-response", () =>
            {
                OnPropertyChanged(nameof(LastServerResponse));
                OnPropertyChanged(nameof(TimeFromLastResponse));
                OnPropertyChanged(nameof(ServerUnresponsive));
            });
        }
    }

    private Version adbVersion;
    public Version AdbVersion
    {
        get => adbVersion;
        set
        {
            if (Set(ref adbVersion, value))
                OnPropertyChanged(nameof(AdbVersionString));
        }
    }

    public string AdbVersionString => $"\u200E - v{AdbVersion}";

    private LogicalDeviceViewModel currentDevice = null;
    public LogicalDeviceViewModel CurrentDevice
    {
        get => currentDevice;
        set
        {
            if (Set(ref currentDevice, value))
                UiChangeRequested?.Invoke(RuntimeUiChange.CurrentDevice);
        }
    }

    private bool? isPathBoxFocused = null;
    public bool? IsPathBoxFocused
    {
        get => isPathBoxFocused;
        set
        {
            if (!Set(ref isPathBoxFocused, value))
                OnPropertyChanged();
            UiChangeRequested?.Invoke(RuntimeUiChange.PathBoxFocus);
        }
    }

    private bool isSearchBoxFocused = false;
    public bool IsSearchBoxFocused
    {
        get => isSearchBoxFocused;
        set
        {
            if (!Set(ref isSearchBoxFocused, value))
                OnPropertyChanged();
            UiChangeRequested?.Invoke(RuntimeUiChange.SearchBoxFocus);
        }
    }

    private bool isTerminalOpen = false;
    public bool IsTerminalOpen
    {
        get => isTerminalOpen;
        set
        {
            if (Set(ref isTerminalOpen, value))
                UiChangeRequested?.Invoke(RuntimeUiChange.TerminalVisibility);
        }
    }

    private bool isLogOpen = false;
    public bool IsLogOpen
    {
        get => isLogOpen;
        set => Set(ref isLogOpen, value);
    }

    private bool isLogPaused = false;
    public bool IsLogPaused
    {
        get => isLogPaused;
        set => Set(ref isLogPaused, value);
    }

    private bool isRootActive = false;
    public bool IsRootActive
    {
        get => isRootActive;
        set => Set(ref isRootActive, value);
    }

    public bool IsDebug
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    private bool isWindowLoaded = false;
    public bool IsWindowLoaded
    {
        get => isWindowLoaded;
        set => Set(ref isWindowLoaded, value);
    }

    private bool isExplorerLoaded = false;
    public bool IsExplorerLoaded
    {
        get => isExplorerLoaded;
        set => Set(ref isExplorerLoaded, value);
    }

    private string adbReadRate = null;
    public string AdbReadRate
    {
        get => adbReadRate;
        set => Set(ref adbReadRate, value);
    }

    private string adbWriteRate = null;
    public string AdbWriteRate
    {
        get => adbWriteRate;
        set => Set(ref adbWriteRate, value);
    }

    private string adbOtherRate = null;
    public string AdbOtherRate
    {
        get => adbOtherRate;
        set => Set(ref adbOtherRate, value);
    }

    private bool isAdbReadActive = false;
    public bool IsAdbReadActive
    {
        get => isAdbReadActive;
        set => Set(ref isAdbReadActive, value);
    }

    private bool isAdbWriteActive = false;
    public bool IsAdbWriteActive
    {
        get => isAdbWriteActive;
        set => Set(ref isAdbWriteActive, value);
    }

    private bool isDetailedPeekMode = false;
    public bool IsDetailedPeekMode
    {
        get => isDetailedPeekMode;
        set => Set(ref isDetailedPeekMode, value);
    }

    private BitmapSource dragBitmap = null;
    public BitmapSource DragBitmap
    {
        get => dragBitmap;
        set => Set(ref dragBitmap, value);
    }

    private DragDropKeyStates dragModifiers = DragDropKeyStates.None;
    public DragDropKeyStates DragModifiers
    {
        get => dragModifiers;
        set => Set(ref dragModifiers, value);
    }

    private Cursor cursor = Cursors.Arrow;
    public Cursor MainCursor
    {
        get => cursor;
        set
        {
            if (Set(ref cursor, value))
                UiChangeRequested?.Invoke(RuntimeUiChange.Cursor);
        }
    }

    private float dpiScalingFactor = 1.0f;
    public float DpiScalingFactor
    {
        get => dpiScalingFactor;
        set => Set(ref dpiScalingFactor, value);
    }

    private bool dragWithinSlave = false;
    public bool DragWithinSlave
    {
        get => dragWithinSlave;
        set => Set(ref dragWithinSlave, value);
    }

    private List<string> savedLocations = null;
    public List<string> SavedLocations
    {
        get
        {
            if (savedLocations is null)
            {
                var storage = Storage.RetrieveValue(nameof(SavedLocations));
                if (storage is not null && storage is string[] locations)
                {
                    savedLocations = [.. locations];
                }
            }
            return savedLocations;
        }

        set => Set(ref savedLocations, value);
    }

    public string DefaultBrowserPath { get; set; }

    public string AdbPath { get; set; }

    public int AdbServerPort { get; set; }

    public IPEndPoint AdbServerEndPoint => new(IPAddress.Loopback, AdbServerPort);

    private string tempDragPath = null;
    private readonly object tempDragPathLock = new();
    public string ResetTempDragPath()
    {
        lock (tempDragPathLock)
        {
            tempDragPath = Path.Combine(App.AppDataPath, $"drag-{Guid.NewGuid():N}");
            return tempDragPath;
        }
    }

    public bool IsAppDeployed => string.Equals(
        Environment.CurrentDirectory,
        @"C:\Windows\System32",
        StringComparison.OrdinalIgnoreCase);

    public bool IsWin11 =>
#if DEBUG
        false;
#else
        Environment.OSVersion.Version >= AdbExplorerConst.WIN11_VERSION;
#endif


    public bool Is22H2 => Environment.OSVersion.Version >= AdbExplorerConst.WIN11_22H2;

    public bool HideForceFluent => !IsWin11;

    public bool UseFluentStyles => IsWin11 || App.Settings.ForceFluentStyles;

    public bool IsRTL => Thread.CurrentThread.CurrentUICulture.TextInfo.IsRightToLeft;

}
