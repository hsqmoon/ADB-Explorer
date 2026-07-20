using ADB_Explorer.Services.Terminal;
using Microsoft.Web.WebView2.Core;

namespace ADB_Explorer.Controls;

public partial class AdbTerminalControl : UserControl
{
    private const string TERMINAL_HOST_NAME = "terminal.adb-explorer.invalid";
    private const int DEFAULT_COLUMNS = 80;
    private const int DEFAULT_ROWS = 24;

    private readonly SemaphoreSlim lifecycleMutex = new(1, 1);
    private readonly SemaphoreSlim webViewMutex = new(1, 1);
    private TaskCompletionSource<bool> pageReadyCompletion = CreateCompletion<bool>();
    private TaskCompletionSource<int> outputAcknowledgement;
    private bool webViewConfigured;
    private bool pageReady;
    private bool isOpen;
    private bool isWebFocused;
    private bool isDisposed;
    private int outputSequence;
    private int outputEpoch;
    private int recoveryScheduled;
    private int columns = DEFAULT_COLUMNS;
    private int rows = DEFAULT_ROWS;
    private string requestedDeviceId = "";

    public AdbTerminalControl()
    {
        InitializeComponent();
        Session.OutputReceived += Session_OutputReceivedAsync;
        Session.Cleared += Session_Cleared;
        TerminalWebView.NavigationCompleted += TerminalWebView_NavigationCompleted;
        TerminalWebView.GotKeyboardFocus += (_, _) => isWebFocused = true;
        TerminalWebView.LostKeyboardFocus += (_, _) => isWebFocused = false;
    }

    public AdbTerminalSession Session { get; } = new();

    public bool IsTerminalFocused => isOpen
        && (isWebFocused || TerminalWebView.IsFocused || TerminalWebView.IsKeyboardFocusWithin);

    public async Task OpenAsync(string deviceId)
    {
        await lifecycleMutex.WaitAsync();
        try
        {
            if (isDisposed)
                return;

            isOpen = true;
            requestedDeviceId = deviceId ?? "";

            try
            {
                await EnsureWebViewReadyAsync();
                await Session.OpenAsync(requestedDeviceId, columns, rows);
                FocusTerminal();
            }
            catch (Exception ex)
            {
                Session.SetHostUnavailable($"Terminal renderer unavailable: {ex.Message}");
            }
        }
        finally
        {
            lifecycleMutex.Release();
        }
    }

    public async Task SetDeviceAsync(string deviceId)
    {
        await lifecycleMutex.WaitAsync();
        try
        {
            if (isDisposed || !isOpen)
                return;

            requestedDeviceId = deviceId ?? "";
            Interlocked.Increment(ref outputEpoch);
            outputAcknowledgement?.TrySetCanceled();
            outputAcknowledgement = null;
            await Session.SetDeviceAsync(requestedDeviceId);
        }
        finally
        {
            lifecycleMutex.Release();
        }
    }

    public async Task CloseAsync()
    {
        await lifecycleMutex.WaitAsync();
        try
        {
            if (isDisposed)
                return;

            isOpen = false;
            requestedDeviceId = "";
            Interlocked.Increment(ref outputEpoch);
            outputAcknowledgement?.TrySetCanceled();
            outputAcknowledgement = null;
            await Session.CloseAsync();
            PostMessage(new { type = "reset" });
        }
        finally
        {
            lifecycleMutex.Release();
        }
    }

    public void FocusTerminal()
    {
        if (!pageReady || isDisposed)
            return;

        TerminalWebView.Focus();
        PostMessage(new { type = "focus" });
    }

    public async Task ShutdownAsync()
    {
        if (isDisposed)
            return;

        isDisposed = true;
        isOpen = false;
        Interlocked.Increment(ref outputEpoch);
        outputAcknowledgement?.TrySetCanceled();
        outputAcknowledgement = null;
        Session.OutputReceived -= Session_OutputReceivedAsync;
        Session.Cleared -= Session_Cleared;

        try
        {
            await Session.DisposeAsync();
        }
        catch
        { }

        if (TerminalWebView.CoreWebView2 is not null)
        {
            TerminalWebView.CoreWebView2.WebMessageReceived -= TerminalWebView_WebMessageReceived;
            TerminalWebView.CoreWebView2.ProcessFailed -= TerminalWebView_ProcessFailed;
        }

        TerminalWebView.Dispose();
    }

