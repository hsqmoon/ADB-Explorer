using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services;

namespace ADB_Explorer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static string SettingsFilePath;
    private static string AppRootPath => AppContext.BaseDirectory;
    private static string ConfigDirectoryPath => Path.Combine(AppRootPath, "config");
    private static string LogDirectoryPath => Path.Combine(AppRootPath, "log");
    private static string CrashLogPath => Path.Combine(LogDirectoryPath, "crash.log");
    private static readonly JsonSerializerSettings JsonSettings = new() { TypeNameHandling = TypeNameHandling.None };

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
        // Read to force it to be set to Windows' culture
        _ = Data.Settings.OriginalCulture;

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // Runtime app data used for transient cleanup only.
        Data.AppDataPath = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "AppData", "Local", AdbExplorerConst.APP_DATA_FOLDER);

        if (e.Args.Length > 0)
        {
            // verify that the provided path is valid - it should not exist as a directory, but its parent directory should exist
            if (!Directory.Exists(FileHelper.GetParentPath(e.Args[0])) || Directory.Exists(e.Args[0]))
            {
                MessageBox.Show($"{Strings.Resources.S_PATH_INVALID}\n\n{e.Args[0]}", Strings.Resources.S_CUSTOM_DATA_PATH, MessageBoxButton.OK, MessageBoxImage.Error);
                
                Current.Shutdown(1);
                return;
            }

            SettingsFilePath = Path.GetFullPath(e.Args[0]);
        }
        else
            SettingsFilePath = Path.Combine(ConfigDirectoryPath, AdbExplorerConst.APP_SETTINGS_FILE);
        
        try
        {
            // if settings file exists in local app data - try to read it from there, otherwise try to read it from the isolated storage (old method)
            if (File.Exists(SettingsFilePath))
            {
                using StreamReader appDataReader = new(SettingsFilePath);
                ReadSettingsFile(appDataReader);
            }
            else
            {
                EnsurePersistenceDirectories();

                using IsolatedStorageFileStream stream = new(AdbExplorerConst.APP_SETTINGS_FILE,
                                                             FileMode.Open,
                                                             IsolatedStorageFile.GetUserStoreForDomain());
                using StreamReader reader = new(stream);
                ReadSettingsFile(reader);
            }

            if (!Data.Settings.UICulture.Equals(CultureInfo.InvariantCulture))
            {
                Thread.CurrentThread.CurrentUICulture =
                Thread.CurrentThread.CurrentCulture = Data.Settings.UICulture;
            }
            
#if !DEPLOY
            DebugLog.Initialize();
#endif

        }
        catch
        {
            // in any case of failing to read the settings, try to write them instead
            // will happen on first ever launch, or after resetting app settings

            WriteSettings();
        }

        // Complete the one-time emoji and font parsing before the main window becomes visible.
        _ = Emoji.Wpf.EmojiData.AllGroups.Count;

        //Select the text in a TextBox when it receives focus.
        EventManager.RegisterClassHandler(typeof(TextBox), TextBox.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(SelectivelyIgnoreMouseButton));
        EventManager.RegisterClassHandler(typeof(TextBox), TextBox.GotKeyboardFocusEvent,
            new RoutedEventHandler(SelectAllText));
        EventManager.RegisterClassHandler(typeof(TextBox), TextBox.MouseDoubleClickEvent,
            new RoutedEventHandler(SelectAllText));


        void ReadSettingsFile(StreamReader reader)
        {
            while (!reader.EndOfStream)
            {
                string[] keyValue = reader.ReadLine().TrimEnd(';').Split(':', 2);
                try
                {
                    var jObj = JsonConvert.DeserializeObject(keyValue[1], JsonSettings);
                    Properties[keyValue[0]] = jObj;
                }
                catch (Exception)
                {
                    Properties[keyValue[0]] = keyValue[1];
                }
            }
        }

        ScheduleDragCleanup();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        Data.FileOpQ.Stop();
        WriteSettings();

        if (Data.Settings.UnrootOnDisconnect is true)
            ADBService.Unroot(Data.CurrentADBDevice);

        ScheduleDragCleanup();
    }

    private static void ScheduleDragCleanup()
    {
        _ = Task.Run(() =>
        {
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(Data.AppDataPath, "drag-*");
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
        if (Data.RuntimeSettings.ResetAppSettings)
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

    private static void EnsurePersistenceDirectories()
    {
        Directory.CreateDirectory(ConfigDirectoryPath);
        Directory.CreateDirectory(LogDirectoryPath);
    }
}
