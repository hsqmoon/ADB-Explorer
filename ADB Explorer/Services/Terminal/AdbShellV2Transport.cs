using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ADB_Explorer.Services.Terminal;

internal interface ITerminalTransport : IAsyncDisposable
{
    Task WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken);
    Task InterruptAsync(CancellationToken cancellationToken);
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken);
    Task<int> ReadAsync(Memory<byte> output, CancellationToken cancellationToken);
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class AdbShellV2Transport : ITerminalTransport
{
    private const byte ID_STDIN = 0;
    private const byte ID_STDOUT = 1;
    private const byte ID_STDERR = 2;
    private const byte ID_EXIT = 3;
    private const byte ID_WINDOW_SIZE_CHANGE = 5;
    private const int SHELL_PACKET_HEADER_SIZE = 5;
    private const int MAX_SHELL_PACKET_SIZE = 1024 * 1024;
    private const int MAX_INPUT_PACKET_SIZE = 64 * 1024;

    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim writeMutex = new(1, 1);
    private readonly TaskCompletionSource<int> exitCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[] pendingOutput;
    private int pendingOutputOffset;
    private int disposed;

    private AdbShellV2Transport(TcpClient client)
    {
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

        TcpClient client = new(serverEndPoint.AddressFamily) { NoDelay = true };
        try
        {
            await client.ConnectAsync(
                serverEndPoint.Address,
                serverEndPoint.Port,
                cancellationToken).ConfigureAwait(false);

            var transport = new AdbShellV2Transport(client);
            await AdbServerProtocol.SendServiceRequestAsync(
                transport.stream,
                $"host:transport:{deviceId}",
                cancellationToken).ConfigureAwait(false);
            await AdbServerProtocol.SendServiceRequestAsync(
                transport.stream,
                "shell,v2,TERM=xterm-256color,pty:",
                cancellationToken).ConfigureAwait(false);
            await transport.ResizeAsync(columns, rows, cancellationToken).ConfigureAwait(false);
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
        for (int offset = 0; offset < input.Length; offset += MAX_INPUT_PACKET_SIZE)
        {
            int length = Math.Min(MAX_INPUT_PACKET_SIZE, input.Length - offset);
            await WritePacketAsync(
                ID_STDIN,
                input.Slice(offset, length),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task InterruptAsync(CancellationToken cancellationToken) =>
        WritePacketAsync(ID_STDIN, new byte[] { 0x03 }, cancellationToken);

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        columns = Math.Clamp(columns, 1, short.MaxValue);
        rows = Math.Clamp(rows, 1, short.MaxValue);
        byte[] size = Encoding.ASCII.GetBytes($"{rows}x{columns},0x0\0");
        return WritePacketAsync(ID_WINDOW_SIZE_CHANGE, size, cancellationToken);
    }

    public async Task<int> ReadAsync(Memory<byte> output, CancellationToken cancellationToken)
    {
        if (output.IsEmpty)
            return 0;

        while (true)
        {
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

            byte[] header = new byte[SHELL_PACKET_HEADER_SIZE];
            try
            {
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                exitCompletion.TrySetResult(-1);
                return 0;
            }
            catch (IOException)
            {
                exitCompletion.TrySetResult(-1);
                throw;
            }

            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
            if (payloadLength < 0 || payloadLength > MAX_SHELL_PACKET_SIZE)
                throw new InvalidDataException($"Invalid ADB shell packet length: {payloadLength}.");

            byte[] payload = new byte[payloadLength];
            if (payloadLength > 0)
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

            switch (header[0])
            {
                case ID_STDOUT:
                case ID_STDERR:
                    if (payloadLength == 0)
                        continue;
                    pendingOutput = payload;
                    break;

                case ID_EXIT:
                    int exitCode = payloadLength > 0 ? payload[0] : -1;
                    exitCompletion.TrySetResult(exitCode);
                    return 0;
            }
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
        writeMutex.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task WritePacketAsync(
        byte id,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        byte[] packet = new byte[SHELL_PACKET_HEADER_SIZE + payload.Length];
        packet[0] = id;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1), payload.Length);
        payload.CopyTo(packet.AsMemory(SHELL_PACKET_HEADER_SIZE));

        await writeMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            exitCompletion.TrySetResult(-1);
            throw;
        }
        finally
        {
            writeMutex.Release();
        }
    }

}

internal static class AdbServerProtocol
{
    public static async Task SendServiceRequestAsync(
        NetworkStream stream,
        string service,
        CancellationToken cancellationToken)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        if (serviceBytes.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(service), "ADB service request is too long.");

        byte[] request = new byte[4 + serviceBytes.Length];
        Encoding.ASCII.GetBytes(serviceBytes.Length.ToString("X4", CultureInfo.InvariantCulture), request);
        serviceBytes.CopyTo(request, 4);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        byte[] status = new byte[4];
        await stream.ReadExactlyAsync(status, cancellationToken).ConfigureAwait(false);
        string response = Encoding.ASCII.GetString(status);
        if (response == "OKAY")
            return;
        if (response != "FAIL")
            throw new IOException($"Unexpected ADB server response: {response}.");

        byte[] lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(
                Encoding.ASCII.GetString(lengthBytes),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out int errorLength)
            || errorLength < 0
            || errorLength > ushort.MaxValue)
        {
            throw new IOException("ADB server returned an invalid error response.");
        }

        byte[] error = new byte[errorLength];
        await stream.ReadExactlyAsync(error, cancellationToken).ConfigureAwait(false);
        throw new IOException(Encoding.UTF8.GetString(error));
    }
}
