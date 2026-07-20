using ADB_Explorer.Models;
using ADB_Explorer.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ADB_Test;

[TestClass]
public class ShellIconServiceTests
{
    [TestMethod]
    public async Task ConcurrentDuplicateRequestsShareOneLoadAndReturnFrozenImages()
    {
        int loadCount = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ShellIconService(async (_, _, _, cancellationToken) =>
        {
            Interlocked.Increment(ref loadCount);
            await release.Task.WaitAsync(cancellationToken);
            return CreateIcons();
        });

        Task<IReadOnlyList<BitmapSource>> first = service.GetIconsAsync(
            "first.txt",
            AbstractFile.SpecialFileType.Regular);
        Task<IReadOnlyList<BitmapSource>> duplicate = service.GetIconsAsync(
            "second.txt",
            AbstractFile.SpecialFileType.Regular);
        release.SetResult();

        var results = await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, loadCount);
        Assert.AreSame(results[0], results[1]);
        Assert.IsTrue(results[0][0].IsFrozen);
    }

    [TestMethod]
    public async Task LeastRecentlyUsedEntryIsReloadedAfterCapacityIsExceeded()
    {
        int loadCount = 0;
        using var service = new ShellIconService((_, _, _, _) =>
        {
            Interlocked.Increment(ref loadCount);
            return Task.FromResult(CreateIcons());
        });

        for (int index = 0; index <= 256; index++)
        {
            await service.GetIconsAsync(
                $"file.extension-{index}",
                AbstractFile.SpecialFileType.Regular);
        }

        await service.GetIconsAsync("file.extension-0", AbstractFile.SpecialFileType.Regular);

        Assert.AreEqual(258, loadCount);
    }

    [TestMethod]
    public async Task PendingCapacityBackpressuresNewUniqueRequestsAndHonorsCancellation()
    {
        int loadCount = 0;
        var firstLoadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ShellIconService(async (_, _, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref loadCount) == 1)
                firstLoadEntered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return CreateIcons();
        });
        var admitted = Enumerable.Range(0, 128)
            .Select(index => service.GetIconsAsync(
                $"file.extension-{index}",
                AbstractFile.SpecialFileType.Regular))
            .ToArray();
        await firstLoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var backpressured = service.GetIconsAsync(
            "file.extension-overflow",
            AbstractFile.SpecialFileType.Regular,
            cancellation.Token);

        Assert.IsFalse(backpressured.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await backpressured;
        });

        release.TrySetResult();
        await Task.WhenAll(admitted).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(128, loadCount);
    }

    [TestMethod]
    public async Task CancelingAnOldSessionLetsANewWaiterRetryTheSharedIcon()
    {
        int loadCount = 0;
        var firstLoadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ShellIconService(async (_, _, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref loadCount) == 1)
            {
                firstLoadEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return CreateIcons();
        });
        using var oldSession = new CancellationTokenSource();

        var oldRequest = service.GetIconsAsync(
            "old.txt",
            AbstractFile.SpecialFileType.Regular,
            oldSession.Token);
        var newRequest = service.GetIconsAsync(
            "new.txt",
            AbstractFile.SpecialFileType.Regular);
        await firstLoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        oldSession.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await oldRequest);
        var icons = await newRequest.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, loadCount);
        Assert.IsTrue(icons[0].IsFrozen);
    }

    [TestMethod]
    public async Task DisposingTheServiceCancelsOutstandingWaiters()
    {
        var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ShellIconService(async (_, _, _, cancellationToken) =>
        {
            loadEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateIcons();
        });
        var request = service.GetIconsAsync("pending.txt", AbstractFile.SpecialFileType.Regular);
        await loadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        service.Dispose();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await request);
    }

    private static IReadOnlyList<BitmapSource> CreateIcons()
    {
        var icon = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[4],
            4);
        icon.Freeze();
        return new[] { icon };
    }
}
