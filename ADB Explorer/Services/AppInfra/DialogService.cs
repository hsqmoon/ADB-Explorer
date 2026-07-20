using ADB_Explorer.Helpers;
using ADB_Explorer.Models;

namespace ADB_Explorer.Services;

public static class DialogService
{
    public enum DialogIcon
    {
        None,
        Critical,
        Exclamation,
        Informational,
        Tip,
        Delete,
    }

    private static string Icon(DialogIcon icon) => icon switch
    {
        DialogIcon.None => "",
        DialogIcon.Critical => "\uEA39",
        DialogIcon.Exclamation => "\uE783",
        DialogIcon.Informational => "\uE946",
        DialogIcon.Tip => "\uE82F",
        DialogIcon.Delete => "\uE74D",
        _ => throw new NotImplementedException(),
    };

    private static readonly ContentDialog windowDialog = new();
    private static readonly SemaphoreSlim messageGate = new(1, 1);

    public static void ShowMessage(string content, string title = "", DialogIcon icon = DialogIcon.None, bool censorContent = true, bool hidePanes = true, bool copyToClipboard = false)
    {
        if (censorContent)
            content = content.Replace(AdbExplorerConst.RECYCLE_PATH, Strings.Resources.S_DRIVE_TRASH);

        string message = content;
        if (Application.Current is App app && app.MainWindow is MainWindow)
        {
            _ = ShowMessageAsync(app, message, title, icon, hidePanes, copyToClipboard);
            return;
        }

        if (copyToClipboard)
            App.FileActions.MessageToCopy = message;
        ShowDialog(message, title, icon, hidePanes);
    }

    private static async Task ShowMessageAsync(
        App app,
        string message,
        string title,
        DialogIcon icon,
        bool hidePanes,
        bool copyToClipboard)
    {
        await messageGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (copyToClipboard)
            {
                await app.EnqueueUiAsync(
                    "dialog.message.copy-state",
                    () => App.FileActions.MessageToCopy = message).ConfigureAwait(false);
            }

            await app.EnqueueUiAsync(
                "dialog.message.title",
                () => ConfigureDialogTitle(title)).ConfigureAwait(false);
            await app.EnqueueUiAsync(
                "dialog.message.content",
                () => ConfigureDialogContent(message)).ConfigureAwait(false);
            await app.EnqueueUiAsync(
                "dialog.message.buttons",
                () => ConfigureDialogButtons(icon, Strings.Resources.S_BUTTON_OK)).ConfigureAwait(false);
            if (hidePanes)
            {
                await app.EnqueueUiAsync(
                    "dialog.message.hide-panes",
                    HidePanes).ConfigureAwait(false);
            }

            Task<ContentDialogResult> dialogTask = null;
            await app.EnqueueUiAsync(
                "dialog.message.show",
                () => dialogTask = windowDialog.ShowAsync()).ConfigureAwait(false);
            if (dialogTask is not null)
                await dialogTask.ConfigureAwait(false);

            if (copyToClipboard)
            {
                await app.EnqueueUiAsync(
                    "dialog.message.copy-clear",
                    () =>
                    {
                        if (App.FileActions.MessageToCopy == message)
                            App.FileActions.MessageToCopy = "";
                    }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "dialog.message");
        }
        finally
        {
            messageGate.Release();
        }
    }

    private static void HidePanes()
    {
        App.RuntimeSettings.IsSettingsPaneOpen = false;
        App.RuntimeSettings.IsDevicesPaneOpen = false;
    }

    public static ContentDialog ShowDialog(object content, string title, DialogIcon icon = DialogIcon.None, bool hidePanes = true, string buttonText = null)
    {
        if (buttonText is null)
            buttonText = Strings.Resources.S_BUTTON_OK;

        ConfigureDialogTitle(title);
        ConfigureDialogContent(content);
        ConfigureDialogButtons(icon, buttonText);

        if (hidePanes)
            HidePanes();

        windowDialog.ShowAsync();
        return windowDialog;
    }

    private static void ConfigureDialogTitle(string title)
    {
        windowDialog.FlowDirection = App.RuntimeSettings.IsRTL
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        windowDialog.Title = title;
    }

    private static void ConfigureDialogContent(object content) => windowDialog.Content = content;

    private static void ConfigureDialogButtons(DialogIcon icon, string buttonText)
    {
        windowDialog.PrimaryButtonText = null;
        windowDialog.CloseButtonText = buttonText;
        DialogHelper.SetDialogIcon(windowDialog, Icon(icon));
    }

    private static TextBlock DialogContentTextBlock;
    private static CheckBox DialogContentCheckbox;
    private static bool IsDialogChecked;
    private static SimpleStackPanel DialogContentStackPanel = new()
    {
        Spacing = 10
    };

    private static void InitContent(string textContent, string checkboxContent = "")
    {
        if (DialogContentTextBlock is null)
            DialogContentTextBlock = new();

        DialogContentTextBlock.Text = textContent;

        if (DialogContentCheckbox is null)
        {
            DialogContentCheckbox = new();
            DialogContentCheckbox.Checked += Checkbox_Checked;
            DialogContentCheckbox.Unchecked += Checkbox_Checked;
        }

        DialogContentCheckbox.Visibility = VisibilityHelper.Visible(!string.IsNullOrEmpty(checkboxContent));
        DialogContentCheckbox.Content = checkboxContent;
        DialogContentCheckbox.IsChecked = false;

        if (DialogContentStackPanel.Children.Count == 0)
        {
            DialogContentStackPanel.Children.Add(DialogContentTextBlock);
            DialogContentStackPanel.Children.Add(DialogContentCheckbox);
        }
    }

    public static async Task<(ContentDialogResult, bool)> ShowConfirmation(string content,
                                        string title = "",
                                        string primaryText = null,
                                        string secondaryText = "",
                                        string cancelText = null,
                                        string checkBoxText = "",
                                        DialogIcon icon = DialogIcon.None,
                                        bool censorContent = true,
                                        bool hidePanes = true)
    {
        if (primaryText is null)
            primaryText = Strings.Resources.S_BUTTON_YES;

        if (cancelText is null)
            cancelText = Strings.Resources.S_CANCEL;

        if (windowDialog.IsVisible)
        {
            return (ContentDialogResult.None, false);
        }

        if (censorContent)
        {
            content = content.Replace(AdbExplorerConst.RECYCLE_PATH, Strings.Resources.S_DRIVE_TRASH);
        }

        InitContent(content, checkBoxText);

        windowDialog.Content = DialogContentStackPanel;
        windowDialog.Title = title;
        windowDialog.PrimaryButtonText = primaryText;
        windowDialog.DefaultButton = ContentDialogButton.Primary;
        windowDialog.SecondaryButtonText = secondaryText;
        windowDialog.CloseButtonText = cancelText;
        DialogHelper.SetDialogIcon(windowDialog, Icon(icon));

        if (hidePanes)
            HidePanes();

        var result = await windowDialog.ShowAsync();

        return (result, IsDialogChecked);
    }

    private static void Checkbox_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        IsDialogChecked = DialogContentCheckbox.IsChecked.Value;
    }

    public static bool IsOpen
    {
        get => windowDialog.IsVisible;
        set => windowDialog.Hide();
    }
}
