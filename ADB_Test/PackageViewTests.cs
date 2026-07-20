using ADB_Explorer;
using ADB_Explorer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace ADB_Test;

[TestClass]
public class PackageViewTests
{
    [TestMethod]
    public void TenThousandPackagesAreFilteredAndSortedOffTheWpfCollectionView()
    {
        var packages = Enumerable.Range(0, 10_000)
            .Select(index => new Package(
                $"package-{10_000 - index:D5}{(index % 5 == 0 ? "-match" : "")}",
                index % 2 == 0 ? Package.PackageType.System : Package.PackageType.User,
                index.ToString(),
                (index * 2).ToString(),
                $"/package-{index}.apk"))
            .ToArray();

        var result = MainWindow.BuildPackageView(
            packages,
            "match",
            showSystemPackages: false,
            nameof(Package.Name),
            ListSortDirection.Ascending,
            CancellationToken.None);

        Assert.HasCount(1_000, result);
        Assert.IsTrue(result.All(package => package.Type is Package.PackageType.User));
        Assert.IsTrue(result.All(package => package.Name.Contains("match")));
        CollectionAssert.AreEqual(
            result.Select(package => package.Name).OrderBy(name => name).ToArray(),
            result.Select(package => package.Name).ToArray());
    }

    [TestMethod]
    public void CanceledPackageViewDoesNotPublishAStaleSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => MainWindow.BuildPackageView(
            [],
            "",
            showSystemPackages: true,
            nameof(Package.Type),
            ListSortDirection.Descending,
            cancellation.Token));
    }
}
