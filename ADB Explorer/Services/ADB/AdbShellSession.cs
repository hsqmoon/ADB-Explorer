using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;

namespace ADB_Explorer.Services;

public sealed class AdbShellSession : ViewModelBase, IDisposable
{
    private const int MAX_TERMINAL_TEXT_LENGTH = 200_000;
    private const int MAX_COMMAND_HISTORY = 100;
    private const int TAB_WIDTH = 4;

    private enum ParserState
    {
        Text,
        Escape,
        Csi,
        Osc,
        OscEscape,
    }

    private enum ShellState
    {
        Unknown,
        Prompt,
        Editing,
        Running,
    }

    private readonly SemaphoreSlim sessionMutex = new(1, 1);
    private readonly List<string> commandHistory = [];
    private readonly StringBuilder rawOutputBuffer = new();
    private readonly StringBuilder terminalTextBuffer = new();
    private readonly StringBuilder currentLineBuffer = new();

    private Process shellProcess;
    private CancellationTokenSource sessionCts;
    private StreamWriter shellInput;
    private ParserState parserState;
    private string outputText = "";
    private string pendingInput = "";
    private string statusText = "Waiting for a device from the main view...";
    private string connectedDeviceId = "";
    private bool isConnected;
    private bool isStarting;
    private bool isShuttingDown;
    private bool followMainDevice;
    private bool suppressAutoReconnect;
    private bool pendingCarriageReturn;
    private int historyIndex;
    private ShellState shellState = ShellState.Unknown;
    private string promptPrefix = "";

    public event Action<string, bool> OutputChunkReceived;
    public event Action Cleared;

    public string OutputText
    {
        get => outputText;
        private set => Set(ref outputText, value);
    }

    public string PendingInput
    {
        get => pendingInput;
        set => Set(ref pendingInput, value);
    }

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

    public string SessionTitle
        => string.IsNullOrWhiteSpace(ConnectedDeviceId)
            ? Strings.Resources.S_TERMINAL
            : $"{Strings.Resources.S_TERMINAL} - {ConnectedDeviceId}";

