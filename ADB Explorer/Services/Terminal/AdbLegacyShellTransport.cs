using System.Net;
using System.Net.Sockets;

namespace ADB_Explorer.Services.Terminal;

internal sealed class AdbLegacyShellTransport : ITerminalTransport
{
    private const string PTY_MARKER = "__ADB_EXPLORER_PTY_8F27C4__";
    private const int MAX_HANDSHAKE_SIZE = 64 * 1024;

    private readonly IPEndPoint serverEndPoint;
    private readonly string deviceId;
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim controlMutex = new(1, 1);
    private readonly TaskCompletionSource<int> exitCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[] pendingOutput;
    private int pendingOutputOffset;
    private string terminalPath;
    private int shellProcessId;
    private int disposed;

    private AdbLegacyShellTransport(IPEndPoint serverEndPoint, string deviceId, TcpClient client)
    {
        this.serverEndPoint = serverEndPoint;
        this.deviceId = deviceId;
        this.client = client;
        stream = client.GetStream();
    }

    public static async Task<ITerminalTransport> ConnectAsync(
        IPEndPoint serverEndPoint,
        string deviceId,
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverEndPoint);
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("The ADB device ID is empty.", nameof(deviceId));

        columns = Math.Clamp(columns, 1, short.MaxValue);
        rows = Math.Clamp(rows, 1, short.MaxValue);
        TcpClient client = new(serverEndPoint.AddressFamily) { NoDelay = true };
        try
        {
            await client.ConnectAsync(
                serverEndPoint.Address,
                serverEndPoint.Port,
                cancellationToken).ConfigureAwait(false);

            var transport = new AdbLegacyShellTransport(serverEndPoint, deviceId, client);
            await AdbServerProtocol.SendServiceRequestAsync(
                transport.stream,
                $"host:transport:{deviceId}",
                cancellationToken).ConfigureAwait(false);

            await AdbServerProtocol.SendServiceRequestAsync(
                transport.stream,
                "shell:",
                cancellationToken).ConfigureAwait(false);
            string command = "stty -echo; "
                + "printf '\\137\\137ADB\\137EXPLORER\\137PTY\\1378F27C4\\137\\137'; "
                + "printf '%s %s\\n' \"$(readlink /proc/self/fd/0)\" \"$$\"; "
                + $"stty rows {rows} cols {columns}; "
                + "export TERM=xterm-256color; stty echo; exec sh -i\n";
            await transport.stream.WriteAsync(
                Encoding.ASCII.GetBytes(command),
                cancellationToken).ConfigureAwait(false);
            await transport.ReadHandshakeAsync(cancellationToken).ConfigureAwait(false);
            return transport;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        try
        {
            await stream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            exitCompletion.TrySetResult(-1);
            throw;
        }
    }

    public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        columns = Math.Clamp(columns, 1, short.MaxValue);
        rows = Math.Clamp(rows, 1, short.MaxValue);

        await controlMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            await ExecuteShellCommandAsync(
                $"stty rows {rows} cols {columns} < {terminalPath}",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            controlMutex.Release();
        }
    }

    public async Task InterruptAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        await stream.WriteAsync(new byte[] { 0x03 }, cancellationToken).ConfigureAwait(false);
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);

        await controlMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            string command = $"read pid comm state parent group session tty foreground rest < /proc/{shellProcessId}/stat; "
                + $"if [ \"$foreground\" -gt 0 ] && [ \"$foreground\" -ne {shellProcessId} ]; "
                + "then kill -TERM -\"$foreground\"; fi";
            await ExecuteShellCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            controlMutex.Release();
        }
    }

    public async Task<int> ReadAsync(Memory<byte> output, CancellationToken cancellationToken)
    {
        if (output.IsEmpty)
            return 0;

        if (pendingOutput is not null)
        {
            int length = Math.Min(output.Length, pendingOutput.Length - pendingOutputOffset);
            pendingOutput.AsMemory(pendingOutputOffset, length).CopyTo(output);
            pendingOutputOffset += length;
            if (pendingOutputOffset == pendingOutput.Length)
            {
                pendingOutput = null;
                pendingOutputOffset = 0;
            }

            return length;
        }

        try
        {
            int read = await stream.ReadAsync(output, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                exitCompletion.TrySetResult(0);
            return read;
        }
        catch (IOException)
        {
            exitCompletion.TrySetResult(-1);
            throw;
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
        exitCompletion.Task.WaitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return ValueTask.CompletedTask;

        stream.Dispose();
        client.Dispose();
        exitCompletion.TrySetResult(-1);
        return ValueTask.CompletedTask;
    }

    private async Task ReadHandshakeAsync(CancellationToken cancellationToken)
    {
        byte[] marker = Encoding.ASCII.GetBytes(PTY_MARKER);
        using MemoryStream captured = new();
        byte[] buffer = new byte[1024];

        while (captured.Length < MAX_HANDSHAKE_SIZE)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("The legacy ADB shell closed before reporting its PTY.");

            captured.Write(buffer, 0, read);
            ReadOnlySpan<byte> bytes = captured.GetBuffer().AsSpan(0, checked((int)captured.Length));
            int markerOffset = bytes.IndexOf(marker);
            if (markerOffset < 0)
                continue;

            int pathOffset = markerOffset + marker.Length;
            int lineLength = bytes[pathOffset..].IndexOf((byte)'\n');
            if (lineLength < 0)
                continue;

            string[] terminalDetails = Encoding.ASCII
                .GetString(bytes.Slice(pathOffset, lineLength))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (terminalDetails.Length != 2
                || !int.TryParse(terminalDetails[1], NumberStyles.None, CultureInfo.InvariantCulture, out shellProcessId)
                || shellProcessId <= 1)
            {
                throw new IOException("The legacy ADB shell returned invalid PTY process details.");
            }

            terminalPath = terminalDetails[0];
            if (!Regex.IsMatch(terminalPath, "^/dev/pts/[0-9]+$", RegexOptions.CultureInvariant))
                throw new IOException($"The legacy ADB shell returned an invalid PTY path: {terminalPath}");

            int suffixOffset = pathOffset + lineLength + 1;
            int pendingLength = markerOffset + bytes.Length - suffixOffset;
            if (pendingLength > 0)
            {
                pendingOutput = new byte[pendingLength];
                bytes[..markerOffset].CopyTo(pendingOutput);
                bytes[suffixOffset..].CopyTo(pendingOutput.AsSpan(markerOffset));
            }

            return;
        }

        throw new IOException("The legacy ADB shell did not report its PTY.");
    }

    private async Task ExecuteShellCommandAsync(string command, CancellationToken cancellationToken)
    {
        using TcpClient commandClient = new(serverEndPoint.AddressFamily) { NoDelay = true };
        await commandClient.ConnectAsync(
            serverEndPoint.Address,
            serverEndPoint.Port,
            cancellationToken).ConfigureAwait(false);
        NetworkStream commandStream = commandClient.GetStream();
        await AdbServerProtocol.SendServiceRequestAsync(
            commandStream,
            $"host:transport:{deviceId}",
            cancellationToken).ConfigureAwait(false);
        await AdbServerProtocol.SendServiceRequestAsync(
            commandStream,
            $"shell:{command}",
            cancellationToken).ConfigureAwait(false);

        byte[] buffer = new byte[256];
        while (await commandStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0)
        { }
    }
}
