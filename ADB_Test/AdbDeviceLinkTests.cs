using ADB_Explorer.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Test;

[TestClass]
public class AdbDeviceLinkTests
{
    [TestMethod]
    public void LinkDetailsAreParsedWithoutStatOutput()
    {
        string[] paths = ["/file-link", "/folder-link", "/broken-link", "/unknown-link", "/missing-result"];
        const string output = """
            /// /file-link /// /init /// f ///
            /// /folder-link /// /tmp /// d ///
            /// /broken-link ///  /// x ///
            /// /unknown-link /// /special /// u ///
            """;

        var result = ADBService.AdbDevice.ParseLinkDetails(paths, output);

        CollectionAssert.AreEqual(
            new[]
            {
                ("/init", FileType.File),
                ("/tmp", FileType.Folder),
                ("", FileType.BrokenLink),
                ("/special", FileType.Unknown),
                ("", FileType.Unknown),
            },
            result.ToArray());
    }

    [TestMethod]
    public void LinkDetailsPreserveSpecialFileTypes()
    {
        const string output = """
            /// /block /// /dev/block0 /// b ///
            /// /char /// /dev/null /// c ///
            /// /fifo /// /run/pipe /// p ///
            /// /socket /// /run/socket /// s ///
            """;

        var result = ADBService.AdbDevice.ParseLinkDetails(
            ["/block", "/char", "/fifo", "/socket"],
            output);

        CollectionAssert.AreEqual(
            new[]
            {
                ("/dev/block0", FileType.BlockDevice),
                ("/dev/null", FileType.CharDevice),
                ("/run/pipe", FileType.FIFO),
                ("/run/socket", FileType.Socket),
            },
            result.ToArray());
    }
}
