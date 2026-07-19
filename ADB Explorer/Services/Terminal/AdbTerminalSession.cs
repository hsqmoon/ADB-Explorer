using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ADB_Explorer.Services.Terminal;

public sealed class AdbTerminalSession : ViewModelBase, IAsyncDisposable
{
    private const int DEFAULT_COLUMNS = 80;
    private const int DEFAULT_ROWS = 24;
    private const int INPUT_QUEUE_CAPACITY = 128;
    private const int OUTPUT_CHUNK_SIZE = 16 * 1024;
    private const int OUTPUT_QUEUE_CAPACITY = 256;
    private const int OUTPUT_BATCH_SIZE = 64 * 1024;
    private static readonly TimeSpan OUTPUT_BATCH_INTERVAL = TimeSpan.FromMilliseconds(12);

    private readonly SemaphoreSlim lifecycleMutex = new(1, 1);
    private readonly Func<string, int, int, Task<ITerminalTransport>> transportFactory;
    private readonly TimeSpan reconnectDelay;

    private ITerminalTransport terminalTransport;
    private CancellationTokenSource sessionCts;
    private Channel<TerminalInput> inputQueue;
    private Channel<byte[]> outputQueue;
    private Task inputPump;
    private Task outputReader;
    private Task outputBatcher;
    private string requestedDeviceId = "";
    private string connectedDeviceId = "";
    private string statusText = "Terminal closed.";
    private bool isOpen;
    private bool isConnected;
    private bool isStarting;
    private bool isDisposed;
    private int columns = DEFAULT_COLUMNS;
    private int rows = DEFAULT_ROWS;
    private int activeGeneration;

    private readonly record struct TerminalInput(byte[] Data, int Columns, int Rows, bool IsInterrupt);

    public AdbTerminalSession()
        : this(StartAdbProcess, TimeSpan.FromMilliseconds(1200))
    { }

    internal AdbTerminalSession(
        Func<string, int, int, Task<ITerminalTransport>> transportFactory,
        TimeSpan reconnectDelay)
    {
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        this.reconnectDelay = reconnectDelay;
    }

    public event Func<byte[], Task> OutputReceived;
    public event Action Cleared;

    public string StatusText
    {
        get => statusText;
        private set => Set(ref statusText, value);
    }

    public string ConnectedDeviceId
    {
        get => connectedDeviceId;
        private set
        {
            if (Set(ref connectedDeviceId, value))
                OnPropertyChanged(nameof(SessionTitle));
        }
    }

    public string SessionTitle => string.IsNullOrWhiteSpace(ConnectedDeviceId)
        ? Strings.Resources.S_TERMINAL
        : $"{Strings.Resources.S_TERMINAL} - {ConnectedDeviceId}";

    public bool IsConnected
    {
        get => isConnected;
        private set
        {
            if (Set(ref isConnected, value))
                OnPropertyChanged(nameof(CanInterrupt));
        }
    }

    public bool IsStarting
    {
        get => isStarting;
        private set => Set(ref isStarting, value);
    }

    public bool CanInterrupt => IsConnected;