    public bool IsConnected
    {
        get => isConnected;
        private set
        {
            if (Set(ref isConnected, value))
            {
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanDisconnect));
                OnPropertyChanged(nameof(CanInterrupt));
            }
        }
    }

    public bool IsStarting
    {
        get => isStarting;
        private set
        {
            if (Set(ref isStarting, value))
            {
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanDisconnect));
                OnPropertyChanged(nameof(CanInterrupt));
            }
        }
    }

    public bool CanConnect => !IsStarting;
    public bool CanDisconnect => IsConnected || IsStarting;
    public bool CanInterrupt => IsConnected;
    public bool IsCommandRunning => shellState is ShellState.Running;
    public bool IsFollowingMainDevice => followMainDevice;

    public void EnableMainDeviceFollow()
    {
        followMainDevice = true;
    }

    public async Task EnsureConnectedToCurrentDeviceAsync()
    {
        if (!followMainDevice)
            return;

        var currentDevice = Data.DevicesObject.Current;
        var deviceId = currentDevice?.Status is AbstractDevice.DeviceStatus.Ok
            ? currentDevice.ID
            : null;

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            await CloseAsync();
            UpdateStatus("Waiting for a device from the main view...");
            return;
        }

        if (IsConnected && ConnectedDeviceId == deviceId && shellProcess is { HasExited: false })
            return;

        await ConnectAsync(deviceId);
    }

    public async Task ConnectAsync(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            UpdateStatus("Waiting for a device from the main view...");
            return;
        }

        await sessionMutex.WaitAsync();
        try
        {
            suppressAutoReconnect = false;
            CloseCore(updateStatus: false);

            IsStarting = true;
            UpdateStatus($"Attaching terminal to {deviceId}...");
            TerminalLog.PrintLine($"connect.begin device={deviceId}");
            LogDiagnostic("connect.begin", $"device={deviceId}");

            var process = ADBService.StartInteractiveAdbShellProcess(deviceId);

            shellProcess = process;
            shellInput = process.StandardInput;
            sessionCts = new();
            ConnectedDeviceId = deviceId;
            IsConnected = true;
            historyIndex = commandHistory.Count;
            UpdateStatus($"Attached to {deviceId}");
            TerminalLog.PrintLine($"connect.ok device={deviceId}; pid={SafeGetPid(process)}");
            LogDiagnostic("connect.ok", $"device={deviceId}; pid={SafeGetPid(process)}; state={DescribeProcess(process)}");

            _ = PumpReaderAsync(process, process.StandardOutput, isError: false, sessionCts.Token);
            _ = PumpReaderAsync(process, process.StandardError, isError: true, sessionCts.Token);
            _ = ObserveExitAsync(process, deviceId, sessionCts.Token);
        }
        catch (Exception ex)
        {
            CloseCore(updateStatus: false);
            AppendOutput($"\n[terminal] {ex.Message}\n");
            UpdateStatus($"Failed to connect to {deviceId}");
            TerminalLog.PrintLine($"connect.fail device={deviceId}; ex={ex.GetType().Name}; msg={ex.Message}");
            LogDiagnostic("connect.fail", $"device={deviceId}; ex={ex.GetType().Name}; msg={ex.Message}");
        }
        finally
        {
            IsStarting = false;
            sessionMutex.Release();
        }
    }

    public async Task SendPendingInputAsync()
    {
        var input = PendingInput;
        PendingInput = "";
        await SendInputAsync(input);
    }

    public async Task SendInputAsync(string input)
    {
        if (!IsConnected || shellProcess is null || shellProcess.HasExited || shellInput is null)
            await EnsureConnectedToCurrentDeviceAsync();

        if (!IsConnected || shellInput is null)
            return;

        input ??= "";

        if (!string.IsNullOrWhiteSpace(input))
        {
            commandHistory.Add(input);
            while (commandHistory.Count > MAX_COMMAND_HISTORY)
                commandHistory.RemoveAt(0);
        }

        historyIndex = commandHistory.Count;
        Data.AddCommandLog($"shell[{ConnectedDeviceId}]> {input}");
        TerminalLog.PrintInput(input);

        await shellInput.WriteLineAsync(input);
        await shellInput.FlushAsync();
    }

    public async Task SendLiteralAsync(string input)
    {
        if (string.IsNullOrEmpty(input))
            return;

        if (!IsConnected || shellProcess is null || shellProcess.HasExited || shellInput is null)
            await EnsureConnectedToCurrentDeviceAsync();

        if (!IsConnected || shellInput is null)
            return;

        Data.AddCommandLog($"shell[{ConnectedDeviceId}]> <literal:{input.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")}>");
        TerminalLog.PrintInput($"<literal:{input.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")}>");
        if (input.Contains('\u0003'))
            LogDiagnostic("input.ctrl_c.literal", $"device={ConnectedDeviceId}; state.before={DescribeProcess(shellProcess)}");

        NoteLocalInput(input);
        await shellInput.WriteAsync(input);
        await shellInput.FlushAsync();
    }

    public async Task InterruptAsync()
    {
        if (!IsConnected || shellInput is null)
            return;

        if (IsCommandRunning)
        {
            Data.AddCommandLog($"shell[{ConnectedDeviceId}]> <Ctrl+C>");
            TerminalLog.PrintInput("<Ctrl+C>");
            LogDiagnostic("interrupt.send", $"device={ConnectedDeviceId}; pid={SafeGetPid(shellProcess)}; state.before={DescribeProcess(shellProcess)}; shellState={shellState}");

            await shellInput.WriteAsync("\u0003");
            await shellInput.FlushAsync();

            var process = shellProcess;
            var deviceId = ConnectedDeviceId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1200);
                    LogDiagnostic("interrupt.after_1.2s", $"device={deviceId}; pid={SafeGetPid(process)}; state={DescribeProcess(process)}; shellState={shellState}");
                }
                catch
                { }
            });
        }
        else
        {
            Data.AddCommandLog($"shell[{ConnectedDeviceId}]> <Ctrl+C translated>");
            TerminalLog.PrintInput("<Ctrl+C translated>");
            LogDiagnostic("interrupt.translated", $"device={ConnectedDeviceId}; pid={SafeGetPid(shellProcess)}; shellState={shellState}");

            // In piped adb shell sessions, sending ETX at the prompt exits the shell.
            // Use line-kill instead so the session stays alive.
            await shellInput.WriteAsync("\u0015");
            await shellInput.FlushAsync();
            shellState = ShellState.Prompt;
        }
    }

    public void Clear()
    {
        rawOutputBuffer.Clear();
        terminalTextBuffer.Clear();
        currentLineBuffer.Clear();
        parserState = ParserState.Text;
        pendingCarriageReturn = false;
        shellState = ShellState.Unknown;
        promptPrefix = "";
        OutputText = "";
        Cleared?.Invoke();
    }

    public void NavigateHistory(int direction)
    {
        if (commandHistory.Count == 0)
            return;

        historyIndex = Math.Clamp(historyIndex + direction, 0, commandHistory.Count);
        PendingInput = historyIndex == commandHistory.Count
            ? ""
            : commandHistory[historyIndex];
    }

    public async Task CloseAsync()
    {
        await sessionMutex.WaitAsync();
        try
        {
            suppressAutoReconnect = true;
            CloseCore();
        }
        finally
        {
            sessionMutex.Release();
        }
    }

    public void Shutdown()
    {
        isShuttingDown = true;
        suppressAutoReconnect = true;

        bool lockTaken = false;
        try
        {
            lockTaken = sessionMutex.Wait(250);
        }
        catch
        { }

        try
        {
            CloseCore();
        }
        finally
        {
            if (lockTaken)
                sessionMutex.Release();
        }
    }

    private void CloseCore(bool updateStatus = true)
    {
        var process = shellProcess;
        var cts = sessionCts;
        var writer = shellInput;
        var deviceId = ConnectedDeviceId;

        shellProcess = null;
        sessionCts = null;
        shellInput = null;

        IsStarting = false;
        IsConnected = false;
        ConnectedDeviceId = "";
        shellState = ShellState.Unknown;
        promptPrefix = "";

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            { }
        }

        if (writer is not null)
        {
            try
            {
                writer.Close();
            }
            catch
            { }
        }

        if (process is not null)
        {
            LogDiagnostic("close.begin", $"device={deviceId}; pid={SafeGetPid(process)}; state.before={DescribeProcess(process)}");
            try
            {
                if (!process.HasExited)
                    ProcessHandling.KillProcess(process);
            }
            catch
            { }

            try
            {
                process.Dispose();
            }
            catch
            { }

            LogDiagnostic("close.end", $"device={deviceId}; pid={SafeGetPid(process)}; state.after={DescribeProcess(process)}");
        }

        cts?.Dispose();

        if (updateStatus)
            UpdateStatus("Terminal detached.");
    }

    private async Task ObserveExitAsync(Process process, string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogDiagnostic("process.wait.cancelled", $"device={deviceId}; pid={SafeGetPid(process)}");
            return;
        }
        catch (ObjectDisposedException)
        {
            LogDiagnostic("process.wait.disposed", $"device={deviceId}; pid={SafeGetPid(process)}");
            return;
        }

        if (!ReferenceEquals(process, shellProcess))
            return;

        Dispatch(() =>
        {
            shellProcess = null;
            shellInput = null;
            sessionCts = null;
            IsConnected = false;
            IsStarting = false;
            ConnectedDeviceId = "";

            var exitCode = process.ExitCode;
            TerminalLog.PrintLine($"process.exit device={deviceId}; pid={SafeGetPid(process)}; exitCode={exitCode}");
            LogDiagnostic("process.exit", $"device={deviceId}; pid={SafeGetPid(process)}; exitCode={exitCode}");
            if (ShouldAutoReconnect(deviceId))
            {
                UpdateStatus($"Shell on {deviceId} closed. Reattaching...");
                _ = ScheduleReconnectAsync(deviceId);
                return;
            }

            UpdateStatus(exitCode == 0
                ? $"Shell session on {deviceId} closed."
                : $"Shell session on {deviceId} ended ({exitCode}).");
        });
    }

    private async Task PumpReaderAsync(Process process, StreamReader reader, bool isError, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                var chunk = new string(buffer, 0, read);
                Dispatch(() =>
                {
                    if (!ReferenceEquals(process, shellProcess))
                        return;

                    OutputChunkReceived?.Invoke(chunk, isError);
                    TerminalLog.PrintOutput(chunk, isError);
                    AppendOutput(chunk, isError);
                    Data.RuntimeSettings.LastServerResponse = DateTime.Now;
                });
            }

            LogDiagnostic(isError ? "stderr.eof" : "stdout.eof", $"device={ConnectedDeviceId}; pid={SafeGetPid(process)}; state={DescribeProcess(process)}");
        }
        catch (ObjectDisposedException)
        {
            LogDiagnostic(isError ? "stderr.disposed" : "stdout.disposed", $"device={ConnectedDeviceId}; pid={SafeGetPid(process)}");
        }
        catch (InvalidOperationException)
        {
            LogDiagnostic(isError ? "stderr.invalid_op" : "stdout.invalid_op", $"device={ConnectedDeviceId}; pid={SafeGetPid(process)}");
        }
        catch (IOException)
        {
            LogDiagnostic(isError ? "stderr.io_exception" : "stdout.io_exception", $"device={ConnectedDeviceId}; pid={SafeGetPid(process)}");
        }
        catch (Exception ex)
        {
            LogDiagnostic(isError ? "stderr.exception" : "stdout.exception", $"device={ConnectedDeviceId}; pid={SafeGetPid(process)}; ex={ex.GetType().Name}; msg={ex.Message}");
            Dispatch(() =>
            {
                if (isShuttingDown)
                    return;

                AppendOutput($"\n[terminal] {ex.Message}\n", true);
            });
        }
    }

    private void AppendOutput(string text, bool isError = false)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (isError)
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        rawOutputBuffer.Append(text);
        string rawTrimmed = TrimText(rawOutputBuffer.ToString());
        if (rawTrimmed.Length < rawOutputBuffer.Length)
        {
            rawOutputBuffer.Clear();
            rawOutputBuffer.Append(rawTrimmed);
        }

        OutputText = rawTrimmed;

        foreach (char c in text)
            AppendChar(c);

        TrimVisibleOutputBuffers();
        UpdateShellState();
    }

    private void UpdateStatus(string text)
    {
        StatusText = text;
    }

    private static string TrimText(string text)
    {
        if (text.Length <= MAX_TERMINAL_TEXT_LENGTH)
            return text;

        int startIndex = text.Length - MAX_TERMINAL_TEXT_LENGTH;
        int lineStart = text.IndexOf(Environment.NewLine, startIndex, StringComparison.Ordinal);

        return lineStart >= 0
            ? text[(lineStart + Environment.NewLine.Length)..]
            : text[startIndex..];
    }

    public void Dispose()
    {
        Shutdown();
    }

    private void AppendChar(char c)
    {
        if (pendingCarriageReturn)
        {
            if (c == '\n')
            {
                pendingCarriageReturn = false;
                FlushCurrentLine();
                return;
            }

            if (c == '\r')
                return;

            pendingCarriageReturn = false;
            currentLineBuffer.Clear();
        }

        switch (parserState)
        {
            case ParserState.Escape:
                HandleEscapeState(c);
                return;

            case ParserState.Csi:
                if (IsAnsiTerminator(c))
                    parserState = ParserState.Text;
                return;

            case ParserState.Osc:
                if (c == '\u0007')
                {
                    parserState = ParserState.Text;
                    return;
                }

                if (c == '\u001B')
                {
                    parserState = ParserState.OscEscape;
                    return;
                }

                return;

            case ParserState.OscEscape:
                parserState = c == '\\'
                    ? ParserState.Text
                    : ParserState.Osc;
                return;
        }

        switch (c)
        {
            case '\u001B':
                parserState = ParserState.Escape;
                return;

            case '\r':
                pendingCarriageReturn = true;
                return;

            case '\n':
                FlushCurrentLine();
                return;

            case '\b':
                RemoveLastVisibleChar();
                return;

            case '\t':
                int spaces = TAB_WIDTH - (currentLineBuffer.Length % TAB_WIDTH);
                currentLineBuffer.Append(' ', spaces);
                return;

            default:
                if (!char.IsControl(c))
                    currentLineBuffer.Append(c);
                return;
        }
    }

    private void HandleEscapeState(char c)
    {
        parserState = c switch
        {
            '[' => ParserState.Csi,
            ']' => ParserState.Osc,
            _ => ParserState.Text,
        };
    }

    private static bool IsAnsiTerminator(char c)
    {
        return c is >= '@' and <= '~';
    }

    private void FlushCurrentLine()
    {
        terminalTextBuffer.Append(currentLineBuffer);
        terminalTextBuffer.AppendLine();
        currentLineBuffer.Clear();
    }

    private void RemoveLastVisibleChar()
    {
        if (currentLineBuffer.Length > 0)
        {
            currentLineBuffer.Length--;
            return;
        }

        if (terminalTextBuffer.Length > 0 && terminalTextBuffer[^1] is not '\n')
            terminalTextBuffer.Length--;
    }

    private void TrimVisibleOutputBuffers()
    {
        string combined = terminalTextBuffer.ToString() + currentLineBuffer;
        string trimmed = TrimText(combined);

        if (trimmed.Length < combined.Length)
            RebuildBuffersFromTrimmedText(trimmed);
    }

    private void UpdateShellState()
    {
        var current = currentLineBuffer.ToString();

        if (LooksLikePrompt(current))
        {
            shellState = ShellState.Prompt;
            promptPrefix = current;
            return;
        }

        if (!string.IsNullOrEmpty(promptPrefix) && current.StartsWith(promptPrefix, StringComparison.Ordinal))
        {
            shellState = ShellState.Editing;
            return;
        }

        if (shellState is not ShellState.Unknown)
            shellState = ShellState.Running;
    }

    private static bool LooksLikePrompt(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        return line.EndsWith("# ", StringComparison.Ordinal)
               || line.EndsWith("$ ", StringComparison.Ordinal)
               || line.EndsWith("> ", StringComparison.Ordinal);
    }

    private void NoteLocalInput(string input)
    {
        if (string.IsNullOrEmpty(input) || !IsConnected)
            return;

        if (input.Contains('\n') || input.Contains('\r'))
        {
            if (shellState is ShellState.Prompt or ShellState.Editing)
                shellState = ShellState.Running;

            return;
        }

        if (input.Contains('\u0003'))
            return;

        if (shellState is ShellState.Prompt or ShellState.Editing)
            shellState = ShellState.Editing;
    }

    private void RebuildBuffersFromTrimmedText(string text)
    {
        terminalTextBuffer.Clear();
        currentLineBuffer.Clear();

        int lastNewLine = text.LastIndexOf('\n');
        if (lastNewLine >= 0)
        {
            terminalTextBuffer.Append(text[..(lastNewLine + 1)]);
            currentLineBuffer.Append(text[(lastNewLine + 1)..]);
        }
        else
            currentLineBuffer.Append(text);
    }

    private bool ShouldAutoReconnect(string deviceId)
    {
        if (isShuttingDown || suppressAutoReconnect)
            return false;

        if (!followMainDevice)
            return false;

        var currentDevice = Data.DevicesObject.Current;
        if (currentDevice?.Status is not AbstractDevice.DeviceStatus.Ok)
            return false;

        var currentDeviceId = currentDevice.ID;
        return string.Equals(currentDeviceId, deviceId, StringComparison.Ordinal);
    }

    private async Task ScheduleReconnectAsync(string deviceId)
    {
        try
        {
            await Task.Delay(1200);
        }
        catch
        {
            return;
        }

        if (!ShouldAutoReconnect(deviceId))
            return;

        LogDiagnostic("reconnect.begin", $"device={deviceId}");
        await EnsureConnectedToCurrentDeviceAsync();
    }

    private static void Dispatch(Action action)
    {
        if (App.Current?.Dispatcher is not Dispatcher dispatcher || dispatcher.CheckAccess())
            action();
        else
            _ = dispatcher.BeginInvoke(action);
    }

    private static int SafeGetPid(Process process)
    {
        try
        {
            return process?.Id ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string DescribeProcess(Process process)
    {
        if (process is null)
            return "null";

        try
        {
            return process.HasExited
                ? $"exited:{process.ExitCode}"
                : "running";
        }
        catch (InvalidOperationException)
        {
            return "no-process";
        }
        catch (Exception ex)
        {
            return $"state-error:{ex.GetType().Name}";
        }
    }

    private static void LogDiagnostic(string eventName, string details)
    {
        _ = eventName;
        _ = details;
    }
}

