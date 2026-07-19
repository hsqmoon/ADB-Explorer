using ADB_Explorer.Models;

namespace ADB_Explorer.Services;

public static partial class NativeMethods
{
    public sealed partial class InterceptClipboard : IDisposable
    {
        private static Action _externalClipAction;
        private static Action<string> _externalIpcAction;
        private static HwndSource _hwndSource;
        private static Dispatcher _dispatcher;
        private static int _clipboardRefreshScheduled;

        public static HANDLE MainWindowHandle { get; private set; } = IntPtr.Zero;

        public static void Init(Window window, Action clipboardAction, Action<string> ipcAction)
        {
            _externalClipAction = clipboardAction;
            _externalIpcAction = ipcAction;
            _dispatcher = window.Dispatcher;
            RoutedEventHandler windowLoadedHandler = null;

            windowLoadedHandler = (sender, e) =>
            {
                MainWindowHandle = new WindowInteropHelper(window).Handle;

                _hwndSource = HwndSource.FromHwnd(MainWindowHandle);
                _hwndSource.AddHook(WndProc);

                AddClipboardFormatListener(MainWindowHandle);

                window.Loaded -= windowLoadedHandler;
            };

            window.Loaded += windowLoadedHandler;
        }

        public static void Close()
        {
            RemoveClipboardFormatListener(MainWindowHandle);
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource?.Dispose();
            _hwndSource = null;
            _dispatcher = null;
            _externalClipAction = null;
            _externalIpcAction = null;
            Interlocked.Exchange(ref _clipboardRefreshScheduled, 0);
        }

        public static void ScheduleClipboardRefresh()
        {
            if (_dispatcher is not { HasShutdownStarted: false } dispatcher
                || Interlocked.Exchange(ref _clipboardRefreshScheduled, 1) == 1)
            {
                return;
            }

            try
            {
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    Interlocked.Exchange(ref _clipboardRefreshScheduled, 0);
                    _externalClipAction?.Invoke();
                }), DispatcherPriority.ApplicationIdle);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _clipboardRefreshScheduled, 0);
            }
        }

        private static HANDLE WndProc(HANDLE hwnd, int msg, HANDLE wParam, HANDLE lParam, ref bool handled)
        {
            if ((ClipboardNotificationMessage)msg is ClipboardNotificationMessage.WM_CLIPBOARDUPDATE)
            {
                if (!Data.RuntimeSettings.IsSplashScreenVisible)
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
