using ADB_Explorer.Models;
using ADB_Explorer.Resources;
using ADB_Explorer.Services;

namespace ADB_Explorer.Helpers;

public static class SettingsHelper
{
    public static void DisableAnimationTipAction() =>
        DialogService.ShowMessage(Strings.Resources.S_DISABLE_ANIMATION, Strings.Resources.S_ANIMATION_TITLE, DialogService.DialogIcon.Tip);

    public static void ResetAppAction()
    {
        Process.Start(Environment.ProcessPath);
        Application.Current.Shutdown();
    }

    public static void ChangeDefaultPathAction()
    {
        var dialog = new CommonOpenFileDialog()
        {
            IsFolderPicker = true,
            Multiselect = false
        };
        if (App.Settings.DefaultFolder != "")
            dialog.DefaultDirectory = App.Settings.DefaultFolder;

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            App.Settings.DefaultFolder = dialog.FileName;
        }
    }

    public static async void ChangeAdbPathAction()
    {
        var dialog = new OpenFileDialog()
        {
            Multiselect = false,
            Title = Strings.Resources.S_OVERRIDE_ADB_BROWSE,
            Filter = $"{Strings.Resources.S_ADB_EXECUTABLE}|adb.exe",
        };

        if (!string.IsNullOrEmpty(App.Settings.ManualAdbPath))
        {
            try
            {
                var dir = Directory.GetParent(App.Settings.ManualAdbPath);

                if (dir.Exists)
                    dialog.InitialDirectory = dir.FullName;
            }
            catch (Exception) { }
        }

        if (dialog.ShowDialog() == true)
        {
            string message = "";
            await ADBService.VerifyAdbVersionAsync(dialog.FileName);
            if (App.RuntimeSettings.AdbVersion is null)
            {
                message = Strings.Resources.S_MISSING_ADB_OVERRIDE;
            }
            else if (App.RuntimeSettings.AdbVersion < AdbExplorerConst.MIN_ADB_VERSION)
            {
                message = Strings.Resources.S_ADB_VERSION_LOW_OVERRIDE;
            }

            if (message != "")
            {
                DialogService.ShowMessage(message, Strings.Resources.S_FAIL_OVERRIDE_TITLE, DialogService.DialogIcon.Exclamation, copyToClipboard: true);
                return;
            }

            App.Settings.ManualAdbPath = dialog.FileName;
        }
    }

    public static void SetSymbolFont()
    {
        Application.Current.Resources["SymbolThemeFontFamily"] = App.Current.FindResource(App.RuntimeSettings.UseFluentStyles ? "FluentSymbolThemeFontFamily" : "AltSymbolThemeFontFamily");
    }

    public static async Task<IReadOnlyList<Notification>> GetNotificationsAsync()
    {
        List<Notification> notifications = [];
        if (App.Settings.OriginalCulture is null || App.Settings.OriginalCulture.Name != "en-US")
        {
            notifications.Add(new(async () =>
            {
                var res = await DialogService.ShowConfirmation(Strings.Resources.S_LANG_NOTIFICATION,
                    Strings.Resources.S_LANG_NOTIFICATION_TITLE,
                    Strings.Resources.S_GOTO_WEBLATE,
                    cancelText: Strings.Resources.S_BUTTON_CLOSE,
                    icon: DialogService.DialogIcon.Informational);

                if (res.Item1 is ContentDialogResult.Primary)
                    Process.Start(App.RuntimeSettings.DefaultBrowserPath, $"\"{Links.WEBLATE}\"");

                App.Settings.ShowLanguageNotification = false;
            }, Strings.Resources.S_LANG_NOTIFICATION_TITLE));
        }

        if (new Version(Properties.AppGlobal.AppVersion) > new Version(App.Settings.LastVersion))
        {
            notifications.Add(new(async () =>
            {
                var res = await DialogService.ShowConfirmation(
                    Strings.Resources.S_NEW_VERSION_MSG,
                    Strings.Resources.S_NEW_VERSION_TITLE,
                    Strings.Resources.S_GO_TO_RELEASE_NOTES,
                    cancelText: Strings.Resources.S_BUTTON_CLOSE);

                if (res.Item1 is ContentDialogResult.Primary)
                    Process.Start(App.RuntimeSettings.DefaultBrowserPath, $"\"https://github.com/Alex4SSB/ADB-Explorer/releases/tag/v{Properties.AppGlobal.AppVersion}\"");

                App.Settings.LastVersion = Properties.AppGlobal.AppVersion;
            }, Strings.Resources.S_NEW_VERSION_TITLE));
        }

        if (!App.RuntimeSettings.IsAppDeployed && App.Settings.CheckForUpdates)
        {
            var latestVersion = await Network.LatestAppReleaseAsync();
            if (latestVersion is null || latestVersion <= App.AppVersion)
                return notifications;

            notifications.Add(new(async () =>
            {
                var res = await DialogService.ShowConfirmation(string.Format(Strings.Resources.S_NEW_VERSION, Properties.AppGlobal.AppDisplayName, latestVersion),
                    Strings.Resources.S_NEW_VERSION_TITLE,
                    Strings.Resources.S_GO_TO_VERSION_PAGE,
                    cancelText: Strings.Resources.S_BUTTON_CLOSE,
                    icon: DialogService.DialogIcon.Informational);

                if (res.Item1 is ContentDialogResult.Primary)
                    Process.Start(App.RuntimeSettings.DefaultBrowserPath, $"\"https://github.com/Alex4SSB/ADB-Explorer/releases/tag/v{latestVersion}\"");
            }, Strings.Resources.S_NEW_VERSION_TITLE));
        }

        return notifications;
    }

    public static void ShowAndroidRobotLicense()
    {
        SimpleStackPanel stack = new()
        {
            Spacing = 8,
            Children =
            {
                new TextBlock()
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = Strings.Resources.S_ANDROID_ROBOT_LIC,
                },
                new TextBlock()
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = Strings.Resources.S_APK_ICON_LIC,
                },
                new HyperlinkButton()
                {
                    Content = Strings.Resources.S_CC_NAME,
                    ToolTip = Links.L_CC_LIC,
                    NavigateUri = Links.L_CC_LIC,
                    HorizontalAlignment = HorizontalAlignment.Center,
                }
            },
        };

        DialogService.ShowDialog(stack, Strings.Resources.S_ANDROID_ICONS_TITLE, DialogService.DialogIcon.Informational);
    }

    public static IEnumerable<CultureInfo> GetAvailableLanguages()
    {
        string assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        yield return CultureInfo.InvariantCulture;
        yield return new CultureInfo("en-US");

        foreach (var dir in Directory.GetDirectories(AppDomain.CurrentDomain.BaseDirectory))
        {
            string folderName = Path.GetFileName(dir);
            CultureInfo culture = null;
            string resourceAssembly = "";

            try
            {
                // Attempt to create a CultureInfo from folder name
                culture = new(folderName);

                // Check if satellite assembly exists for this culture
                resourceAssembly = Path.Combine(dir, $"{assemblyName}.resources.dll");
                
            }
            catch (CultureNotFoundException)
            {
                // Folder name is not a valid culture
                continue;
            }

            if (File.Exists(resourceAssembly))
            {
                yield return culture;
            }
        }
    }

    public static double GetCurrentPercentageTranslated(CultureInfo currentCulture)
    {
        var neutralCulture = CultureInfo.InvariantCulture;

        var resourceManager = Strings.Resources.ResourceManager;
        var resourceType = typeof(Strings.Resources);
        var propertyInfos = resourceType.GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        var stringProps = propertyInfos.Where(p => p.PropertyType == typeof(string));
        var neutralValues = stringProps.Select(p => resourceManager.GetString(p.Name, neutralCulture)).Where(s => !s.All(c => char.IsAsciiLetterUpper(c)));
        var currentValues = stringProps.Select(p => resourceManager.GetString(p.Name, currentCulture));
        double translated = neutralValues.Except(currentValues).Count();
        double total = neutralValues.Count();

        return translated / total;
    }
}
