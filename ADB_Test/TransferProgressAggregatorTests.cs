using ADB_Explorer.Models;
using ADB_Explorer.Services;
using AdvancedSharpAdbClient.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ADB_Test;

[TestClass]
public class TransferProgressAggregatorTests
{
    [TestMethod]
    public async Task FlushKeepsOnlyLatestUpdateForEachFile()
    {
        List<TransferProgressSnapshot> applied = [];
        using var aggregator = new TransferProgressAggregator(snapshot =>
        {
            applied.Add(snapshot);
            return Task.CompletedTask;
        });
        var file = new SyncFile("/file.bin") { Size = 100 };

        aggregator.Reset(100);
        aggregator.Report(
            file,
            new AdbSyncProgressInfo(file.FullPath, null, 25, 25),
            25,
            0,
            25,
            0);
        aggregator.Report(
            file,
            new AdbSyncProgressInfo(file.FullPath, null, 75, 75),
            50,
            25,
            75,
            0);

        await aggregator.FlushAsync();

        Assert.HasCount(1, applied);
        Assert.HasCount(1, applied[0].Updates);
        Assert.AreEqual(75L, applied[0].TransferredBytes);
        Assert.AreEqual(75d, ((AdbSyncProgressInfo)applied[0].Updates[0].Update).CurrentFilePercentage);
    }

    [TestMethod]
    public async Task PeriodicFlushPublishesWithoutExplicitFlush()
    {
        var applied = new TaskCompletionSource<TransferProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var aggregator = new TransferProgressAggregator(snapshot =>
        {
            applied.TrySetResult(snapshot);
            return Task.CompletedTask;
        });
        var file = new SyncFile("/periodic.bin") { Size = 100 };

        aggregator.Reset(100);
        aggregator.Report(
            file,
            new AdbSyncProgressInfo(file.FullPath, null, 50, 50),
            50,
            0,
            50,
            0);

        var snapshot = await applied.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.HasCount(1, snapshot.Updates);
        Assert.AreEqual(50L, snapshot.TransferredBytes);
    }

    [TestMethod]
    public async Task ConcurrentFinalFlushWaitsForInFlightFlushAndKeepsNewerUpdate()
    {
        List<TransferProgressSnapshot> applied = [];
        var firstApplyEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int applyCount = 0;
        using var aggregator = new TransferProgressAggregator(async snapshot =>
        {
            applied.Add(snapshot);
            if (Interlocked.Increment(ref applyCount) == 1)
            {
                firstApplyEntered.TrySetResult(true);
                await releaseFirstApply.Task;
            }
        });
        var file = new SyncFile("/concurrent.bin") { Size = 100 };

        aggregator.Reset(100);
        aggregator.Report(
            file,
            new AdbSyncProgressInfo(file.FullPath, null, 25, 25),
            25,
            0,
            25,
            0);
        Task firstFlush = aggregator.FlushAsync();
        await firstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        aggregator.Report(
            file,
            new AdbSyncProgressInfo(file.FullPath, null, 100, 100),
            75,
            25,
            100,
            0);
        Task finalFlush = aggregator.FlushAsync();
        releaseFirstApply.TrySetResult(true);

        await Task.WhenAll(firstFlush, finalFlush).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.HasCount(2, applied);
        Assert.AreEqual(25d, ((AdbSyncProgressInfo)applied[0].Updates.Single().Update).CurrentFilePercentage);
        Assert.AreEqual(100d, ((AdbSyncProgressInfo)applied[1].Updates.Single().Update).CurrentFilePercentage);
        Assert.AreEqual(100L, applied[1].TransferredBytes);
    }

    [TestMethod]
    public async Task DisposeDuringFinalFlushDoesNotDiscardTheCapturedSnapshot()
    {
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TransferProgressSnapshot applied = null;
        var aggregator = new TransferProgressAggregator(async snapshot =>
        {
            applyEntered.TrySetResult();
            await releaseApply.Task;
            applied = snapshot;
        });
        var file = new SyncFile("/final.bin") { Size = 10 };
        aggregator.Reset(10);
        aggregator.Report(
            file,
            new AdbSyncProgressInfo(file.FullPath, null, 100, 10),
            10,
            0,
            100,
            0);

        Task flush = aggregator.FlushAsync();
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        aggregator.Dispose();
        releaseApply.TrySetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsNotNull(applied);
        Assert.HasCount(1, applied.Updates);
        Assert.AreEqual(10L, applied.TransferredBytes);
    }
}
