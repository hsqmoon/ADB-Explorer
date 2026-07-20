using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

internal class ThemeService : ViewModelBase, IDisposable
{
    //https://medium.com/southworks/handling-dark-light-modes-in-wpf-3f89c8a4f2db

    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string RegistryValueName = "AppsUseLightTheme";
    private const string QueryPrefix = "SELECT * FROM RegistryValueChangeEvent WHERE Hive = 'HKEY_USERS' AND KeyPath";
    private readonly object watcherLock = new();
    private static ResourceDictionary themeBrushes;
    private static ApplicationTheme? appliedTheme;
    private ManagementEventWatcher watcher;
    private int watchStarted;
    private int disposed;

    private AppSettings.AppTheme? windowsTheme;
    public AppSettings.AppTheme WindowsTheme
    {
        get
        {
            if (windowsTheme is null)
                windowsTheme = GetWindowsTheme();

            return windowsTheme.Value;
        }
        set => Set(ref windowsTheme, value);
    }

    public Task StartWatchingAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref watchStarted, 1) == 1)
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowsTheme = GetWindowsTheme();

            using var currentUser = WindowsIdentity.GetCurrent();
            string query = $@"{QueryPrefix} = '{currentUser.User.Value}\\{RegistryKeyPath.Replace(@"\", @"\\")}' AND ValueName = '{RegistryValueName}'";
            var newWatcher = new ManagementEventWatcher(query);
            newWatcher.EventArrived += ThemeChanged;
            newWatcher.Start();

            lock (watcherLock)
            {
                if (Volatile.Read(ref disposed) == 1)
                {
                    newWatcher.EventArrived -= ThemeChanged;
                    try
                    {
                        newWatcher.Stop();
                    }
                    catch (ManagementException)
                    { }
                    finally
                    {
                        newWatcher.Dispose();
                    }
                    return;
                }

                watcher = newWatcher;
            }
        }, cancellationToken);
    }

    private void ThemeChanged(object sender, EventArrivedEventArgs e)
    {
        if (Volatile.Read(ref disposed) == 0)
            WindowsTheme = GetWindowsTheme();
    }

    private static AppSettings.AppTheme GetWindowsTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
        return key?.GetValue(RegistryValueName) is < 1 ? AppSettings.AppTheme.dark : AppSettings.AppTheme.light;
    }

    public ApplicationTheme AppThemeToActual(AppSettings.AppTheme appTheme) => appTheme switch
    {
        AppSettings.AppTheme.light => ApplicationTheme.Light,
        AppSettings.AppTheme.dark => ApplicationTheme.Dark,
        AppSettings.AppTheme.windowsDefault => AppThemeToActual(WindowsTheme),
        _ => throw new NotSupportedException(),
    };

    public void SetTheme(AppSettings.AppTheme theme) => SetTheme(AppThemeToActual(theme));

    public static void SetTheme(ApplicationTheme theme)
    {
        if (appliedTheme == theme && themeBrushes is not null)
            return;

        ThemeManager.Current.ApplicationTheme = theme;

        var resources = Application.Current.Resources;
        var source = (ResourceDictionary)resources["DynamicBrushes"];
        ResourceDictionary replacement = [];
        foreach (string resource in source.Keys)
        {
            var brush = new SolidColorBrush((Color)resources[$"{theme}{resource}"]);
            brush.Freeze();
            replacement[resource] = brush;
        }

        int index = themeBrushes is null
            ? -1
            : resources.MergedDictionaries.IndexOf(themeBrushes);
        if (index < 0)
            resources.MergedDictionaries.Add(replacement);
        else
            resources.MergedDictionaries[index] = replacement;

        themeBrushes = replacement;
        appliedTheme = theme;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return;

        lock (watcherLock)
        {
            if (watcher is null)
                return;

            watcher.EventArrived -= ThemeChanged;
            try
            {
                watcher.Stop();
            }
            catch (ManagementException)
            { }
            finally
            {
                watcher.Dispose();
            }
            watcher = null;
        }
    }
}
