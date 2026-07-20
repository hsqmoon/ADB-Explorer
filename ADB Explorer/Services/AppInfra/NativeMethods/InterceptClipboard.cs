using ADB_Explorer.Models;

namespace ADB_Explorer.Services;

public static partial class NativeMethods
{
    public sealed partial class InterceptClipboard : IDisposable
    {
        private static Action _externalClipAction;
        private static Action<string> _externalIpcAction;
        private static HwndSource _hwndSource;

        public static HANDLE MainWindowHandle { get; private set; } = IntPtr.Zero;

        public static void Init(Window window, Action clipboardAction, Action<string> ipcAction)
        {
            _externalClipAction = clipboardAction;
            _externalIpcAction = ipcAction;
            RoutedEventHandler windowLoadedHandler = null;

            windowLoadedHandler = (sender, e) =>
            {
                MainWindowHandle = new WindowInteropHelper(window).Handle;

                _hwndSource = HwndSource.FromHwnd(MainWindowHandle);
                _hwndSource.AddHook(WndProc);

                AddClipboardFormatListener(MainWindowHandle);

                window.Loaded -= windowLoadedHandler;
            };

            if (window.IsLoaded)
                windowLoadedHandler(window, new RoutedEventArgs());
            else
                window.Loaded += windowLoadedHandler;
        }

        public static void Close()
        {
            RemoveClipboardFormatListener(MainWindowHandle);
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource?.Dispose();
            _hwndSource = null;
            _externalClipAction = null;
            _externalIpcAction = null;
        }

        public static void ScheduleClipboardRefresh()
        {
            if (Application.Current is not App app)
                return;

            app.EnqueueUiLatest(
                "clipboard.refresh",
                "clipboard.refresh",
                () => _externalClipAction?.Invoke());
        }

        private static HANDLE WndProc(HANDLE hwnd, int msg, HANDLE wParam, HANDLE lParam, ref bool handled)
        {
            if ((ClipboardNotificationMessage)msg is ClipboardNotificationMessage.WM_CLIPBOARDUPDATE)
            {
                if (App.RuntimeSettings.IsWindowLoaded)
                    ScheduleClipboardRefresh();
                handled = true;
            }
            else if ((WindowMessages)msg is WindowMessages.WM_COPYDATA)
            // Since we already have a hook for MainWindow, we'll use it for IPC as well
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                _externalIpcAction?.Invoke(cds.lpData);
            }

            return IntPtr.Zero;
        }

        [LibraryImport("User32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AddClipboardFormatListener(HANDLE hwnd);

        [LibraryImport("User32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RemoveClipboardFormatListener(HANDLE hwnd);

        public void Dispose() => Close();
    }
}
