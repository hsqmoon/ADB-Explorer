using ADB_Explorer.Models;
using ADB_Explorer.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ADB_Test;

[TestClass]
public class ShellStaServiceTests
{
    [TestMethod]
    public async Task ServiceUsesOneLongLivedStaThread()
    {
        using var service = new ShellStaService();

        var first = await service.InvokeAsync(
            () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()),
            CancellationToken.None);
        var second = await service.InvokeAsync(
            () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()),
            CancellationToken.None);

        Assert.AreEqual(first.Item1, second.Item1);
        Assert.AreEqual(ApartmentState.STA, first.Item2);
    }

    [TestMethod]
    public async Task IconRequestsAreCachedAndDeduplicated()
    {
        using var sta = new ShellStaService();
        using var icons = new ShellIconService(sta);

        var firstTask = icons.GetIconsAsync("first.txt", AbstractFile.SpecialFileType.Regular);
        var secondTask = icons.GetIconsAsync("second.txt", AbstractFile.SpecialFileType.Regular);
        await Task.WhenAll(firstTask, secondTask);

        Assert.AreSame(firstTask.Result, secondTask.Result);
        Assert.IsNotEmpty(firstTask.Result);
        Assert.IsTrue(firstTask.Result[0].IsFrozen);
    }

    [TestMethod]
    public async Task ServiceRecreatesStaWorkerAfterUnexpectedDispatcherExit()
    {
        using var service = new ShellStaService();
        int firstThread = await service.InvokeAsync(
            () => Environment.CurrentManagedThreadId,
            CancellationToken.None);

        await service.InvokeAsync(
            () =>
            {
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                return true;
            },
            CancellationToken.None);

        await Task.Delay(100);
        int secondThread = await service.InvokeAsync(
            () => Environment.CurrentManagedThreadId,
            CancellationToken.None);

        Assert.AreNotEqual(firstThread, secondThread);
    }

    [TestMethod]
    public async Task DropSnapshotCopiesManagedDataWithoutReturningComObjects()
    {
        using var service = new ShellStaService();
        var snapshots = new DropSnapshotService(service);
        string[] paths = Enumerable.Range(0, 1000)
            .Select(index => $@"C:\drop\file-{index}.bin")
            .ToArray();
        DataObject dataObject = new();
        dataObject.SetData(DataFormats.FileDrop, paths);

        var snapshot = await snapshots.ReadAsync(dataObject, null, CancellationToken.None);

        Assert.HasCount(paths.Length, snapshot.Files);
        CollectionAssert.AreEqual(paths, snapshot.Files);
        Assert.HasCount(0, snapshot.Descriptors);
    }

    [TestMethod]
    public async Task RepeatedDropSnapshotsRemainStableOnTheSameStaWorker()
    {
        using var service = new ShellStaService();
        var snapshots = new DropSnapshotService(service);
        string[] paths = Enumerable.Range(0, 25)
            .Select(index => $@"C:\drop\file-{index}.bin")
            .ToArray();
        DataObject dataObject = new();
        dataObject.SetData(DataFormats.FileDrop, paths);
        int workerThread = 0;

        for (int iteration = 0; iteration < 100; iteration++)
        {
            var snapshot = await snapshots.ReadAsync(dataObject, null, CancellationToken.None);
            Assert.HasCount(paths.Length, snapshot.Files);

            int currentThread = await service.InvokeAsync(
                () => Environment.CurrentManagedThreadId,
                CancellationToken.None);
            if (iteration == 0)
                workerThread = currentThread;
            else
                Assert.AreEqual(workerThread, currentThread);
        }
    }
}
