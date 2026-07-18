using ADB_Explorer.Models;

namespace ADB_Explorer.Services;

public static class DebugLog
{
    private static readonly Mutex mutex = new();
    private static string resolvedLogPath;

    [Conditional("DEBUG")]
    public static void Initialize()
    {
        _ = LogPath;
    }

    [Conditional("DEBUG")]
    public static void PrintLine(string message)
    {
        mutex.WaitOne();

        try
        {
            if (LogPath is not string logPath)
                return;

            File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss:fff} | {message}\n");
        }
        catch
        {
            // Logging must never crash the app.
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static string LogPath => resolvedLogPath ??= ResolveLogPath();

    private static string ResolveLogPath()
    {
        return PrepareLogPath(Path.Combine(AppContext.BaseDirectory, "log", "dragdrop.log"));
    }

    private static string PrepareLogPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                return null;

            if (Path.GetDirectoryName(fullPath) is not string directory || string.IsNullOrWhiteSpace(directory))
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
