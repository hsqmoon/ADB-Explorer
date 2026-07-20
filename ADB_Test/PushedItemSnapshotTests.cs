using ADB_Explorer.Models;
using ADB_Explorer.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Test;

[TestClass]
public class PushedItemSnapshotTests
{
    [TestMethod]
    public void BatchPushSnapshotSurvivesSourceTreeRelease()
    {
        var source = new SyncFile(@"C:\drop", FileType.Folder)
        {
            PathType = FilePathType.Windows,
        };
        source.Children.Add(new SyncFile(@"C:\drop\alpha.txt")
        {
            PathType = FilePathType.Windows,
            Size = 12,
        });
        source.Children.Add(new SyncFile(@"C:\drop\folder", FileType.Folder)
        {
            PathType = FilePathType.Windows,
        });
        var target = new SyncFile("/mnt/UDISK", FileType.Folder);

        var snapshot = FileSyncOperation.CreatePushedItemSnapshot(source, target, isBatch: true);
        source.ClearAll();

        Assert.HasCount(2, snapshot);
        Assert.IsEmpty(source.Children);
        CollectionAssert.AreEqual(
            new[] { "alpha.txt", "folder" },
            snapshot.Select(item => item.FullName).ToArray());
        CollectionAssert.AreEqual(
            new[] { "/mnt/UDISK/alpha.txt", "/mnt/UDISK/folder" },
            snapshot.Select(item => item.FullPath).ToArray());
        Assert.IsTrue(snapshot.All(item => item.ParentPath == "/mnt/UDISK"));
        Assert.AreEqual(FileType.File, snapshot[0].Type);
        Assert.AreEqual(FileType.Folder, snapshot[1].Type);
        CollectionAssert.AreEqual(
            new[] { @"C:\drop\alpha.txt", @"C:\drop\folder" },
            snapshot.Select(item => item.SourcePath).ToArray());
        CollectionAssert.AreEqual(
            new[] { false, true },
            snapshot.Select(item => item.SourceIsDirectory).ToArray());
    }

    [TestMethod]
    public void SinglePushSnapshotUsesFinalTargetName()
    {
        var source = new SyncFile(@"C:\drop\source.txt")
        {
            PathType = FilePathType.Windows,
            Size = 21,
        };
        var target = new SyncFile("/mnt/UDISK/renamed.txt")
        {
            Size = source.Size,
        };

        var snapshot = FileSyncOperation.CreatePushedItemSnapshot(source, target, isBatch: false);

        Assert.HasCount(1, snapshot);
        Assert.AreEqual("/mnt/UDISK", snapshot[0].ParentPath);
        Assert.AreEqual("renamed.txt", snapshot[0].FullName);
        Assert.AreEqual("/mnt/UDISK/renamed.txt", snapshot[0].FullPath);
        Assert.AreEqual(21L, snapshot[0].Size);
        Assert.AreEqual(@"C:\drop\source.txt", snapshot[0].SourcePath);
        Assert.IsFalse(snapshot[0].SourceIsDirectory);
    }
}
