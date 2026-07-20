using System.Diagnostics.Tracing;

namespace ADB_Explorer.Services;

[EventSource(Name = "ADBExplorer-Performance")]
internal sealed class PerformanceTrace : EventSource
{
    public static readonly PerformanceTrace Log = new();

    private PerformanceTrace()
    { }

    [Event(1, Level = EventLevel.Informational)]
    public void StartupStage(string stage, long elapsedMicroseconds)
    {
        if (IsEnabled())
            WriteEvent(1, stage, elapsedMicroseconds);
    }

    [Event(2, Level = EventLevel.Warning)]
    public void UiWorkOverBudget(string workName, long elapsedMicroseconds)
    {
        if (IsEnabled())
            WriteEvent(2, workName, elapsedMicroseconds);
    }

    [Event(3, Level = EventLevel.Error)]
    public void UiWorkStalled(string workName, long elapsedMicroseconds)
    {
        if (IsEnabled())
            WriteEvent(3, workName, elapsedMicroseconds);
    }

    [Event(4, Level = EventLevel.Informational)]
    public void DeviceSnapshotApplied(long sequence, long deviceCount, long elapsedMicroseconds)
    {
        if (IsEnabled())
            WriteEvent(4, sequence, deviceCount, elapsedMicroseconds);
    }

    [Event(5, Level = EventLevel.Informational)]
    public void DirectoryFirstRow(long generation, long elapsedMicroseconds)
    {
        if (IsEnabled())
            WriteEvent(5, generation, elapsedMicroseconds);
    }

    [Event(6, Level = EventLevel.Informational)]
    public void UiQueueDepth(int fifoDepth, int latestDepth)
    {
        if (IsEnabled())
            WriteEvent(6, fifoDepth, latestDepth);
    }

    [Event(7, Level = EventLevel.Informational)]
    public void TransferProgressDelay(long updateCount, long elapsedMicroseconds)
    {
        if (IsEnabled())
            WriteEvent(7, updateCount, elapsedMicroseconds);
    }
}