    private async Task EnsureWebViewReadyAsync()
    {
        if (pageReady)
            return;

        await webViewMutex.WaitAsync();
        Task readyTask;
        try
        {
            if (pageReady)
                return;

            string hostFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
            string hostPath = Path.Combine(hostFolder, "index.html");
            if (!File.Exists(hostPath))
                throw new FileNotFoundException("Terminal frontend was not deployed.", hostPath);

            if (TerminalWebView.CoreWebView2 is null)
                await TerminalWebView.EnsureCoreWebView2Async();

            if (!webViewConfigured)
            {
                CoreWebView2Settings settings = TerminalWebView.CoreWebView2.Settings;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.AreDefaultContextMenusEnabled = true;
                settings.AreDefaultScriptDialogsEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;

                TerminalWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    TERMINAL_HOST_NAME,
                    hostFolder,
                    CoreWebView2HostResourceAccessKind.DenyCors);
                TerminalWebView.CoreWebView2.WebMessageReceived += TerminalWebView_WebMessageReceived;
                TerminalWebView.CoreWebView2.ProcessFailed += TerminalWebView_ProcessFailed;
                webViewConfigured = true;
            }

            pageReadyCompletion = CreateCompletion<bool>();
            pageReady = false;
            TerminalWebView.Source = new($"https://{TERMINAL_HOST_NAME}/index.html");
            readyTask = pageReadyCompletion.Task;
        }
        finally
        {
            webViewMutex.Release();
        }

        await readyTask.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private async Task Session_OutputReceivedAsync(byte[] output)
    {
        if (isDisposed || !pageReady || output.Length == 0)
            return;

        int sequence = Interlocked.Increment(ref outputSequence);
        int epoch = Volatile.Read(ref outputEpoch);
        TaskCompletionSource<int> completion = CreateCompletion<int>();
        bool posted = false;

        if (Application.Current is not App app)
            return;

        await app.EnqueueUiAsync("terminal.output", () =>
        {
            if (isDisposed || !pageReady)
                return;

            outputAcknowledgement = completion;
            PostMessage(new
            {
                type = "output",
                id = sequence,
                data = Convert.ToBase64String(output),
            });
            posted = true;
        });

        if (!posted)
            return;

        try
        {
            int acknowledgement = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (acknowledgement != sequence)
                throw new InvalidDataException("Terminal output acknowledgement was out of order.");
        }
        catch
        {
            if (isOpen && pageReady && epoch == Volatile.Read(ref outputEpoch))
                ScheduleRendererRecovery("Terminal renderer stopped responding.");
        }
        finally
        {
            if (ReferenceEquals(outputAcknowledgement, completion))
                outputAcknowledgement = null;
        }
    }

    private void Session_Cleared()
    {
        if (Dispatcher.CheckAccess())
            PostMessage(new { type = "clear" });
        else if (Application.Current is App app)
            app.EnqueueUiLatest("terminal.clear", "terminal.clear", () => PostMessage(new { type = "clear" }));
    }

    private async void TerminalWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (JToken.Parse(e.WebMessageAsJson) is not JObject message)
                return;

