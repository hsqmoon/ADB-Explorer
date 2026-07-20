using System.Threading.Channels;

namespace ADB_Explorer.Services;

internal sealed class ShellStaService : IDisposable
{
    private const int QUEUE_CAPACITY = 128;

    private readonly object workerLock = new();
    private ShellWorker worker;
    private int disposed;

    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(action);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = GetWorker();
            try
            {
                return await current.InvokeAsync(action, cancellationToken).ConfigureAwait(false);
            }
            catch (ShellWorkerUnavailableException) when (Volatile.Read(ref disposed) == 0)
            {
                RemoveWorker(current);
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private ShellWorker GetWorker()
    {
        lock (workerLock)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (worker is null || !worker.IsAlive)
                worker = new ShellWorker(WorkerStopped, QUEUE_CAPACITY);

            return worker;
        }
    }

    private void WorkerStopped(ShellWorker stoppedWorker, Exception failure)
    {
        RemoveWorker(stoppedWorker);
        if (failure is not null && Volatile.Read(ref disposed) == 0)
            App.ReportBackgroundFailure(failure, "shell-sta.worker");
    }

    private void RemoveWorker(ShellWorker stoppedWorker)
    {
        lock (workerLock)
        {
            if (ReferenceEquals(worker, stoppedWorker))
                worker = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        ShellWorker current;
        lock (workerLock)
        {
            current = worker;
            worker = null;
        }

        current?.Dispose();
    }

    private sealed class ShellWorker : IDisposable
    {
        private readonly Channel<IShellRequest> requests;
        private readonly CancellationTokenSource lifetime = new();
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action<ShellWorker, Exception> stopped;
        private readonly Thread thread;
        private Dispatcher dispatcher;
        private int stopping;

        public bool IsAlive => Volatile.Read(ref stopping) == 0;

        public ShellWorker(Action<ShellWorker, Exception> stopped, int capacity)
        {
            this.stopped = stopped;
            requests = Channel.CreateBounded<IShellRequest>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ADB Explorer Shell STA",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref stopping) != 0)
                throw new ShellWorkerUnavailableException(
                    new OperationCanceledException("The Shell STA service stopped."));

            var request = new ShellRequest<T>(action, cancellationToken);
            try
            {
                await requests.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                throw new ShellWorkerUnavailableException(ex);
            }

            return await request.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private void Run()
        {
            Exception failure = null;
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                _ = PumpAsync();
                if (Volatile.Read(ref stopping) != 0)
                    throw new OperationCanceledException("The Shell STA service stopped during startup.");

                ready.TrySetResult();
                Dispatcher.Run();
                if (Volatile.Read(ref stopping) == 0)
                    failure = new InvalidOperationException("The Shell STA dispatcher exited unexpectedly.");
            }
            catch (Exception ex)
            {
                failure = ex;
                ready.TrySetException(new ShellWorkerUnavailableException(ex));
            }
            finally
            {
                Interlocked.Exchange(ref stopping, 1);
                ready.TrySetException(new ShellWorkerUnavailableException(
                    failure ?? new OperationCanceledException("The Shell STA service stopped.")));
                lifetime.Cancel();
                requests.Writer.TryComplete(failure);
                while (requests.Reader.TryRead(out var request))
                {
                    request.Fail(new ShellWorkerUnavailableException(
                        failure ?? new OperationCanceledException("The Shell STA service stopped.")));
                }

                stopped(this, failure);
                lifetime.Dispose();
            }
        }

        private async Task PumpAsync()
        {
            try
            {
                while (await requests.Reader.WaitToReadAsync(lifetime.Token))
                {
                    if (requests.Reader.TryRead(out var request))
                        request.Execute();

                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            { }
            catch (Exception ex)
            {
                App.ReportBackgroundFailure(ex, "shell-sta.pump");
            }
            finally
            {
                if (dispatcher is not null && !dispatcher.HasShutdownStarted)
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0)
                return;

            requests.Writer.TryComplete();
            lifetime.Cancel();
            if (dispatcher is not null && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }

    }

    private interface IShellRequest
    {
        void Execute();
        void Fail(Exception exception);
    }

    private sealed class ShellRequest<T>(Func<T> action, CancellationToken cancellationToken) : IShellRequest
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Completion => completion.Task;

        public void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        public void Fail(Exception exception) => completion.TrySetException(exception);
    }

    private sealed class ShellWorkerUnavailableException(Exception innerException) : Exception(null, innerException);
}
