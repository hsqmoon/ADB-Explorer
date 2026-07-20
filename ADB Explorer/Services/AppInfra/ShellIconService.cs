using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using System.Threading.Channels;

namespace ADB_Explorer.Services;

internal sealed class ShellIconService : IDisposable
{
    private const int CACHE_CAPACITY = 256;
    private const int PENDING_CAPACITY = 128;

    private readonly ShellStaService shellSta;
    private readonly Func<string, AbstractFile.SpecialFileType, int, CancellationToken, Task<IReadOnlyList<BitmapSource>>> iconLoader;
    private readonly Channel<IconRequest> requests = Channel.CreateBounded<IconRequest>(new BoundedChannelOptions(PENDING_CAPACITY)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false,
    });
    private readonly ConcurrentDictionary<IconKey, Lazy<Task<IReadOnlyList<BitmapSource>>>> inFlight = new();
    private readonly SemaphoreSlim pendingGate = new(PENDING_CAPACITY, PENDING_CAPACITY);
    private readonly Dictionary<IconKey, (IReadOnlyList<BitmapSource> Icons, LinkedListNode<IconKey> Node)> cache = [];
    private readonly LinkedList<IconKey> cacheOrder = [];
    private readonly object cacheLock = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private int disposed;

    public ShellIconService(ShellStaService shellSta)
    {
        this.shellSta = shellSta;
        iconLoader = LoadFromShellAsync;
        worker = RunWorkerAsync(lifetime.Token);
    }

    internal ShellIconService(
        Func<string, AbstractFile.SpecialFileType, int, CancellationToken, Task<IReadOnlyList<BitmapSource>>> iconLoader)
    {
        this.iconLoader = iconLoader ?? throw new ArgumentNullException(nameof(iconLoader));
        worker = RunWorkerAsync(lifetime.Token);
    }

    public async Task<IReadOnlyList<BitmapSource>> GetIconsAsync(
        string fileName,
        AbstractFile.SpecialFileType specialType,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var key = new IconKey(
            specialType.HasFlag(AbstractFile.SpecialFileType.Regular)
                ? Path.GetExtension(fileName).ToLowerInvariant()
                : "",
            specialType,
            16);
        return await GetIconsCoreAsync(
            key,
            fileName,
            specialType,
            16,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BitmapSource> GetPreviewIconAsync(
        string fileName,
        AbstractFile.SpecialFileType specialType,
        int iconSize,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        int normalizedSize = Math.Clamp(iconSize, 16, 256);
        var key = new IconKey(
            specialType.HasFlag(AbstractFile.SpecialFileType.Regular)
                ? Path.GetExtension(fileName).ToLowerInvariant()
                : "",
            specialType,
            normalizedSize);
        return (await GetIconsCoreAsync(
            key,
            fileName,
            specialType,
            normalizedSize,
            cancellationToken).ConfigureAwait(false)).FirstOrDefault();
    }

    private async Task<IReadOnlyList<BitmapSource>> GetIconsCoreAsync(
        IconKey key,
        string fileName,
        AbstractFile.SpecialFileType specialType,
        int iconSize,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryGetCached(key, out var cached))
                return cached;
            if (inFlight.TryGetValue(key, out var existing))
            {
                var sharedResult = await AwaitInFlightAsync(key, existing, cancellationToken).ConfigureAwait(false);
                if (sharedResult is not null)
                    return sharedResult;
                continue;
            }

            using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime.Token);
            await pendingGate.WaitAsync(pendingCancellation.Token).ConfigureAwait(false);
            bool releaseGate = true;
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                if (TryGetCached(key, out cached))
                    return cached;
                if (inFlight.TryGetValue(key, out existing))
                {
                    pendingGate.Release();
                    releaseGate = false;
                    var sharedResult = await AwaitInFlightAsync(key, existing, cancellationToken).ConfigureAwait(false);
                    if (sharedResult is not null)
                        return sharedResult;
                    continue;
                }

                var candidate = new Lazy<Task<IReadOnlyList<BitmapSource>>>(
                    () => LoadAsync(key, fileName, specialType, iconSize, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                var request = inFlight.GetOrAdd(key, candidate);
                var task = request.Value;
                if (ReferenceEquals(candidate, request))
                {
                    releaseGate = false;
                    _ = RemoveInFlightAsync(key, request, task);
                }
                else
                {
                    pendingGate.Release();
                    releaseGate = false;
                }

                var result = await AwaitInFlightAsync(key, request, cancellationToken).ConfigureAwait(false);
                if (result is not null)
                    return result;
            }
            finally
            {
                if (releaseGate)
                    pendingGate.Release();
            }
        }
    }

    private async Task<IReadOnlyList<BitmapSource>> AwaitInFlightAsync(
        IconKey key,
        Lazy<Task<IReadOnlyList<BitmapSource>>> request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await request.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            inFlight.TryRemove(new KeyValuePair<IconKey, Lazy<Task<IReadOnlyList<BitmapSource>>>>(key, request));
            return null;
        }
    }

    private async Task RemoveInFlightAsync(
        IconKey key,
        Lazy<Task<IReadOnlyList<BitmapSource>>> request,
        Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        { }
        finally
        {
            inFlight.TryRemove(new KeyValuePair<IconKey, Lazy<Task<IReadOnlyList<BitmapSource>>>>(key, request));
            pendingGate.Release();
        }
    }

    private async Task<IReadOnlyList<BitmapSource>> LoadAsync(
        IconKey key,
        string fileName,
        AbstractFile.SpecialFileType specialType,
        int iconSize,
        CancellationToken cancellationToken)
    {
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        var completion = new TaskCompletionSource<IReadOnlyList<BitmapSource>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await requests.Writer.WriteAsync(
            new(key, fileName, specialType, iconSize, completion, requestLifetime.Token),
            requestLifetime.Token).ConfigureAwait(false);
        return await completion.Task.WaitAsync(requestLifetime.Token).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    if (TryGetCached(request.Key, out var cached))
                    {
                        request.Completion.TrySetResult(cached);
                        continue;
                    }

                    using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                        request.CancellationToken,
                        cancellationToken);
                    var icons = await iconLoader(
                        request.FileName,
                        request.SpecialType,
                        request.IconSize,
                        requestLifetime.Token).ConfigureAwait(false);
                    requestLifetime.Token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref disposed) != 0)
                        throw new OperationCanceledException(requestLifetime.Token);
                    foreach (var icon in icons.Where(icon => icon is not null && !icon.IsFrozen && icon.CanFreeze))
                        icon.Freeze();
                    StoreCached(request.Key, icons);
                    request.Completion.TrySetResult(icons);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested || request.CancellationToken.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    request.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { }
        finally
        {
            while (requests.Reader.TryRead(out var request))
                request.Completion.TrySetCanceled(cancellationToken);
        }
    }

    private Task<IReadOnlyList<BitmapSource>> LoadFromShellAsync(
        string fileName,
        AbstractFile.SpecialFileType specialType,
        int iconSize,
        CancellationToken cancellationToken) =>
        shellSta.InvokeAsync(
            () => iconSize <= 16
                ? (IReadOnlyList<BitmapSource>)FileToIconConverter.GetImage(
                    fileName,
                    specialType,
                    true).ToArray()
                : (IReadOnlyList<BitmapSource>)[FileToIconConverter.GetImage(
                    fileName,
                    iconSize,
                    specialType)],
            cancellationToken);

    private void StoreCached(IconKey key, IReadOnlyList<BitmapSource> icons)
    {
        lock (cacheLock)
        {
            if (cache.TryGetValue(key, out var existing))
                cacheOrder.Remove(existing.Node);

            var node = cacheOrder.AddFirst(key);
            cache[key] = (icons, node);
            while (cache.Count > CACHE_CAPACITY)
            {
                var oldest = cacheOrder.Last;
                cacheOrder.RemoveLast();
                cache.Remove(oldest.Value);
            }
        }
    }

    private bool TryGetCached(IconKey key, out IReadOnlyList<BitmapSource> icons)
    {
        lock (cacheLock)
        {
            if (!cache.TryGetValue(key, out var entry))
            {
                icons = null;
                return false;
            }

            cacheOrder.Remove(entry.Node);
            cacheOrder.AddFirst(entry.Node);
            icons = entry.Icons;
            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        requests.Writer.TryComplete();
        lifetime.Cancel();
        _ = FinishDisposeAsync();
        lock (cacheLock)
        {
            cache.Clear();
            cacheOrder.Clear();
        }
    }

    private async Task FinishDisposeAsync()
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { }
        catch (Exception ex)
        {
            App.ReportBackgroundFailure(ex, "shell-icon.worker");
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private readonly record struct IconKey(
        string Extension,
        AbstractFile.SpecialFileType SpecialType,
        int Size);

    private sealed record IconRequest(
        IconKey Key,
        string FileName,
        AbstractFile.SpecialFileType SpecialType,
        int IconSize,
        TaskCompletionSource<IReadOnlyList<BitmapSource>> Completion,
        CancellationToken CancellationToken);
}