            switch ((string)message["type"])
            {
                case "ready":
                    columns = NormalizeDimension(message["cols"]?.Value<int>(), DEFAULT_COLUMNS);
                    rows = NormalizeDimension(message["rows"]?.Value<int>(), DEFAULT_ROWS);
                    pageReady = true;
                    await Session.ResizeAsync(columns, rows);
                    pageReadyCompletion.TrySetResult(true);
                    break;

                case "input":
                    int textInputId = message["id"]?.Value<int>() ?? -1;
                    await Session.SendTextAsync((string)message["data"] ?? "");
                    PostMessage(new { type = "inputAck", id = textInputId });
                    break;

                case "inputBinary":
                    int binaryInputId = message["id"]?.Value<int>() ?? -1;
                    string encodedInput = (string)message["data"] ?? "";
                    if (!string.IsNullOrEmpty(encodedInput))
                        await Session.SendBinaryAsync(Convert.FromBase64String(encodedInput));
                    PostMessage(new { type = "inputAck", id = binaryInputId });
                    break;

                case "interrupt":
                    int interruptId = message["id"]?.Value<int>() ?? -1;
                    await Session.InterruptAsync();
                    PostMessage(new { type = "inputAck", id = interruptId });
                    break;

                case "resize":
                    columns = NormalizeDimension(message["cols"]?.Value<int>(), columns);
                    rows = NormalizeDimension(message["rows"]?.Value<int>(), rows);
                    await Session.ResizeAsync(columns, rows);
                    break;

                case "outputAck":
                    int outputId = message["id"]?.Value<int>() ?? -1;
                    outputAcknowledgement?.TrySetResult(outputId);
                    break;

                case "copy":
                    if (Application.Current is App copyApp)
                        await copyApp.SetClipboardTextAsync((string)message["data"] ?? "");
                    break;

                case "requestPaste":
                    string clipboardText = Application.Current is App pasteApp
                        ? await pasteApp.ReadClipboardTextAsync()
                        : "";
                    if (!string.IsNullOrEmpty(clipboardText))
                        PostMessage(new { type = "paste", data = clipboardText });
                    break;

                case "openExternal":
                    OpenExternalLink((string)message["data"] ?? "");
                    break;

                case "focus":
                    isWebFocused = message["focused"]?.Value<bool>() == true;
                    break;
            }
        }
        catch (Exception ex)
        {
            Session.SetHostUnavailable($"Terminal bridge error: {ex.Message}");
        }
    }

    private void TerminalWebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
            return;

        pageReady = false;
        Interlocked.Increment(ref outputEpoch);
        pageReadyCompletion.TrySetException(
            new InvalidOperationException($"Terminal navigation failed: {e.WebErrorStatus}"));
    }

    private void TerminalWebView_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
    {
        pageReady = false;
        Interlocked.Increment(ref outputEpoch);
        outputAcknowledgement?.TrySetCanceled();
        pageReadyCompletion.TrySetException(
            new InvalidOperationException($"WebView2 process failed: {e.ProcessFailedKind}"));
        ScheduleRendererRecovery("Terminal renderer restarted after a WebView2 failure.");
    }

    private void ScheduleRendererRecovery(string status)
    {
        if (isDisposed || Interlocked.Exchange(ref recoveryScheduled, 1) != 0)
            return;

        Session.SetHostUnavailable(status);
        if (Application.Current is App app)
            app.EnqueueUiLatest("terminal.recover", "terminal.recover", () => _ = RecoverRendererAsync());
    }

    private async Task RecoverRendererAsync()
    {
        await lifecycleMutex.WaitAsync();
        try
        {
            if (isDisposed || !isOpen)
                return;

            await Session.CloseAsync();
            pageReady = false;
            pageReadyCompletion = CreateCompletion<bool>();
            TerminalWebView.CoreWebView2?.Reload();
            await pageReadyCompletion.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Session.OpenAsync(requestedDeviceId, columns, rows);
        }
        catch (Exception ex)
        {
            Session.SetHostUnavailable($"Terminal renderer recovery failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref recoveryScheduled, 0);
            lifecycleMutex.Release();
        }
    }

    private async void Interrupt_Click(object sender, RoutedEventArgs e)
    {
        await Session.InterruptAsync();
        FocusTerminal();
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        PostMessage(new { type = "find" });
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Session.Clear();
        FocusTerminal();
    }

    private void PostMessage(object message)
    {
        if (!pageReady || TerminalWebView.CoreWebView2 is null)
            return;

        TerminalWebView.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(message));
    }

    private static void OpenExternalLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
            || uri.Scheme is not ("http" or "https" or "ftp"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        { }
    }

    private static int NormalizeDimension(int? value, int fallback) => value is > 0
        ? Math.Clamp(value.Value, 1, short.MaxValue)
        : fallback;

    private static TaskCompletionSource<T> CreateCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
