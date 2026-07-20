using System.Threading.Channels;

namespace ADB_Explorer.Services;

internal interface IUiWorkScheduler : IDisposable
{
    bool CheckAccess { get; }

    ValueTask EnqueueAsync(string workName, Action action, CancellationToken cancellationToken = default);

    void EnqueueLatest(string key, string workName, Action action);
}

internal sealed class UiWorkScheduler : IUiWorkScheduler
{
    private const int FIFO_CAPACITY = 512;
    private const int LATEST_CAPACITY = 1024;
    private static readonly TimeSpan MAX_UI_WORK = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan STALL_THRESHOLD = TimeSpan.FromMilliseconds(50);

    private readonly Dispatcher dispatcher;
    private readonly Channel<UiWorkItem> fifo = Channel.CreateBounded<UiWorkItem>(new BoundedChannelOptions(FIFO_CAPACITY)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly object latestLock = new();
    private readonly Dictionary<string, UiWorkItem> latestByKey = [];
    private readonly Queue<string> latestKeys = [];
    private int drainScheduled;
    private int disposed;
    private int fifoCount;
    private bool readLatestNext;

    public bool CheckAccess => dispatcher.CheckAccess();

    public UiWorkScheduler(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        dispatcher.ShutdownStarted += Dispatcher_ShutdownStarted;
    }

    public async ValueTask EnqueueAsync(string workName, Action action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
        if (dispatcher.HasShutdownStarted)
        {
            Dispose();
            throw new ObjectDisposedException(nameof(UiWorkScheduler));
        }
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new UiWorkItem(workName, action, completion, cancellationToken);

        Interlocked.Increment(ref fifoCount);
        try
        {
            await fifo.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref fifoCount);
            throw;
        }
        LogQueueDepth();
        ScheduleDrain();
        await completion.Task.ConfigureAwait(false);
    }

    public void EnqueueLatest(string key, string workName, Action action)
    {
        if (Volatile.Read(ref disposed) == 1 || dispatcher.HasShutdownStarted)
            return;

        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(action);

        lock (latestLock)
        {
            if (Volatile.Read(ref disposed) == 1 || dispatcher.HasShutdownStarted)
                return;

            if (!latestByKey.ContainsKey(key))
            {
                while (latestByKey.Count >= LATEST_CAPACITY && latestKeys.Count > 0)
                    latestByKey.Remove(latestKeys.Dequeue());

                latestKeys.Enqueue(key);
            }

            latestByKey[key] = new(workName, action, null, CancellationToken.None);
        }

        LogQueueDepth();
        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (Volatile.Read(ref disposed) == 1
            || dispatcher.HasShutdownStarted
            || Interlocked.Exchange(ref drainScheduled, 1) == 1)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(Drain, DispatcherPriority.Input);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref drainScheduled, 0);
        }
    }

    private void Drain()
    {
        Interlocked.Exchange(ref drainScheduled, 0);
        if (Volatile.Read(ref disposed) == 1)
            return;

        if (TryTakeNext(out var item))
            Execute(item);

        if (HasPendingWork())
            ScheduleDrain();
    }

    private bool TryTakeNext(out UiWorkItem item)
    {
        if (readLatestNext && TryTakeLatest(out item))
        {
            readLatestNext = false;
            return true;
        }

        if (fifo.Reader.TryRead(out item))
        {
            Interlocked.Decrement(ref fifoCount);
            readLatestNext = true;
            return true;
        }

        if (TryTakeLatest(out item))
        {
            readLatestNext = false;
            return true;
        }

        return false;
    }

    private bool TryTakeLatest(out UiWorkItem item)
    {
        lock (latestLock)
        {
            while (latestKeys.Count > 0)
            {
                var key = latestKeys.Dequeue();
                if (latestByKey.Remove(key, out item))
                    return true;
            }
        }

        item = null;
        return false;
    }

    private static void Execute(UiWorkItem item)
    {
        if (item.CancellationToken.IsCancellationRequested)
        {
            item.Completion?.TrySetCanceled(item.CancellationToken);
            return;
        }

        var workStart = Stopwatch.GetTimestamp();
        try
        {
            item.Action();
            item.Completion?.TrySetResult();
        }
        catch (Exception ex)
        {
            if (item.Completion is not null)
                item.Completion.TrySetException(ex);
            else
                App.ReportBackgroundFailure(ex, item.WorkName);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(workStart);
            if (elapsed >= STALL_THRESHOLD)
                PerformanceTrace.Log.UiWorkStalled(item.WorkName, elapsed.Ticks / 10);
            else if (elapsed > MAX_UI_WORK)
                PerformanceTrace.Log.UiWorkOverBudget(item.WorkName, elapsed.Ticks / 10);
        }
    }

    private bool HasPendingWork()
    {
        if (fifo.Reader.TryPeek(out _))
            return true;

        lock (latestLock)
            return latestKeys.Count > 0;
    }

    private void LogQueueDepth()
    {
        if (!PerformanceTrace.Log.IsEnabled())
            return;

        int latestDepth;
        lock (latestLock)
            latestDepth = latestByKey.Count;
        PerformanceTrace.Log.UiQueueDepth(Volatile.Read(ref fifoCount), latestDepth);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return;

        dispatcher.ShutdownStarted -= Dispatcher_ShutdownStarted;
        fifo.Writer.TryComplete();
        while (fifo.Reader.TryRead(out var item))
        {
            Interlocked.Decrement(ref fifoCount);
            item.Completion?.TrySetCanceled();
        }

        lock (latestLock)
        {
            latestByKey.Clear();
            latestKeys.Clear();
        }
    }

    private void Dispatcher_ShutdownStarted(object sender, EventArgs e) => Dispose();

    private sealed record UiWorkItem(
        string WorkName,
        Action Action,
        TaskCompletionSource Completion,
        CancellationToken CancellationToken);
}
