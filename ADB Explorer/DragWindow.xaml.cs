using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services;
using System.Windows.Documents;
using static ADB_Explorer.Services.NativeMethods;

namespace ADB_Explorer;

/// <summary>
/// Interaction logic for DragWindow.xaml
/// </summary>
public partial class DragWindow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static readonly TimeSpan DRAG_TOOLTIP_UPDATE_INTERVAL = TimeSpan.FromMilliseconds(200);
    private readonly DispatcherTimer DragTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private HANDLE dragWindowHandle;

    public DragWindow()
    {
        InitializeComponent();

        DragTimer.Tick += DragTimer_Tick;

#if DEBUG
        MainBorder.BorderThickness = new Thickness(1);
        MainBorder.BorderBrush = Brushes.OrangeRed;
#endif
    }

    private void DragTimer_Tick(object sender, EventArgs e)
    {
        if (App.RuntimeSettings.DragBitmap is null)
            return;

        if (CursorInfo.IsRightButtonPressed)
        {
            CancelDrag();
            return;
        }

        if (!CursorInfo.TryGetPosition(out var mousePosition))
            return;

        UpdateMouse(mousePosition);
        if (DateTime.Now - lastTooltipUpdate >= DRAG_TOOLTIP_UPDATE_INTERVAL)
        {
            lastTooltipUpdate = DateTime.Now;
            GetPathUnderMouse();
        }
    }

    private DateTime lastTooltipUpdate;
    private readonly SolidColorBrush blueBrush = new(Colors.DodgerBlue);

    private void RuntimeSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(App.RuntimeSettings.DragBitmap))
            return;

        if (Dispatcher.CheckAccess())
            UpdateDragTimerState();
        else if (Application.Current is App app)
            app.EnqueueUiLatest("drag-window.state", "drag-window.state", UpdateDragTimerState);
    }

    private void UpdateDragTimerState()
    {
        if (App.RuntimeSettings.DragBitmap is null)
        {
            DragTimer.Stop();
            return;
        }

        lastTooltipUpdate = DateTime.MinValue;
        if (CursorInfo.TryGetPosition(out var mousePosition))
            UpdateMouse(mousePosition);
        if (App.RuntimeSettings.DragBitmap is not null)
            DragTimer.Start();
    }

    private void GetPathUnderMouse()
    {
        void updateTooltip()
        {
            DragTooltip.Inlines.Clear();
            if (App.CopyPaste.DragFiles.Length == 0 || App.CopyPaste.CurrentDropEffect is DragDropEffects.None)
            {
                return;
            }

            // Shouldn't happen. But if it does, we don't want to do anything.
            if (hwndUnderMouse == dragWindowHandle)
                return;

            string target = "";
            if (MouseWithinApp)
            {
                if (App.CopyPaste.IsSelf
                    && App.CopyPaste.DropTarget == App.CopyPaste.DragParent
                    && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                    && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    return;
                }

                target = FileHelper.GetFullName(App.CopyPaste.DropTarget);
            }

            var format = "";
            string result = "";
            var count = App.CopyPaste.DragFiles.Length;

            if (count == 1)
            {
                int sourceLength = target is null ? 30 : 45 - target.Length;
                var source = FileHelper.GetShortFileName(App.CopyPaste.DragFiles[0], sourceLength);

                if (App.FileActions.IsAppDrive && MouseWithinApp)
                {
                    result = string.Format(Strings.Resources.S_DRAG_INSTALL_SINGLE, source);
                    var apkSplit = result.Split(source);

                    DragTooltip.Inlines.Add(new Run(apkSplit[0]) { Foreground = blueBrush });
                    DragTooltip.Inlines.Add(source);
                    DragTooltip.Inlines.Add(new Run(apkSplit[1]) { Foreground = blueBrush });
                    
                    return;
                }

                if (App.CopyPaste.CurrentDropEffect is DragDropEffects.Link)
                {
                    result = string.Format(Strings.Resources.S_DRAGDROP_LINK, target);
                }
                else if (App.CopyPaste.CurrentDropEffect is DragDropEffects.Move)
                {
                    format = string.IsNullOrEmpty(target)
                        ? Strings.Resources.S_DRAGDROP_MOVE_SINGLE
                        : Strings.Resources.S_DRAGDROP_MOVE_TARGET_SINGLE;
                }
                else if (App.CopyPaste.CurrentDropEffect is DragDropEffects.Copy)
                {
                    format = string.IsNullOrEmpty(target)
                        ? Strings.Resources.S_DRAGDROP_COPY_SINGLE
                        : Strings.Resources.S_DRAGDROP_COPY_TARGET_SINGLE;
                }

                if (result == "")
                {
                    result = string.Format(format, string.IsNullOrEmpty(target)
                        ? [source]
                        : [source, target]);
                }

                var split = result.Split(source);

                DragTooltip.Inlines.Add(new Run(split[0]) { Foreground = blueBrush });
                DragTooltip.Inlines.Add(source);

                split = split[1].Split(target);

                DragTooltip.Inlines.Add(new Run(split[0]) { Foreground = blueBrush });
                if (split.Length > 1)
                {
                    DragTooltip.Inlines.Add(target);
                    DragTooltip.Inlines.Add(new Run(split[1]) { Foreground = blueBrush });
                }
            }
            else
            {
                if (App.FileActions.IsAppDrive && MouseWithinApp)
                {
                    result = string.Format(Strings.Resources.S_DRAG_INSTALL_MULTIPLE, count);
                    DragTooltip.Inlines.Add(new Run(result) { Foreground = blueBrush });

                    return;
                }

                if (App.CopyPaste.CurrentDropEffect is DragDropEffects.Move)
                {
                    format = string.IsNullOrEmpty(target)
                        ? Strings.Resources.S_DRAGDROP_MOVE
                        : Strings.Resources.S_DRAGDROP_MOVE_TARGET;
                }
                else if (App.CopyPaste.CurrentDropEffect is DragDropEffects.Copy)
                {
                    format = string.IsNullOrEmpty(target)
                        ? Strings.Resources.S_DRAGDROP_COPY
                        : Strings.Resources.S_DRAGDROP_COPY_TARGET;
                }

                if (result == "")
                {
                    result = string.Format(format, string.IsNullOrEmpty(target)
                        ? [count]
                        : [count, target]);
                }

                var split = result.Split(target);

                DragTooltip.Inlines.Add(new Run(split[0]) { Foreground = blueBrush });
                if (split.Length > 1)
                    DragTooltip.Inlines.Add(target);
            }
        }

        if (App.Current.Dispatcher.CheckAccess())
            updateTooltip();
        else if (Application.Current is App app)
            app.EnqueueUiLatest("drag-window.tooltip", "drag-window.tooltip", updateTooltip);
    }

    private bool mouseWithinApp = true;
    public bool MouseWithinApp
    {
        get => mouseWithinApp;
        set
        {
            if (mouseWithinApp == value)
                return;

            mouseWithinApp = value;
            GetPathUnderMouse();

            OnPropertyChanged();
        }
    }

    private HANDLE hwndUnderMouse = IntPtr.Zero;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        DragImage.Width = SystemParameters.IconWidth;
        DragImage.Height = SystemParameters.IconHeight;

        App.CopyPaste.PropertyChanged += (s, e) =>
        {
            if ((e.PropertyName == nameof(App.CopyPaste.DragFiles)
                || e.PropertyName == nameof(App.CopyPaste.DropTarget))
                && App.RuntimeSettings.DragBitmap is not null)
            {
                GetPathUnderMouse();
            }
        };
        App.RuntimeSettings.PropertyChanged += RuntimeSettings_PropertyChanged;

        dragWindowHandle = new WindowInteropHelper(this).Handle;

        Services.WindowStyle.SetWindowHidden(dragWindowHandle);

