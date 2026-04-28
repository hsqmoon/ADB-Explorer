using ADB_Explorer.Helpers;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;
using System.Windows;

namespace ADB_Explorer.Models;

internal static class Data
{
    private const int MAX_COMMAND_LOG_ENTRIES = 2000;
    private static readonly object commandLogLock = new();
    private static readonly Queue<Log> pendingCommandLogs = new();
    private static bool isCommandLogFlushScheduled;

    public static ADBService.AdbDevice CurrentADBDevice { get; set; } = null;

    public static string CurrentPath { get; set; }
    public static string ParentPath { get; set; }

    public static DriveViewModel CurrentDrive { get; set; } = null;

    public static FileOperationQueue FileOpQ { get; set; }

    public static Dictionary<string, string> CurrentDisplayNames { get; set; } = [];

    public static AppSettings Settings { get; set; } = new();

    public static AppRuntimeSettings RuntimeSettings { get; set; } = new();

    public static CopyPasteService CopyPaste { get; } = new();

    public static AdbShellSession Terminal { get; } = new();

    public static BatchObservableCollection<Log> CommandLog { get; set; } = [];

    public static void AddCommandLog(string content)
    {
        if (!Settings.EnableLog || RuntimeSettings.IsLogPaused)
            return;

        var log = new Log(content);
        lock (commandLogLock)
        {
            pendingCommandLogs.Enqueue(log);
        }

        ScheduleCommandLogFlush();
    }

    private static void ScheduleCommandLogFlush()
    {
        if (Application.Current?.Dispatcher is not { HasShutdownStarted: false, HasShutdownFinished: false } dispatcher)
        {
            FlushPendingCommandLogs();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            if (isCommandLogFlushScheduled)
                return;

            isCommandLogFlushScheduled = true;
            _ = dispatcher.BeginInvoke(FlushPendingCommandLogs, DispatcherPriority.Background);
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(ScheduleCommandLogFlush), DispatcherPriority.Background);
    }

    private static void FlushPendingCommandLogs()
    {
        List<Log> logsToAdd = [];
        lock (commandLogLock)
        {
            while (pendingCommandLogs.Count > 0)
            {
                logsToAdd.Add(pendingCommandLogs.Dequeue());
            }

            isCommandLogFlushScheduled = false;
        }

        if (logsToAdd.Count > 0)
        {
            CommandLog.AddRange(logsToAdd);

            int overflow = CommandLog.Count - MAX_COMMAND_LOG_ENTRIES;
            if (overflow > 0)
                CommandLog.RemoveRange(0, overflow);
        }

        bool shouldFlushAgain;
        lock (commandLogLock)
        {
            shouldFlushAgain = pendingCommandLogs.Count > 0;
        }

        if (shouldFlushAgain)
            ScheduleCommandLogFlush();
    }

    public static ObservableList<TrashIndexer> RecycleIndex { get; set; } = [];

    public static ObservableList<Package> Packages { get; set; } = [];

    public static Version AppVersion => new(Properties.AppGlobal.AppVersion);

    public static FileActionsEnable FileActions { get; set; } = new();

    public static DirectoryLister DirList { get; set; }

    public static string AppDataPath { get; set; } = "";

    public static Devices DevicesObject { get; set; }

    public static MDNS MdnsService { get; set; } = new();

    public static PairingQrClass QrClass { get; set; }

    public static IEnumerable<FileClass> SelectedFiles { get; set; } = [];

    public static IEnumerable<Package> SelectedPackages { get; set; } = [];
}
