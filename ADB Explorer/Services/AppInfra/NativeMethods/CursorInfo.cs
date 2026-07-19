namespace ADB_Explorer.Services;

public static partial class NativeMethods
{
    public static partial class CursorInfo
    {
        private const int VK_RBUTTON = 0x02;

        private static HANDLE windowUnderMouse = IntPtr.Zero;

        public static bool TryGetPosition(out POINT point) => GetCursorPos(out point);

        public static bool IsRightButtonPressed => (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

        public static HANDLE GetWindowUnderMouse()
        {
            if (TryGetPosition(out var point))
                return GetWindowUnderMouse(point);

            return windowUnderMouse;
        }

        public static HANDLE GetWindowUnderMouse(POINT point)
        {
            windowUnderMouse = GetAncestor(WindowFromPoint(point), GaFlags.GA_ROOT);
            return windowUnderMouse;
        }

        [LibraryImport("User32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out POINT lpPoint);

        [LibraryImport("User32.dll")]
        private static partial short GetAsyncKeyState(int vKey);

        [LibraryImport("User32.dll")]
        private static partial HANDLE WindowFromPoint(POINT point);

        [LibraryImport("User32.dll")]
        private static partial HANDLE GetAncestor(HANDLE hwnd, GaFlags gaFlags);
    }
}
