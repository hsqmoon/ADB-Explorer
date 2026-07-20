using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const int MAX_COMMAND_LOG_ENTRIES = 2000;
    private static string SettingsFilePath;
    private static string AppRootPath => AppContext.BaseDirectory;
    private static string ConfigDirectoryPath => Path.Combine(AppRootPath, "config");
    private static string LogDirectoryPath => Path.Combine(AppRootPath, "log");
    private static string CrashLogPath => Path.Combine(LogDirectoryPath, "crash.log");
    private static readonly JsonSerializerSettings JsonSettings = new() { TypeNameHandling = TypeNameHandling.None };
    private static readonly object commandLogLock = new();
    private static readonly Queue<Log> pendingCommandLogs = new();
    private static readonly AppSettings settings = new();
    private static readonly AppRuntimeSettings runtimeSettings = new(settings);
    private static readonly CopyPasteService copyPaste = new();
    private static readonly ObservableCollection<Log> commandLog = [];
    private static readonly FileActionsEnable fileActions = new();
    private static readonly MDNS mdnsService = new();
    private AppRuntime appRuntime;
    private Task<SettingsLoadResult> settingsLoadTask = Task.FromResult(
        new SettingsLoadResult(new Dictionary<string, object>(), null));
    private int settingsInitialized;
    private long startupTimestamp;

    public static AppSettings Settings => settings;
    public static AppRuntimeSettings RuntimeSettings => runtimeSettings;
    public static CopyPasteService CopyPaste => copyPaste;
    internal static ObservableCollection<Log> CommandLog => commandLog;
    public static FileActionsEnable FileActions => fileActions;
    public static MDNS MdnsService => mdnsService;
    public static PairingQrClass QrClass { get; internal set; }
    public static Version AppVersion => new(ADB_Explorer.Properties.AppGlobal.AppVersion);
    public static string AppDataPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AdbExplorerConst.APP_DATA_FOLDER);

    public App()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windowsDirectory))
                Environment.SetEnvironmentVariable("windir", windowsDirectory, EnvironmentVariableTarget.Process);
        }
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        startupTimestamp = Stopwatch.GetTimestamp();
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        string settingsPathError = null;
        bool customSettingsPath = e.Args.Length > 0;
        if (customSettingsPath)
        {
            try
            {
                SettingsFilePath = Path.GetFullPath(e.Args[0]);
            }
            catch (Exception ex)
            {
                settingsPathError = ex.Message;
                SettingsFilePath = Path.Combine(ConfigDirectoryPath, AdbExplorerConst.APP_SETTINGS_FILE);
            }
        }
        else
            SettingsFilePath = Path.Combine(ConfigDirectoryPath, AdbExplorerConst.APP_SETTINGS_FILE);

        settingsLoadTask = settingsPathError is null
            ? Task.Run(() => LoadSettings(SettingsFilePath, customSettingsPath))
            : Task.FromResult(new SettingsLoadResult(
                new Dictionary<string, object>(),
                settingsPathError));

        //Select the text in a TextBox when it receives focus.
        EventManager.RegisterClassHandler(typeof(TextBox), TextBox.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(SelectivelyIgnoreMouseButton));
        EventManager.RegisterClassHandler(typeof(TextBox), TextBox.GotKeyboardFocusEvent,
            new RoutedEventHandler(SelectAllText));
        EventManager.RegisterClassHandler(typeof(TextBox), TextBox.MouseDoubleClickEvent,
            new RoutedEventHandler(SelectAllText));

        ScheduleDragCleanup();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        _ = InitializeApplicationAsync(mainWindow);
    }

    private async Task InitializeApplicationAsync(MainWindow mainWindow)
    {
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Input);
            if (mainWindow.Dispatcher.HasShutdownStarted)
                return;

            var settings = await settingsLoadTask.ConfigureAwait(true);
            if (mainWindow.Dispatcher.HasShutdownStarted)
                return;

            if (settings.Error is not null)
            {
                MessageBox.Show(
                    $"{Strings.Resources.S_PATH_INVALID}\n\n{settings.Error}",
                    Strings.Resources.S_CUSTOM_DATA_PATH,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            foreach (var item in settings.Values)
                Properties[item.Key] = item.Value;

            Volatile.Write(ref settingsInitialized, 1);
            PerformanceTrace.Log.StartupStage(
                "startup.settings",
                Stopwatch.GetElapsedTime(startupTimestamp).Ticks / 10);

            if (!App.Settings.UICulture.Equals(CultureInfo.InvariantCulture))
            {
                Thread.CurrentThread.CurrentUICulture =
                Thread.CurrentThread.CurrentCulture = App.Settings.UICulture;
            }

#if DEBUG
            await Task.Run(() => DebugLog.Initialize()).ConfigureAwait(true);
#endif

            var settingsModelTask = Task.Run(UISettings.Init);
            var notificationsTask = SettingsHelper.GetNotificationsAsync();
            mainWindow.InitializeTheme();
            mainWindow.InitializeFont();
            mainWindow.InitializeRenderMode();

            long viewStart = Stopwatch.GetTimestamp();
            mainWindow.InitializeView();
            var viewDuration = Stopwatch.GetElapsedTime(viewStart);
            PerformanceTrace.Log.StartupStage(
                "startup.view",
                Stopwatch.GetElapsedTime(startupTimestamp).Ticks / 10);
            PerformanceTrace.Log.StartupStage("startup.view-build", viewDuration.Ticks / 10);

            await settingsModelTask.ConfigureAwait(true);
            if (mainWindow.Dispatcher.HasShutdownStarted)
                return;

            mainWindow.InitializeSettingsSources();
            mainWindow.InitializeToolbars();
            mainWindow.InitializeSettingsState();
            mainWindow.InitializeFileOperationModel();
            mainWindow.InitializeFileOperationColumns();
            mainWindow.InitializeFileOperationFilter();
            mainWindow.ActivateRuntime();

            long firstFrameStart = Stopwatch.GetTimestamp();
            mainWindow.Show();
            appRuntime?.NotifyUiReady();
            PerformanceTrace.Log.StartupStage(
                "startup.first-frame",
                Stopwatch.GetElapsedTime(startupTimestamp).Ticks / 10);
            var firstFrameDuration = Stopwatch.GetElapsedTime(firstFrameStart);
            PerformanceTrace.Log.StartupStage("startup.window-show", firstFrameDuration.Ticks / 10);

            mainWindow.ShowAuxiliaryWindows();
            _ = PublishNotificationsAsync(notificationsTask);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex, "startup.initialize-window");
            MessageBox.Show(ex.Message, "ADB Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task PublishNotificationsAsync(Task<IReadOnlyList<Notification>> notificationsTask)
    {
        try
        {
            var notifications = await notificationsTask.ConfigureAwait(false);
            foreach (var notification in notifications)
            {
                await EnqueueUiAsync(
                    "startup.notification",
                    () => UISettings.Notifications.Add(notification)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            ReportBackgroundFailure(ex, "startup.notifications");
        }
    }

    private static SettingsLoadResult LoadSettings(string settingsFilePath, bool customSettingsPath)
    {
        if (customSettingsPath
            && (!Directory.Exists(FileHelper.GetParentPath(settingsFilePath))
                || Directory.Exists(settingsFilePath)))
        {
            return new(new Dictionary<string, object>(), settingsFilePath);
        }

        try
        {
            if (File.Exists(settingsFilePath))
            {
                using StreamReader appDataReader = new(settingsFilePath);
                return new(ReadSettingsFile(appDataReader), null);
            }

            EnsurePersistenceDirectories();
            using IsolatedStorageFileStream stream = new(
                AdbExplorerConst.APP_SETTINGS_FILE,
                FileMode.Open,
                IsolatedStorageFile.GetUserStoreForDomain());
            using StreamReader reader = new(stream);
            return new(ReadSettingsFile(reader), null);
        }
        catch
        {
            return new(new Dictionary<string, object>(), null);
        }
    }

    private static Dictionary<string, object> ReadSettingsFile(StreamReader reader)
    {
        Dictionary<string, object> values = [];
        while (reader.ReadLine() is string line)
        {
            string[] keyValue = line.TrimEnd(';').Split(':', 2);
            if (keyValue.Length != 2 || string.IsNullOrWhiteSpace(keyValue[0]))
                continue;

            try
            {
                values[keyValue[0]] = JsonConvert.DeserializeObject(keyValue[1], JsonSettings);
            }
            catch (JsonException)
            {
                values[keyValue[0]] = keyValue[1];
            }
        }

        return values;
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        appRuntime?.FileOperations.Stop();
        StopRuntime();
        if (Volatile.Read(ref settingsInitialized) != 0)
            WriteSettings();

        if (Volatile.Read(ref settingsInitialized) != 0
            && App.Settings.UnrootOnDisconnect is true)
            ADBService.Unroot(ActiveAdbDevice);

        ScheduleDragCleanup();
    }

    internal IUiWorkScheduler AttachRuntime(MainWindow mainWindow)
    {
        if (appRuntime is not null)
            throw new InvalidOperationException("The application runtime is already attached.");

        appRuntime = new(mainWindow);
        return appRuntime.UiScheduler;
    }

    internal void StartRuntime() => appRuntime?.Start();

    internal void RefreshDevices() => appRuntime?.RefreshDevices();

    internal Task RestartAdbServerAsync() =>
        appRuntime?.RestartAdbServerAsync() ?? Task.CompletedTask;

    internal Task KillAdbProcessAsync() =>
        appRuntime?.KillAdbProcessAsync() ?? Task.CompletedTask;

    internal void LoadHistoryDevices() => appRuntime?.LoadHistoryDevices();

    internal void RequestUi(UiCommand command) => appRuntime?.RequestUi(command);

    internal void RequestNavigation(AdbLocation location) => appRuntime?.RequestNavigation(location);

    internal void RequestPathNavigation(string path) => appRuntime?.RequestPathNavigation(path);

    internal void RequestDriveNavigation(DriveViewModel drive) => appRuntime?.RequestDriveNavigation(drive);

    internal void RequestFileNavigation(FileClass file) => appRuntime?.RequestFileNavigation(file);

    internal bool ConsumeBackForwardNavigation() => appRuntime?.ConsumeBackForwardNavigation() is true;

    internal DirectorySession CurrentDirectorySession => appRuntime?.CurrentDirectorySession;

    internal static DirectorySession ActiveDirectorySession =>
        (Current as App)?.CurrentDirectorySession;

    public static FileOperationQueue ActiveFileOperations =>
        (Current as App)?.appRuntime?.FileOperations;

    public static Devices ActiveDevices =>
        (Current as App)?.appRuntime?.Devices;

    public static ADBService.AdbDevice ActiveAdbDevice
    {
        get => (Current as App)?.appRuntime?.CurrentAdbDevice;
        internal set
        {
            if ((Current as App)?.appRuntime is AppRuntime runtime)
                runtime.CurrentAdbDevice = value;
        }
    }

    public static ExplorerState ExplorerState =>
        (Current as App)?.appRuntime?.ExplorerState;

    internal void CloseDirectorySession() => appRuntime?.CloseDirectorySession();

    internal void FollowLink(string target) => appRuntime?.FollowLink(target);

    internal void SetExplorerSource(System.Collections.IEnumerable source) => appRuntime?.SetExplorerSource(source);

    internal void SelectExplorerItem(object item) => appRuntime?.SelectExplorerItem(item);

    internal void SelectExplorerItems(IEnumerable<FileClass> items, DirectorySession expectedSession) =>
        appRuntime?.SelectExplorerItems(items, expectedSession);

    internal void RefreshPackages() => appRuntime?.RefreshPackages();

    internal Task<IReadOnlyList<BitmapSource>> GetFileIconsAsync(
        string fileName,
        AbstractFile.SpecialFileType specialType,
        CancellationToken cancellationToken = default) =>
        appRuntime?.ShellIcons.GetIconsAsync(fileName, specialType, cancellationToken)
        ?? Task.FromResult<IReadOnlyList<BitmapSource>>([]);

    internal Task<BitmapSource> GetPreviewIconAsync(
        string fileName,
        AbstractFile.SpecialFileType specialType,
        int iconSize,
        CancellationToken cancellationToken = default) =>
        appRuntime?.ShellIcons.GetPreviewIconAsync(fileName, specialType, iconSize, cancellationToken)
        ?? Task.FromResult<BitmapSource>(null);

    internal void EnqueueUiLatest(string key, string workName, Action action) =>
        appRuntime?.UiScheduler.EnqueueLatest(key, workName, action);

    internal ValueTask EnqueueUiAsync(
        string workName,
        Action action,
        CancellationToken cancellationToken = default) =>
        appRuntime?.UiScheduler.EnqueueAsync(workName, action, cancellationToken) ?? ValueTask.CompletedTask;

    internal Task<ClipboardSnapshot> ReadClipboardAsync(
        string currentDeviceId,
        CancellationToken cancellationToken = default) =>
        appRuntime?.ReadClipboardAsync(currentDeviceId, cancellationToken)
        ?? Task.FromResult(ClipboardSnapshot.Empty);

    internal Task SetClipboardAsync(
        VirtualFileDataObject dataObject,
        CancellationToken cancellationToken = default) =>
        appRuntime?.SetClipboardAsync(dataObject, cancellationToken) ?? Task.CompletedTask;

    internal Task ClearClipboardAsync(
        bool clearSystemClipboard,
        CancellationToken cancellationToken = default) =>
        appRuntime?.ClearClipboardAsync(clearSystemClipboard, cancellationToken) ?? Task.CompletedTask;

    internal Task<bool> SetClipboardTextAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        appRuntime?.SetClipboardTextAsync(text, cancellationToken) ?? Task.FromResult(false);

    internal Task<string> ReadClipboardTextAsync(CancellationToken cancellationToken = default) =>
        appRuntime?.ReadClipboardTextAsync(cancellationToken) ?? Task.FromResult("");

    internal Task<DropSnapshot> ReadDropAsync(
        IDataObject dataObject,
        string currentDeviceId,
        CancellationToken cancellationToken = default) =>
        appRuntime?.ReadDropAsync(dataObject, currentDeviceId, cancellationToken)
        ?? Task.FromResult(DropSnapshot.Empty);

    internal Task<ShellMaterializationSnapshot> MaterializeDropAsync(
        IDataObject dataObject,
        FileDescriptor[] descriptors,
        bool hasShellIdList,
        bool hasFileContents,
        string targetDirectory,
        CancellationToken cancellationToken = default) =>
        appRuntime?.MaterializeDropAsync(
            dataObject,
            descriptors,
            hasShellIdList,
            hasFileContents,
            targetDirectory,
            cancellationToken)
        ?? Task.FromResult(ShellMaterializationSnapshot.Empty);

    internal Task<ShellMaterializationSnapshot> MaterializeClipboardAsync(
        FileDescriptor[] descriptors,
        bool hasShellIdList,
        bool hasFileContents,
        string targetDirectory,
        CancellationToken cancellationToken = default) =>
        appRuntime?.MaterializeClipboardAsync(
            descriptors,
            hasShellIdList,
            hasFileContents,
            targetDirectory,
            cancellationToken)
        ?? Task.FromResult(ShellMaterializationSnapshot.Empty);

    internal void StopRuntime()
    {
        appRuntime?.Dispose();
        appRuntime = null;
    }

    private static void ScheduleDragCleanup()
    {
        _ = Task.Run(() =>
        {
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(App.AppDataPath, "drag-*");
            }
            catch
            {
                return;
            }

            var staleBefore = DateTime.UtcNow.AddDays(-1);
            directories.ForEach(dir =>
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) >= staleBefore)
                        return;

                    Directory.Delete(dir, true);
                }
                catch
                { }
            });
        });
    }

    private void WriteSettings()
    {
        if (App.RuntimeSettings.ResetAppSettings)
        {
            try
            {
                File.Delete(SettingsFilePath);
            }
            catch
            { }
            
            return;
        }

        try
        {
            var settingsDirectory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
                Directory.CreateDirectory(settingsDirectory);

            using StreamWriter writer = new(SettingsFilePath);

            foreach (string key in from string key in Properties.Keys
                                   orderby key
                                   select key)
            {
                writer.WriteLine($"{key}:{JsonConvert.SerializeObject(Properties[key], JsonSettings)};");
            }
        }
        catch (Exception)
        { }
    }

    private void SelectivelyIgnoreMouseButton(object sender, MouseButtonEventArgs e)
    {
        // Find the TextBox
        DependencyObject parent = e.OriginalSource as UIElement;
        while (parent is not null and not TextBox)
            parent = VisualTreeHelper.GetParent(parent);

        if (parent is not null)
        {
            var textBox = (TextBox)parent;
            if (!textBox.IsKeyboardFocusWithin)
            {
                // If the text box is not yet focused, give it the focus and
                // stop further processing of this click event.
                textBox.Focus();
                e.Handled = true;
            }
        }
    }

    private void SelectAllText(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            tb.SelectAll();
    }

    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Handle error 0x800401D0 (CLIPBRD_E_CANT_OPEN) - global WPF issue
        if (e.Exception is COMException comException && comException.ErrorCode == -2147221040)
        {
            e.Handled = true;
            return;
        }

        WriteCrashLog(e.Exception, nameof(Application_DispatcherUnhandledException));

        // If application shutdown has started, do not throw exceptions
        if (App.Current is null || App.Current.Dispatcher is null)
            e.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            WriteCrashLog(ex, nameof(CurrentDomain_UnhandledException));
        else
            WriteCrashLog(new Exception(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception"), nameof(CurrentDomain_UnhandledException));
    }

    private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, nameof(TaskScheduler_UnobservedTaskException));
        e.SetObserved();
    }

    private static void WriteCrashLog(Exception exception, string source)
    {
        try
        {
            EnsurePersistenceDirectories();

            StringBuilder message = new();
            message.AppendLine(new string('=', 80));
            message.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            message.AppendLine($"Source: {source}");
            message.AppendLine($"Exception: {exception.GetType().FullName}");
            message.AppendLine($"Message: {exception.Message}");
            message.AppendLine($"WorkingSetMB: {Environment.WorkingSet / 1024 / 1024}");
            message.AppendLine($"ManagedHeapMB: {GC.GetTotalMemory(false) / 1024 / 1024}");
            message.AppendLine(exception.ToString());
            message.AppendLine();

            File.AppendAllText(CrashLogPath, message.ToString());
        }
        catch
        { }
    }

    internal static void ReportBackgroundFailure(Exception exception, string source) => WriteCrashLog(exception, source);

    internal static void AddCommandLog(string content)
    {
        if (!Settings.EnableLog || RuntimeSettings.IsLogPaused)
            return;

        var log = new Log(content);
        lock (commandLogLock)
        {
            while (pendingCommandLogs.Count >= MAX_COMMAND_LOG_ENTRIES)
                pendingCommandLogs.Dequeue();

            pendingCommandLogs.Enqueue(log);
        }

        if (Current is App app)
            app.EnqueueUiLatest("command-log.collection", "command-log.collection", FlushPendingCommandLogs);
    }

    private static void FlushPendingCommandLogs()
    {
        List<Log> logsToAdd = [];
        lock (commandLogLock)
        {
            while (pendingCommandLogs.Count > 0 && logsToAdd.Count < 8)
                logsToAdd.Add(pendingCommandLogs.Dequeue());
        }

        foreach (var log in logsToAdd)
            CommandLog.Add(log);

        int overflow = CommandLog.Count - MAX_COMMAND_LOG_ENTRIES;
        for (int index = 0; index < overflow; index++)
            CommandLog.RemoveAt(0);

        lock (commandLogLock)
        {
            if (pendingCommandLogs.Count > 0 && Current is App app)
                app.EnqueueUiLatest("command-log.collection", "command-log.collection", FlushPendingCommandLogs);
        }
    }

    private sealed record SettingsLoadResult(
        IReadOnlyDictionary<string, object> Values,
        string Error);

    private static void EnsurePersistenceDirectories()
    {
        Directory.CreateDirectory(ConfigDirectoryPath);
        Directory.CreateDirectory(LogDirectoryPath);
    }
}
