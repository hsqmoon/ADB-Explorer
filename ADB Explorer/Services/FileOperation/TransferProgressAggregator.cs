using ADB_Explorer.Models;

namespace ADB_Explorer.Services;

internal sealed record TransferProgressSnapshot(
    IReadOnlyList<(SyncFile File, FileOpProgressInfo Update)> Updates,
    long TotalBytes,
    long TransferredBytes,
    int ActiveCount,
    double ActivePercentage,
    long CapturedTimestamp);

internal sealed class TransferProgressAggregator : IDisposable
{
    private static readonly TimeSpan FLUSH_INTERVAL = TimeSpan.FromMilliseconds(100);

    private readonly object sync = new();
    private readonly Dictionary<string, (SyncFile File, FileOpProgressInfo Update)> latestByFile = [];
    private readonly Func<TransferProgressSnapshot, Task> applySnapshot;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly SemaphoreSlim flushGate = new(1, 1);
    private int flushScheduled;
    private int disposed;
    private long totalBytes;
    private long transferredBytes;
    private int activeCount;
    private double activePercentage;

    public TransferProgressAggregator(Func<TransferProgressSnapshot, Task> applySnapshot)
    {
        this.applySnapshot = applySnapshot;
        lifetimeToken = lifetime.Token;
    }

    public void Reset(long initialTotalBytes)
    {
        lock (sync)
        {
            latestByFile.Clear();
            totalBytes = initialTotalBytes;
            transferredBytes = 0;
            activeCount = 0;
            activePercentage = 0;
        }
    }

    public void Report(
        SyncFile file,
        FileOpProgressInfo update,
        long transferredBytesIncrease,
        double previousPercentage,
        double percentage,
        long totalBytesIncrease)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            latestByFile[file.FullPath] = (file, update);
            totalBytes += totalBytesIncrease;
            (transferredBytes, activeCount, activePercentage) = CalculateTotals(
                transferredBytes,
                activeCount,
                activePercentage,
                transferredBytesIncrease,
                previousPercentage,
                percentage);
        }

        ScheduleFlush();
    }

    public async Task FlushAsync()
    {
        await flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            TransferProgressSnapshot snapshot;
            lock (sync)
            {
                snapshot = new(
                    latestByFile.Values.ToArray(),
                    totalBytes,
                    transferredBytes,
                    activeCount,
                    activePercentage,
                    Stopwatch.GetTimestamp());
                latestByFile.Clear();
            }

            if (snapshot.Updates.Count > 0)
            {
                await applySnapshot(snapshot).ConfigureAwait(false);
                PerformanceTrace.Log.TransferProgressDelay(
                    snapshot.Updates.Count,
                    Stopwatch.GetElapsedTime(snapshot.CapturedTimestamp).Ticks / 10);
            }

        }
        finally
        {
            Interlocked.Exchange(ref flushScheduled, 0);
            bool hasPendingUpdates;
            lock (sync)
                hasPendingUpdates = latestByFile.Count > 0;

            flushGate.Release();

            if (hasPendingUpdates && Volatile.Read(ref disposed) == 0)
                ScheduleFlush();
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            latestByFile.Clear();
            totalBytes = 0;
            transferredBytes = 0;
            activeCount = 0;
            activePercentage = 0;
        }
    }

    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref flushScheduled, 1) == 1)
            return;

        _ = FlushAfterDelayAsync();
    }

    private async Task FlushAfterDelayAsync()
    {
        try
        {
            await Task.Delay(FLUSH_INTERVAL, lifetimeToken).ConfigureAwait(false);
            await FlushAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        { }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref flushScheduled, 0);
            App.ReportBackgroundFailure(ex, nameof(TransferProgressAggregator));
        }
    }

    internal static (long TransferredBytes, int ActiveCount, double ActivePercentage) CalculateTotals(
        long transferredBytes,
        int activeCount,
        double activePercentage,
        long transferredBytesIncrease,
        double previousPercentage,
        double percentage)
    {
        transferredBytes = Math.Max(0, transferredBytes + transferredBytesIncrease);

        if (previousPercentage is > 0 and < 100)
        {
            activeCount--;
            activePercentage -= previousPercentage;
        }

        if (percentage is > 0 and < 100)
        {
            activeCount++;
            activePercentage += percentage;
        }

        return (transferredBytes, activeCount, activePercentage);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        lifetime.Cancel();
        lifetime.Dispose();
        Clear();
    }
}