    public async Task OpenAsync(string deviceId, int initialColumns, int initialRows)
    {
        await lifecycleMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            isOpen = true;
            requestedDeviceId = NormalizeDeviceId(deviceId);
            columns = NormalizeDimension(initialColumns, DEFAULT_COLUMNS);
            rows = NormalizeDimension(initialRows, DEFAULT_ROWS);
            await RestartForRequestedDeviceAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleMutex.Release();
        }
    }

    public async Task SetDeviceAsync(string deviceId)
    {
        await lifecycleMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (isDisposed || !isOpen)
                return;

            string normalizedDeviceId = NormalizeDeviceId(deviceId);
            if (string.Equals(requestedDeviceId, normalizedDeviceId, StringComparison.Ordinal)
                && terminalTransport is not null)
            {
                return;
            }

            requestedDeviceId = normalizedDeviceId;
            await RestartForRequestedDeviceAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleMutex.Release();
        }
    }

    public async Task CloseAsync()
    {
        await lifecycleMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            isOpen = false;
            requestedDeviceId = "";
            Interlocked.Increment(ref activeGeneration);
            await StopCoreAsync().ConfigureAwait(false);
            UpdateUi(() =>
            {
                StatusText = "Terminal closed.";
                ConnectedDeviceId = "";
                IsConnected = false;
                IsStarting = false;
            });
            Cleared?.Invoke();
        }
        finally
        {
            lifecycleMutex.Release();
        }
    }

    public Task<bool> SendTextAsync(string input)
    {
        if (string.IsNullOrEmpty(input))
            return Task.FromResult(true);

        return EnqueueInputAsync(new(Encoding.UTF8.GetBytes(input), 0, 0, false));
    }

    public Task<bool> SendBinaryAsync(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty)
            return Task.FromResult(true);

        return EnqueueInputAsync(new(input.ToArray(), 0, 0, false));
    }

    public Task<bool> InterruptAsync() => EnqueueInputAsync(new(null, 0, 0, true));

    public void Clear() => Cleared?.Invoke();

    internal void SetHostUnavailable(string message)
    {
        UpdateUi(() =>
        {
            IsStarting = false;
            IsConnected = false;
            StatusText = message;
        });
    }

    public Task<bool> ResizeAsync(int newColumns, int newRows)
    {
        newColumns = NormalizeDimension(newColumns, DEFAULT_COLUMNS);
        newRows = NormalizeDimension(newRows, DEFAULT_ROWS);
        if (columns == newColumns && rows == newRows)
            return Task.FromResult(true);

        columns = newColumns;
        rows = newRows;
        if (inputQueue is null)
            return Task.FromResult(true);

        return EnqueueInputAsync(new(null, columns, rows, false));
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
            return;

        await lifecycleMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (isDisposed)
                return;

            isDisposed = true;
            isOpen = false;
            requestedDeviceId = "";
            Interlocked.Increment(ref activeGeneration);
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleMutex.Release();
            lifecycleMutex.Dispose();
        }
    }

    private async Task RestartForRequestedDeviceAsync()
    {
        Interlocked.Increment(ref activeGeneration);
        await StopCoreAsync().ConfigureAwait(false);
        Cleared?.Invoke();

        if (string.IsNullOrEmpty(requestedDeviceId))
        {
            UpdateUi(() =>
            {
                StatusText = "Waiting for an online device...";
                ConnectedDeviceId = "";
            });
            return;
        }

        int generation = Volatile.Read(ref activeGeneration);
        await StartCoreAsync(requestedDeviceId, generation).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(string deviceId, int generation)
    {
        UpdateUi(() =>
        {
            IsStarting = true;
            IsConnected = false;
            ConnectedDeviceId = deviceId;
            StatusText = $"Attaching terminal to {deviceId}...";
        });

        try
        {
            ITerminalTransport transport = await transportFactory(deviceId, columns, rows).ConfigureAwait(false);
            CancellationTokenSource cts = new();
            Channel<TerminalInput> newInputQueue = Channel.CreateBounded<TerminalInput>(new BoundedChannelOptions(INPUT_QUEUE_CAPACITY)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            Channel<byte[]> newOutputQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OUTPUT_QUEUE_CAPACITY)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });

            terminalTransport = transport;
            sessionCts = cts;
            inputQueue = newInputQueue;
            outputQueue = newOutputQueue;
            inputPump = PumpInputAsync(transport, newInputQueue.Reader, cts.Token);
            outputReader = ReadOutputAsync(transport, newOutputQueue.Writer, cts.Token);
            outputBatcher = BatchOutputAsync(newOutputQueue.Reader, generation, cts.Token);

            Task readerTask = outputReader;
            Task batcherTask = outputBatcher;
            _ = MonitorTransportAsync(transport, deviceId, generation, readerTask, batcherTask, cts.Token);

            UpdateUi(() =>
            {
                IsStarting = false;
                IsConnected = true;
                StatusText = $"Attached to {deviceId}";
            });
        }
        catch (Exception ex)
        {
            await StopCoreAsync().ConfigureAwait(false);
            UpdateUi(() =>
            {
                IsStarting = false;
                IsConnected = false;
                StatusText = $"Unable to attach to {deviceId}: {ex.Message}";
            });

            if (isOpen
                && string.Equals(requestedDeviceId, deviceId, StringComparison.Ordinal)
                && generation == Volatile.Read(ref activeGeneration))
            {
                _ = ReconnectAsync(deviceId, generation);
            }
        }
    }

    private async Task MonitorTransportAsync(
        ITerminalTransport transport,
        string deviceId,
        int generation,
        Task readerTask,
        Task batcherTask,
        CancellationToken cancellationToken)
    {
        int exitCode;
        try
        {
            exitCode = await transport.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            exitCode = -1;
        }

        try
        {
            await Task.WhenAll(readerTask, batcherTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch
        { }

        await lifecycleMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (isDisposed
                || generation != Volatile.Read(ref activeGeneration)
                || !ReferenceEquals(transport, terminalTransport))
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            UpdateUi(() =>
            {
                IsConnected = false;
                IsStarting = false;
                StatusText = isOpen
                    ? $"Shell on {deviceId} ended ({exitCode}). Reattaching..."
                    : "Terminal closed.";
            });

            if (isOpen && string.Equals(requestedDeviceId, deviceId, StringComparison.Ordinal))
            {
                int reconnectGeneration = Interlocked.Increment(ref activeGeneration);
                _ = ReconnectAsync(deviceId, reconnectGeneration);
            }
        }
        finally
        {
            lifecycleMutex.Release();
        }
    }

    private async Task ReconnectAsync(string deviceId, int generation)
    {
        try
        {
            await Task.Delay(reconnectDelay).ConfigureAwait(false);
            await lifecycleMutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (isDisposed
                    || !isOpen
                    || terminalTransport is not null
                    || generation != Volatile.Read(ref activeGeneration)
                    || !string.Equals(requestedDeviceId, deviceId, StringComparison.Ordinal))
                {
                    return;
                }

                int nextGeneration = Interlocked.Increment(ref activeGeneration);
                await StartCoreAsync(deviceId, nextGeneration).ConfigureAwait(false);
            }
            finally
            {
                lifecycleMutex.Release();
            }
        }
        catch (ObjectDisposedException)
        { }
    }

    private async Task<bool> EnqueueInputAsync(TerminalInput input)
    {
        Channel<TerminalInput> queue = inputQueue;
        CancellationTokenSource cts = sessionCts;
        if (queue is null || cts is null)
            return false;

        try
        {
            await queue.Writer.WriteAsync(input, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        { }
        catch (ChannelClosedException)
        { }

        return false;
    }

    private static async Task PumpInputAsync(
        ITerminalTransport transport,
        ChannelReader<TerminalInput> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.TryRead(out TerminalInput message))
                    continue;

                if (message.IsInterrupt)
                {
                    try
                    {
                        await transport.InterruptAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException)
                    { }
                    continue;
                }

                if (message.Data is null)
                {
                    while (reader.TryPeek(out TerminalInput nextResize)
                           && !nextResize.IsInterrupt
                           && nextResize.Data is null
                           && reader.TryRead(out TerminalInput latestResize))
                    {
                        message = latestResize;
                    }

                    try
                    {
                        await transport.ResizeAsync(
                            message.Columns,
                            message.Rows,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException)
                    { }
                    continue;
                }

                await transport.WriteInputAsync(message.Data, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        { }
        catch (ObjectDisposedException)
        { }
        catch (IOException)
        { }
    }

    private static async Task ReadOutputAsync(
        ITerminalTransport transport,
        ChannelWriter<byte[]> writer,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[OUTPUT_CHUNK_SIZE];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await transport.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await writer.WriteAsync(buffer.AsSpan(0, read).ToArray(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        { }
        catch (ObjectDisposedException)
        { }
        catch (IOException)
        { }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task BatchOutputAsync(
        ChannelReader<byte[]> reader,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(OUTPUT_BATCH_INTERVAL, cancellationToken).ConfigureAwait(false);

                List<byte[]> chunks = [];
                int length = 0;
                while (length < OUTPUT_BATCH_SIZE && reader.TryRead(out byte[] chunk))
                {
                    chunks.Add(chunk);
                    length += chunk.Length;
                }

                Func<byte[], Task> sink = OutputReceived;
                if (length > 0
                    && sink is not null
                    && generation == Volatile.Read(ref activeGeneration))
                {
                    await sink(Combine(chunks, length)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        { }
    }

    private async Task StopCoreAsync()
    {
        ITerminalTransport transport = terminalTransport;
        CancellationTokenSource cts = sessionCts;
        Channel<TerminalInput> pendingInput = inputQueue;
        Task oldInputPump = inputPump;
        Task oldOutputReader = outputReader;
        Task oldOutputBatcher = outputBatcher;

        terminalTransport = null;
        sessionCts = null;
        inputQueue = null;
        outputQueue = null;
        inputPump = null;
        outputReader = null;
        outputBatcher = null;

        pendingInput?.Writer.TryComplete();
        if (transport is not null)
            await transport.DisposeAsync().ConfigureAwait(false);

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        { }

        Task[] pumps = [oldInputPump, oldOutputReader, oldOutputBatcher];
        Task[] activePumps = pumps.Where(task => task is not null).ToArray();
        if (activePumps.Length > 0)
        {
            try
            {
                await Task.WhenAll(activePumps).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            { }
        }

        cts?.Dispose();
        UpdateUi(() => IsConnected = false);
    }

    private static byte[] Combine(List<byte[]> chunks, int length)
    {
        if (chunks.Count == 1)
            return chunks[0];

        byte[] combined = new byte[length];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
            offset += chunk.Length;
        }

        return combined;
    }

    private static Task<ITerminalTransport> StartAdbProcess(string deviceId, int columns, int rows)
    {
        return AdbTerminalTransport.ConnectAsync(
            Data.RuntimeSettings.AdbServerEndPoint,
            deviceId,
            columns,
            rows);
    }

    private static string NormalizeDeviceId(string deviceId) => deviceId?.Trim() ?? "";

    private static int NormalizeDimension(int value, int fallback) => value > 0
        ? Math.Clamp(value, 1, short.MaxValue)
        : fallback;

    private static void UpdateUi(Action action)
    {
        if (Application.Current?.Dispatcher is not { HasShutdownStarted: false } dispatcher
            || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
    }
}
