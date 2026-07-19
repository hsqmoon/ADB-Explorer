using ADB_Explorer.Models;

namespace ADB_Explorer.Services.AppInfra;

public class IpcService
{
    private static readonly object PendingFileMovesLock = new();
    private static readonly HashSet<(string DeviceId, string FullPath)> PendingFileMoves = [];
    private static int fileMoveRefreshScheduled;

    public enum MessageType
    {
        DragCanceled,
        FileMoved,
    }

    public static void AcceptIpcMessage(string message)
    {
        string[] msgContent = message.Split('|', 2);
        if (msgContent.Length != 2)
            return;

        if (!Enum.TryParse(typeof(MessageType), msgContent[0], true, out var res))
            return;

        switch ((MessageType)res)
        {
            case MessageType.DragCanceled:
                if (Enum.TryParse(msgContent[1], out NativeMethods.HResult hr) && hr is NativeMethods.HResult.DRAGDROP_S_CANCEL)
                    Data.CopyPaste.ClearDrag();
                break;
            case MessageType.FileMoved:
                var content = msgContent[1].Split('\n', 2);
                if (content.Length != 2)
                    return;

                lock (PendingFileMovesLock)
                {
                    PendingFileMoves.Add((content[0], content[1]));
                }

                ScheduleFileMoveRefresh();

                break;
        }
    }

    private static void ScheduleFileMoveRefresh()
    {
        if (Interlocked.Exchange(ref fileMoveRefreshScheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);

            if (App.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher)
            {
                Interlocked.Exchange(ref fileMoveRefreshScheduled, 0);
                return;
            }

            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                (string DeviceId, string FullPath)[] movedFiles;
                lock (PendingFileMovesLock)
                {
                    movedFiles = [.. PendingFileMoves];
                    PendingFileMoves.Clear();
                    Interlocked.Exchange(ref fileMoveRefreshScheduled, 0);
                }

                string deviceId = Data.CurrentADBDevice?.ID;
                string currentPath = Data.CurrentPath;
                var paths = movedFiles
                    .Where(file => file.DeviceId == deviceId
                        && ADB_Explorer.Helpers.FileHelper.GetParentPath(file.FullPath) == currentPath)
                    .Select(file => file.FullPath)
                    .ToHashSet();

                if (paths.Count > 0 && Data.DirList is not null)
                    Data.DirList.FileList.RemoveAll(file => paths.Contains(file.FullPath));
            }), DispatcherPriority.Background);
        });
    }

    public static bool SendIpcMessage(HANDLE hWnd, MessageType type, string content = "")
    {
        var message = $"{Enum.GetName(type)}|{content}";

        NativeMethods.COPYDATASTRUCT cds = new()
        {
            dwData = IntPtr.Zero,
            cbData = Encoding.Unicode.GetByteCount(message) + 2,
            lpData = message
        };

        return NativeMethods.SendMessage(hWnd, NativeMethods.WindowMessages.WM_COPYDATA, ref cds);
    }

    public static void NotifyDropCancel(NativeMethods.HResult hr)
    {
        if (Data.RuntimeSettings.DragWithinSlave)
            SendIpcMessage(NativeMethods.CursorInfo.GetWindowUnderMouse(), MessageType.DragCanceled, $"{hr}");
    }

    public static void NotifyFileMoved(int remotePid, ADBService.AdbDevice device, FilePath file)
    {
        using var process = Process.GetProcessById(remotePid);

        SendIpcMessage(process.MainWindowHandle, MessageType.FileMoved, $"{device.ID}\n{file.FullPath}");
    }
}