#if DEBUG
        MouseWithinApp = true;
#endif

        UpdateDragTimerState();
    }

    private void CancelDrag() => App.RuntimeSettings.DragBitmap = null;

    private void UpdateMouse(POINT point)
    {
        if (App.RuntimeSettings.DragBitmap is null)
            return;

        var actualPoint = MonitorInfo.MousePositionToDpi(point, dragWindowHandle);

        if (DragImage.ActualHeight >= 1)
        {
            Top = actualPoint.Y - DragImage.ActualHeight - 2;
            Left = actualPoint.X - DragImage.ActualWidth / 2;
        }

        hwndUnderMouse = CursorInfo.GetWindowUnderMouse(point);

        // Shouldn't happen. But if it does, we don't want to do anything.
        if (hwndUnderMouse == dragWindowHandle)
            return;

        var wasWithinApp = MouseWithinApp;
        MouseWithinApp = hwndUnderMouse == InterceptClipboard.MainWindowHandle;

        if (!MouseWithinApp && App.CopyPaste.DragStatus is CopyPasteService.DragState.None)
            App.RuntimeSettings.DragBitmap = null;

        if (!MouseWithinApp)
        {
            if (wasWithinApp)
                App.CopyPaste.PasteState = DragDropEffects.None;
        }
        else
            App.RuntimeSettings.DragWithinSlave = false;
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        DragTimer.Stop();
        App.RuntimeSettings.PropertyChanged -= RuntimeSettings_PropertyChanged;
    }

    private void Border_MouseUp(object sender, MouseButtonEventArgs e)
    {
        App.RuntimeSettings.DragBitmap = null;
    }
}

