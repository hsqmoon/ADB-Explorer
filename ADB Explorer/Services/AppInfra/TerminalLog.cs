namespace ADB_Explorer.Services;

public static class TerminalLog
{
    private static readonly Mutex mutex = new();
    private static string resolvedLogPath;

    public static void Initialize()
    {
        _ = LogPath;
    }

    public static void PrintLine(string message)
    {
        Write("INFO", message);
    }

    public static void PrintInput(string message)
    {
        Write("IN", message);
    }

    public static void PrintOutput(string message, bool isError = false)
    {
        Write(isError ? "ERR" : "OUT", message);
    }

    private static void Write(string kind, string message)
    {
        mutex.WaitOne();

        try
        {
            if (LogPath is not string logPath)
                return;

            message ??= "";
            message = message.Replace("\r", "\\r").Replace("\n", "\\n");
            File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{kind}] {message}{Environment.NewLine}");
        }
        catch
        { }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static string LogPath => resolvedLogPath ??= PrepareLogPath(Path.Combine(AppContext.BaseDirectory, "log", "terminal.log"));

    private static string PrepareLogPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            Directory.CreateDirectory(directory);

            if (!File.Exists(fullPath))
                File.WriteAllText(fullPath, "");

            return fullPath;
        }
        catch
        {
            return null;
        }
    }
}
