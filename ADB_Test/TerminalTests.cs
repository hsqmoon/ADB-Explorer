using ADB_Explorer.Services.Terminal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ADB_Test;

[TestClass]
public sealed class TerminalTests
{
    [TestMethod]
    [TestCategory("Terminal")]
    public async Task TerminalSessionPreservesInputOrderSwitchesDeviceAndReconnectsTest()
    {
        List<TestTerminalTransport> transports = [];
        await using AdbTerminalSession session = new(
            (_, _, _) =>
            {
                var transport = new TestTerminalTransport();
                lock (transports)
                    transports.Add(transport);
                return Task.FromResult<ITerminalTransport>(transport);
            },
            TimeSpan.FromMilliseconds(50));

        object outputLock = new();
        StringBuilder output = new();
        session.Cleared += () =>
        {
            lock (outputLock)
                output.Clear();
        };
        session.OutputReceived += bytes =>
        {
            lock (outputLock)
                output.Append(Encoding.UTF8.GetString(bytes));
            return Task.CompletedTask;
        };

        await session.OpenAsync("device-a", 80, 24);
        TestTerminalTransport first = transports[0];
        Assert.IsTrue(await session.SendTextAsync("__ORDER_A__"));
        Assert.IsTrue(await session.SendTextAsync("__ORDER_B__"));
        await WaitUntilAsync(
            () => first.InputText.Contains("__ORDER_A____ORDER_B__", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));
        first.FailNextResize = true;
        Assert.IsTrue(await session.ResizeAsync(81, 25));
        Assert.IsTrue(await session.SendTextAsync("__AFTER_RESIZE_FAILURE__"));
        await WaitUntilAsync(
            () => first.InputText.Contains("__AFTER_RESIZE_FAILURE__", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));
        Assert.IsTrue(await session.InterruptAsync());
        Assert.IsTrue(await session.SendTextAsync("__AFTER_INTERRUPT__"));
        await WaitUntilAsync(
            () => first.InputText.Contains("__AFTER_RESIZE_FAILURE__\u0003__AFTER_INTERRUPT__", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2));

        await first.EmitAsync(Encoding.UTF8.GetBytes("__OLD_DEVICE__"));
        await session.SetDeviceAsync("device-b");
        Assert.IsTrue(first.IsDisposed);
        TestTerminalTransport second = transports[1];
        await second.EmitAsync(Encoding.UTF8.GetBytes("__NEW_DEVICE__"));
        await WaitUntilAsync(
            () =>
            {
                lock (outputLock)
                    return output.ToString().Contains("__NEW_DEVICE__", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        string switchedOutput;
        lock (outputLock)
            switchedOutput = output.ToString();
        StringAssert.Contains(switchedOutput, "__NEW_DEVICE__");
        Assert.IsFalse(switchedOutput.Contains("__OLD_DEVICE__", StringComparison.Ordinal));

        second.Exit(7);
        await WaitUntilAsync(
            () =>
            {
                lock (transports)
                    return transports.Count == 3;
            },
            TimeSpan.FromSeconds(2));
        Assert.IsTrue(second.IsDisposed);
        Assert.IsTrue(session.IsConnected);

        await session.CloseAsync();
        lock (transports)
            Assert.IsTrue(transports.All(transport => transport.IsDisposed));
    }

    [TestMethod]
    [TestCategory("Terminal")]
    [TestCategory("Performance")]
    public async Task TerminalSessionBoundsPendingInputTest()
    {
        TestTerminalTransport transport = null;
        await using AdbTerminalSession session = new(
            (_, _, _) =>
            {
                transport = new TestTerminalTransport();
                return Task.FromResult<ITerminalTransport>(transport);
            },
            TimeSpan.FromMilliseconds(50));

        await session.OpenAsync("input-device", 80, 24);
        transport.PauseInput();
        List<Task<bool>> writes = Enumerable.Range(0, 256)
            .Select(index => session.SendTextAsync(index.ToString("X2")))
            .ToList();

        try
        {
            await Task.Delay(100);
            Assert.IsTrue(writes.Any(task => !task.IsCompleted), "The input queue accepted unbounded pending writes.");
        }
        finally
        {
            transport.ResumeInput();
        }

        Assert.IsTrue((await Task.WhenAll(writes)).All(result => result));
        await WaitUntilAsync(() => transport.InputText.Length == 512, TimeSpan.FromSeconds(5));
        await session.CloseAsync();
    }

    [TestMethod]
    [TestCategory("Terminal")]
    [TestCategory("Performance")]
    public async Task TerminalSessionBatchesTenMegabytesWithBackpressureTest()
    {
        const int payloadSize = 10 * 1024 * 1024;
        TestTerminalTransport transport = null;
        await using AdbTerminalSession session = new(
            (_, _, _) =>
            {
                transport = new TestTerminalTransport();
                return Task.FromResult<ITerminalTransport>(transport);
            },
            TimeSpan.FromMilliseconds(50));

        long receivedBytes = 0;
        int batchCount = 0;
        int activeConsumers = 0;
        int maximumConsumers = 0;
        TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OutputReceived += async bytes =>
        {
            int active = Interlocked.Increment(ref activeConsumers);
            maximumConsumers = Math.Max(maximumConsumers, active);
            Interlocked.Increment(ref batchCount);
            long total = Interlocked.Add(ref receivedBytes, bytes.Length);
            await Task.Delay(2);
            Interlocked.Decrement(ref activeConsumers);
            if (total >= payloadSize)
                completed.TrySetResult(true);
        };

        await session.OpenAsync("performance-device", 120, 40);
        byte[] chunk = Enumerable.Repeat((byte)'x', 16 * 1024).ToArray();
        Task producer = Task.Run(async () =>
        {
            for (int sent = 0; sent < payloadSize; sent += chunk.Length)
                await transport.EmitAsync(chunk);
        });

        await Task.Delay(50);
        Assert.IsFalse(producer.IsCompleted, "The bounded output path did not apply backpressure.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(producer, completed.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        stopwatch.Stop();

        Assert.IsGreaterThanOrEqualTo(payloadSize, receivedBytes);
        Assert.AreEqual(1, maximumConsumers);
        Assert.IsLessThan(1000, batchCount);
        Assert.IsLessThan(15d, stopwatch.Elapsed.TotalSeconds);
        await session.CloseAsync();
    }

    [TestMethod]
    [TestCategory("Terminal")]
    [TestCategory("TerminalDevice")]
    public async Task AdbTerminalTransportSupportsResizeUnicodeTopAndInterruptTest()
    {
        string deviceId = Environment.GetEnvironmentVariable("ADB_TERMINAL_TEST_DEVICE");
        if (string.IsNullOrWhiteSpace(deviceId))
            Assert.Inconclusive("Set ADB_TERMINAL_TEST_DEVICE to run the device test.");

        int port = int.TryParse(Environment.GetEnvironmentVariable("ADB_TERMINAL_TEST_PORT"), out int configuredPort)
            ? configuredPort
            : 5037;
        IPEndPoint serverEndPoint = new(IPAddress.Loopback, port);
        await using AdbTerminalSession session = new(
            (serial, columns, rows) => AdbTerminalTransport.ConnectAsync(
                serverEndPoint,
                serial,
                columns,
                rows),
            TimeSpan.FromMilliseconds(500));

        object outputLock = new();
        StringBuilder output = new();
        Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
        session.Cleared += () =>
        {
            lock (outputLock)
            {
                output.Clear();
                decoder.Reset();
            }
        };
        session.OutputReceived += bytes =>
        {
            lock (outputLock)
            {
                char[] characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                int count = decoder.GetChars(bytes, 0, bytes.Length, characters, 0, false);
                output.Append(characters, 0, count);
            }

            return Task.CompletedTask;
        };

        string Snapshot()
        {
            lock (outputLock)
                return output.ToString();
        }

        static int Occurrences(string text, string value) =>
            text.Split(value, StringSplitOptions.None).Length - 1;

        await session.OpenAsync(deviceId, 80, 24);
        Assert.IsTrue(await session.SendTextAsync("printf '__ADB_READY__\\n'\r"), session.StatusText);
        await WaitUntilAsync(
            () => Occurrences(Snapshot(), "__ADB_READY__") >= 2,
            TimeSpan.FromSeconds(10));

        session.Clear();
        Assert.IsTrue(await session.SendTextAsync(
            "printf '__UNICODE__'; printf '\\344\\270\\255\\346\\226\\207'; printf '__END__\\n'\r"));
        await WaitUntilAsync(
            () => Snapshot().Contains("\u4E2D\u6587", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        StringAssert.Contains(Snapshot(), "__UNICODE__\u4E2D\u6587__END__");

        session.Clear();
        Assert.IsTrue(await session.ResizeAsync(101, 33));
        Assert.IsTrue(await session.SendTextAsync("stty size; echo __SIZE_END__\r"));
        await WaitUntilAsync(
            () => Regex.IsMatch(Snapshot(), @"\b33\s+101\b"),
            TimeSpan.FromSeconds(10));

        session.Clear();
        Assert.IsTrue(await session.SendBinaryAsync(new byte[] { 0x0c }));
        Assert.IsTrue(await session.SendTextAsync("echo __CTRL_L_DONE__\r"));
        await WaitUntilAsync(
            () => Occurrences(Snapshot(), "__CTRL_L_DONE__") >= 2,
            TimeSpan.FromSeconds(10));

        session.Clear();
        Assert.IsTrue(await session.SendTextAsync("top\r"));
        await WaitUntilAsync(() => Snapshot().Length > 500, TimeSpan.FromSeconds(10));
        Assert.IsTrue(await session.InterruptAsync());
        Assert.IsTrue(await session.SendTextAsync("echo __TOP_DONE__\r"));
        await WaitUntilAsync(
            () => Occurrences(Snapshot(), "__TOP_DONE__") >= 2,
            TimeSpan.FromSeconds(10));

        session.Clear();
        Assert.IsTrue(await session.SendTextAsync("vi /tmp/adb_explorer_terminal_test\r"));
        await WaitUntilAsync(() => Snapshot().Length > 200, TimeSpan.FromSeconds(10));
        Assert.IsTrue(await session.SendTextAsync(":q!\r"));
        Assert.IsTrue(await session.SendTextAsync("echo __VI_DONE__\r"));
        await WaitUntilAsync(
            () => Occurrences(Snapshot(), "__VI_DONE__") >= 2,
            TimeSpan.FromSeconds(10));

        session.Clear();
        Assert.IsTrue(await session.SendTextAsync("(echo __LESS_ACTIVE__; seq 1 200) | less\r"));
        await WaitUntilAsync(
            () => Occurrences(Snapshot(), "__LESS_ACTIVE__") >= 2,
            TimeSpan.FromSeconds(10));
        Assert.IsTrue(await session.SendTextAsync("q"));
        Assert.IsTrue(await session.SendTextAsync("echo __LESS_DONE__\r"));
        await WaitUntilAsync(
            () => Occurrences(Snapshot(), "__LESS_DONE__") >= 2,
            TimeSpan.FromSeconds(10));

        session.Clear();
        Assert.IsTrue(await session.SendTextAsync("sleep 30\r"));
        await Task.Delay(500);
        Stopwatch interruptTime = Stopwatch.StartNew();
        Assert.IsTrue(await session.InterruptAsync());
        Assert.IsTrue(await session.SendTextAsync("echo __INTERRUPTED__\r"));
        await WaitUntilAsync(
            () => Occurrences(Snapshot(), "__INTERRUPTED__") >= 2,
            TimeSpan.FromSeconds(8),
            () => Snapshot());
        interruptTime.Stop();
        Assert.IsLessThan(8d, interruptTime.Elapsed.TotalSeconds);

        await session.CloseAsync();
        Assert.IsFalse(session.IsConnected);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        Func<string> diagnostic = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
                Assert.Fail($"The condition did not become true before the timeout. {diagnostic?.Invoke()}");
            await Task.Delay(20);
        }
    }

    private sealed class TestTerminalTransport : ITerminalTransport
    {
        private readonly Channel<byte[]> output = Channel.CreateBounded<byte[]>(64);
        private readonly ConcurrentQueue<byte[]> input = new();
        private readonly TaskCompletionSource<int> exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> inputWriteGate = CreateOpenGate();
        private byte[] pendingOutput;
        private int pendingOffset;

        public bool IsDisposed { get; private set; }
        public bool FailNextResize { get; set; }
        public ConcurrentQueue<(int Columns, int Rows)> Sizes { get; } = new();
        public string InputText => Encoding.UTF8.GetString(input.SelectMany(bytes => bytes).ToArray());

        public async Task WriteInputAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            await inputWriteGate.Task.WaitAsync(cancellationToken);
            input.Enqueue(value.ToArray());
        }

        public Task InterruptAsync(CancellationToken cancellationToken)
        {
            input.Enqueue(new byte[] { 0x03 });
            return Task.CompletedTask;
        }

        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
        {
            if (FailNextResize)
            {
                FailNextResize = false;
                throw new IOException("Simulated resize failure.");
            }

            Sizes.Enqueue((columns, rows));
            return Task.CompletedTask;
        }

        public async Task<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            while (pendingOutput is null)
            {
                if (!await output.Reader.WaitToReadAsync(cancellationToken))
                    return 0;
                if (output.Reader.TryRead(out byte[] next))
                    pendingOutput = next;
            }

            int length = Math.Min(destination.Length, pendingOutput.Length - pendingOffset);
            pendingOutput.AsMemory(pendingOffset, length).CopyTo(destination);
            pendingOffset += length;
            if (pendingOffset == pendingOutput.Length)
            {
                pendingOutput = null;
                pendingOffset = 0;
            }

            return length;
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            exit.Task.WaitAsync(cancellationToken);

        public ValueTask EmitAsync(byte[] value) => output.Writer.WriteAsync(value);

        public void Exit(int exitCode)
        {
            exit.TrySetResult(exitCode);
            output.Writer.TryComplete();
        }

        public void PauseInput() => inputWriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResumeInput() => inputWriteGate.TrySetResult(true);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            output.Writer.TryComplete();
            exit.TrySetResult(-1);
            return ValueTask.CompletedTask;
        }

        private static TaskCompletionSource<bool> CreateOpenGate()
        {
            TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult(true);
            return gate;
        }
    }
}
