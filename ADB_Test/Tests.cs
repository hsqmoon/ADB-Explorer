using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;
using AdvancedSharpAdbClient.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

[assembly: DoNotParallelize]

namespace ADB_Test
{
    [TestClass]
    public class Tests
    {
        [ClassInitialize]
        public static void Initialize(TestContext _)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
            {
                Environment.SetEnvironmentVariable(
                    "windir",
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            }
        }

        [TestMethod]
        public void ToSizeTest()
        {
            var testVals = new Dictionary<long, string>()
            {
                { 0, "0B" },
                { 300, "300B" },
                { 33000, "32.2KB" }, // 32.226
                { 500690, "489KB" }, // 488.955
                { 1024204, "1MB" }, // 1.0002
                { 1200100, "1.1MB" }, // 1.145
                { 3400200100, "3.2GB" }, // 1.667
                { 1200300400500, "1.1TB" } // 1.092
            };

            foreach (var item in testVals)
            {
                Assert.AreEqual(item.Value, item.Key.BytesToSize());
            }

        }

        [TestMethod]
        public void ToTimeTest()
        {
            Assert.AreEqual(string.Format(ADB_Explorer.Strings.Resources.S_MILLISECONDS_SHORT, "50"), UnitConverter.ToTime(.05));
            Assert.AreEqual(string.Format(ADB_Explorer.Strings.Resources.S_HOURS_SHORT, "1:00:00"), UnitConverter.ToTime(3600));
        }

        [TestMethod]
        public void TextBoxValidationTest1()
        {
            var caretIndex = 1;
            var text = "0";
            var altText = "0";
            var maxLength = -1;

            for (int i = 1; i < 6; i++)
            {
                text += i;
                caretIndex++;

                TextHelper.TextBoxValidation(ref caretIndex, ref text, ref altText, ref maxLength, separator: '-', maxChars: 6);

                var index = i - 1 + (i - 1);
                Assert.AreEqual($"{i - 1}-{i}", text[index..(index + 3)]);
                Assert.AreEqual(text.Length, caretIndex);
                Assert.AreEqual(text, altText);
            }
            Assert.AreEqual(11, maxLength);
        }

        [TestMethod]
        public void TextBoxValidationTest2()
        {
            var caretIndex = 1;
            var text = "0";
            var altText = "0";
            var maxLength = -1;

            for (int i = 1; i < 6; i++)
            {
                text += i;
                caretIndex++;

                TextHelper.TextBoxValidation(ref caretIndex, ref text, ref altText, ref maxLength, maxChars: 5);

                Assert.AreEqual($"{i}"[0], text[i]);
                Assert.AreEqual(i + 1, caretIndex);
                Assert.AreEqual(text, altText);
            }
            Assert.AreEqual(5, maxLength);
        }

        [TestMethod]
        public void TextBoxValidationTest3()
        {
            var caretIndex = 1;
            var text = "190";
            var altText = "19";
            var maxLength = -1;

            TextHelper.TextBoxValidation(ref caretIndex, ref text, ref altText, ref maxLength, maxNumber: 255);
            Assert.AreEqual("190", text);

            caretIndex = 3;
            TextHelper.TextBoxValidation(ref caretIndex, ref text, ref altText, ref maxLength, specialChars: '.', maxNumber: 255);
            Assert.AreEqual("190.", text);
            Assert.AreEqual(4, caretIndex);

            text = "190.300";
            TextHelper.TextBoxValidation(ref caretIndex, ref text, ref altText, ref maxLength, specialChars: '.', maxNumber: 255);
            Assert.AreEqual("190.30", text);

            text = "005";
            altText = "00";
            TextHelper.TextBoxValidation(ref caretIndex, ref text, ref altText, ref maxLength, specialChars: '.', maxNumber: 255);
            Assert.AreEqual("005.", text);

            text = "0" + text;
            TextHelper.TextBoxValidation(ref caretIndex, ref text, ref altText, ref maxLength, specialChars: '.', maxNumber: 255);
            Assert.AreEqual("005.", text);
        }

        [TestMethod]
        public void DuplicateFileTest()
        {
            string[] names = ["New File", "New File 1", "New File.txt"];
            string[] names1 = ["New File", "New File 2"];

            // New file
            Assert.AreEqual("New File", FileHelper.DuplicateFile(Array.Empty<string>(), "New File"));
            Assert.AreEqual("New File 1", FileHelper.DuplicateFile(["New File"], "New File"));
            Assert.AreEqual("New File 2", FileHelper.DuplicateFile(names, "New File"));
            Assert.AreEqual("New File 1", FileHelper.DuplicateFile(names1, "New File"));
            Assert.AreEqual("New File.pdf", FileHelper.DuplicateFile(names, "New File.pdf"));
            Assert.AreEqual("New File 1.txt", FileHelper.DuplicateFile(names, "New File.txt"));

            string copyName = string.Format(ADB_Explorer.Strings.Resources.S_ITEM_COPY, "New File");
            string[] names2 = ["New File", $"{copyName} 1", "New File.txt"];
            string[] names3 = ["New File", $"{copyName} 2"];

            // Copy
            Assert.AreEqual("New File", FileHelper.DuplicateFile(Array.Empty<string>(), "New File", System.Windows.DragDropEffects.Copy));
            Assert.AreEqual($"{copyName} 1", FileHelper.DuplicateFile(["New File"], "New File", System.Windows.DragDropEffects.Copy));
            Assert.AreEqual($"{copyName} 2", FileHelper.DuplicateFile(names2, "New File", System.Windows.DragDropEffects.Copy));
            Assert.AreEqual($"{copyName} 1", FileHelper.DuplicateFile(names3, "New File", System.Windows.DragDropEffects.Copy));
            Assert.AreEqual("New File.pdf", FileHelper.DuplicateFile(names2, "New File.pdf", System.Windows.DragDropEffects.Copy));
            Assert.AreEqual($"{copyName} 1.txt", FileHelper.DuplicateFile(names2, "New File.txt", System.Windows.DragDropEffects.Copy));
        }

        [TestMethod]
        public void VerifyIconTest()
        {
            var goodIcon = "\uE8EA";
            var badIcon1 = "\u0050";
            var badIcon2 = "FOO";

            // Verify this does not throw an exception by simply executing it
            StyleHelper.VerifyIcon(goodIcon);

            try
            {
                StyleHelper.VerifyIcon(badIcon1);
                Assert.Fail();
            }
            catch (Exception)
            {

            }

            try
            {
                StyleHelper.VerifyIcon(badIcon2);
                Assert.Fail();
            }
            catch (Exception)
            {

            }

        }

        [TestMethod]
        public void HashTest()
        {
            var bytes = Encoding.UTF8.GetBytes("ADB Explorer");
            using var md5Stream = new System.IO.MemoryStream(bytes);
            using var shaStream = new System.IO.MemoryStream(bytes);

            Assert.AreEqual("D0249C232FBF4184D1AB9E3341618F1B", Security.CalculateWindowsFileHash(md5Stream));
            Assert.AreEqual("8615421DF7FB952CC657B987DA75EE3092DFF415656BB6D9BB33852DB1435DF2",
                Security.CalculateWindowsFileHash(shaStream, useSHA: true));
        }

        [TestMethod]
        public void ExtractRelativePathTest()
        {
            var fullPath = @"/sdcard/DCIM/New Folder 1/New File.txt";
            var parent = @"/sdcard/DCIM/";

            var result = FileHelper.ExtractRelativePath(fullPath, parent);
            Assert.AreEqual("New Folder 1/New File.txt", result);

            result = FileHelper.ExtractRelativePath(fullPath, parent[..^1]);
            Assert.AreEqual("New Folder 1/New File.txt", result);

            result = FileHelper.ExtractRelativePath("New File.txt", "New File.txt");
            Assert.AreEqual("New File.txt", result);
        }

        [TestMethod]
        public void PullUpdatesTest()
        {
            const string updatesRaw = @"/sdcard/Download/DCIM/.duolingo-5-2-4.apk|0|0|
/sdcard/Download/DCIM/.duolingo-5-2-4.apk|3|25|
/sdcard/Download/DCIM/.duolingo-5-2-4.apk|6|42|
/sdcard/Download/DCIM/.duolingo-5-2-4.apk|10|69|
/sdcard/Download/DCIM/.duolingo-5-2-4.apk|12|81|
/sdcard/Download/DCIM/com.android.chrome_89.0.4389.105-438910534_11lang_11feat_31174470a9b1266fa7d8c87e62ac9c88_apkmirror.com.apkm|16|8|
/sdcard/Download/DCIM/com.android.chrome_89.0.4389.105-438910534_11lang_11feat_31174470a9b1266fa7d8c87e62ac9c88_apkmirror.com.apkm|17|27|
/sdcard/Download/DCIM/com.android.chrome_89.0.4389.105-438910534_11lang_11feat_31174470a9b1266fa7d8c87e62ac9c88_apkmirror.com.apkm|18|44|
/sdcard/Download/DCIM/com.android.chrome_89.0.4389.105-438910534_11lang_11feat_31174470a9b1266fa7d8c87e62ac9c88_apkmirror.com.apkm|20|63|
/sdcard/Download/DCIM/com.android.chrome_89.0.4389.105-438910534_11lang_11feat_31174470a9b1266fa7d8c87e62ac9c88_apkmirror.com.apkm|21|81|
/sdcard/Download/DCIM/com.android.chrome_89.0.4389.105-438910534_11lang_11feat_31174470a9b1266fa7d8c87e62ac9c88_apkmirror.com.apkm|23|99|
/sdcard/Download/DCIM/root-checker-6-5-0.apk|24|18|
/sdcard/Download/DCIM/root-checker-6-5-0.apk|25|38|
/sdcard/Download/DCIM/root-checker-6-5-0.apk|27|56|
/sdcard/Download/DCIM/root-checker-6-5-0.apk|28|77|
/sdcard/Download/DCIM/mobisaver/397860000-398046fa4.jpg|31|2|
/sdcard/Download/DCIM/mobisaver/397860000-398046fa4.jpg|32|29|
/sdcard/Download/DCIM/mobisaver/397860000-398046fa4.jpg|33|54|
/sdcard/Download/DCIM/mobisaver/397860000-398046fa4.jpg|35|81|
/sdcard/Download/DCIM/New Folder/DSC_0001 - Copy 1.JPG|36|10|
/sdcard/Download/DCIM/New Folder/DSC_0001 - Copy 1.JPG|38|42|
/sdcard/Download/DCIM/New Folder/DSC_0001 - Copy 1.JPG|39|72|
/sdcard/Download/DCIM/New Folder/DSC_0001.JPG|41|4|
/sdcard/Download/DCIM/New Folder/DSC_0001.JPG|42|34|
/sdcard/Download/DCIM/New Folder/DSC_0001.JPG|43|64|
/sdcard/Download/DCIM/New Folder/DSC_0001.JPG|45|96|
/sdcard/Download/DCIM/New Folder/DSC_0001_1 - Copy 1.JPG|46|46|
/sdcard/Download/DCIM/New Folder/DSC_0001_1.JPG|48|1|
/sdcard/Download/DCIM/New Folder/DSC_0001_1.JPG|49|53|
/sdcard/Download/DCIM/New Folder/DSC_0002 - Copy 1.JPG|50|4|
/sdcard/Download/DCIM/New Folder/DSC_0002 - Copy 1.JPG|52|35|
/sdcard/Download/DCIM/New Folder/DSC_0002 - Copy 1.JPG|53|63|
/sdcard/Download/DCIM/New Folder/DSC_0002 - Copy 1.JPG|54|92|
/sdcard/Download/DCIM/New Folder/DSC_0002.JPG|56|24|
/sdcard/Download/DCIM/New Folder/DSC_0002.JPG|57|53|
/sdcard/Download/DCIM/New Folder/DSC_0002.JPG|59|82|
/sdcard/Download/DCIM/New Folder/DSC_0002_1 - Copy 1.JPG|60|10|
/sdcard/Download/DCIM/New Folder/DSC_0002_1 - Copy 1.JPG|62|33|
/sdcard/Download/DCIM/New Folder/DSC_0002_1 - Copy 1.JPG|63|55|
/sdcard/Download/DCIM/New Folder/DSC_0002_1 - Copy 1.JPG|64|80|
/sdcard/Download/DCIM/New Folder/DSC_0002_1.JPG|66|3|
/sdcard/Download/DCIM/New Folder/DSC_0002_1.JPG|67|27|
/sdcard/Download/DCIM/New Folder/DSC_0002_1.JPG|68|50|
/sdcard/Download/DCIM/New Folder/DSC_0002_1.JPG|70|72|
/sdcard/Download/DCIM/New Folder/DSC_0002_1.JPG|71|97|
/sdcard/Download/DCIM/New Folder/DSC_0003 - Copy 1.JPG|73|90|
/sdcard/Download/DCIM/New Folder/DSC_0003.JPG|74|96|
/sdcard/Download/DCIM/New Folder/DSC_0003_1 - Copy 1.JPG|75|43|
/sdcard/Download/DCIM/New Folder/DSC_0003_1 - Copy 1.JPG|77|92|
/sdcard/Download/DCIM/New Folder/DSC_0003_1.JPG|78|38|
/sdcard/Download/DCIM/New Folder/DSC_0003_1.JPG|80|83|
/sdcard/Download/DCIM/New Folder/DSC_0004 - Copy 1.JPG|81|50|
/sdcard/Download/DCIM/New Folder/DSC_0004.JPG|83|24|
/sdcard/Download/DCIM/New Folder/DSC_0004.JPG|84|94|
/sdcard/Download/DCIM/New Folder/DSC_0004_1 - Copy 1.JPG|85|47|
/sdcard/Download/DCIM/New Folder/DSC_0004_1 - Copy 1.JPG|87|95|
/sdcard/Download/DCIM/New Folder 1/Screenshot_20170303-173019.png|88|94|
/sdcard/Download/DCIM/New Folder 1/Screenshot_20170312-223158.png|89|35|
/sdcard/Download/DCIM/New Folder 1/Screenshot_20170321-183427.png|91|100|
/sdcard/Download/DCIM/New Folder 1/Screenshot_20170321-193051.png|92|67|
/sdcard/Download/DCIM/New Folder 1/Screenshot_20170321-211752.png|94|50|
/sdcard/Download/DCIM/New Folder 1/Screenshot_20170324-205213.png|95|77|
/sdcard/Download/DCIM/New Folder 1/Screenshot_20170401-135422.png|96|77|
/sdcard/Download/DCIM/New Folder 1/Screenshot_20170406-082707.png|98|11|
/sdcard/Download/DCIM/New Folder 1/Screenshot_20170409-163142.png|99|54|";
            var updatesStringRows = updatesRaw.Split("\r\n").Select(r => r.Split('|', StringSplitOptions.RemoveEmptyEntries));
            var updates = updatesStringRows.Select(u => new AdbSyncProgressInfo(u[0], int.Parse(u[1]), int.Parse(u[2]), null));
            SyncFile file = new("/sdcard/Download/DCIM", AbstractFile.FileType.Folder);

            file.AddUpdates(updates);

            Assert.IsNotEmpty(file.Children);
            Assert.IsTrue(file.Children.All(c => c.RelationFrom(file) is AbstractFile.RelationType.Ancestor));
            Assert.AreEqual(0, file.Children.Count(c => c.ProgressUpdates.Any(u => u is SyncErrorInfo)));
        }

        [TestMethod]
        public void GetFullNameTest()
        {
            Assert.AreEqual("adb.exe", FileHelper.GetFullName(@"E:\Android_SDK\platform-tools_r33.0.3\adb.exe"));

            Assert.AreEqual("root-checker-6-5-0.apk", FileHelper.GetFullName(@"/sdcard/ASUS/root-checker-6-5-0.apk"));

            Assert.AreEqual("root-checker-6-5-0.apk", FileHelper.GetFullName(@"root-checker-6-5-0.apk"));

            Assert.AreEqual("ASUS", FileHelper.GetFullName(@"/sdcard/ASUS/"));

            Assert.AreEqual("sdcard", FileHelper.GetFullName(@"/sdcard/"));

            Assert.AreEqual("/", FileHelper.GetFullName("/"));

            Assert.AreEqual(@"C:\", FileHelper.GetFullName(@"C:\"));
        }

        [TestMethod]
        public void ParentNameTest()
        {
            Assert.AreEqual("/sdcard", FileHelper.GetParentPath("/sdcard/a"));
            Assert.AreEqual("/", FileHelper.GetParentPath("/sdcard"));
            Assert.AreEqual("E:", FileHelper.GetParentPath("E:\\New folder"));
            Assert.AreEqual("E:", FileHelper.GetParentPath("E:\\"));
        }

        [TestMethod]
        public void BytePatternTest()
        {
            Assert.AreEqual(-1, ByteHelper.PatternAt(Encoding.Unicode.GetBytes("foobar"), [0, 0]));
            Assert.AreEqual(11, ByteHelper.PatternAt(Encoding.Unicode.GetBytes("foobar\0"), [0, 0]));
            Assert.AreEqual(12, ByteHelper.PatternAt(Encoding.Unicode.GetBytes("foobar\0"), [0, 0], evenAlign: true));
        }

        [TestMethod]
        public void FileSizeTest()
        {
            long number = (long)Math.Pow(Math.Pow(9, 9), 2);
            NativeMethods.FILESIZE size = new(number);

            Assert.AreEqual(number, size.GetSize());
        }

        public class TestItem(int initialValue) : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;

            private int value = initialValue;
            public int Value
            {
                get => value;
                set
                {
                    if (this.value == value)
                        return;

                    this.value = value;
                    PropertyChanged?.Invoke(this, new(nameof(Value)));
                }
            }
        }

        [TestMethod]
        public void AddRange_TimingCheck()
        {
            // Arrange
            var list = new ObservableList<TestItem>();
            var singleItem = new List<TestItem> { new(1) };
            var multipleItems = Enumerable.Range(0, 10).Select(i => new TestItem(i)).ToList();

            var multipleItemsTime = TimeSpan.Zero;
            for (int i = 0; i < 1000; i++)
            {
                list = [];

                var stopwatch = Stopwatch.StartNew();
                list.AddRange(multipleItems);
                stopwatch.Stop();

                multipleItemsTime += stopwatch.Elapsed;
            }
            
            // Ensure items were added correctly
            Assert.HasCount(10, list);

            var singleItemTime = TimeSpan.Zero;
            for (int i = 0; i < 1000; i++)
            {
                list = [];

                var stopwatch = Stopwatch.StartNew();
                list.AddRange(singleItem);
                stopwatch.Stop();

                singleItemTime += stopwatch.Elapsed;
            }

            // Output the timing results
            var oneItemTime = singleItemTime.TotalMilliseconds;
            var totalMulti = multipleItemsTime.TotalMilliseconds;

            Console.WriteLine($"1 vs each of 10 overhead:       {oneItemTime - totalMulti / 10:F3} ns");
            Console.WriteLine($"Average for 1 item:             {oneItemTime:F3} ns");
            Console.WriteLine($"Average for each of 10 items:   {totalMulti / 10:F3} ns");
            Console.WriteLine($"Average for 10 items:           {totalMulti:F3} ns");

            // Ensure items were added correctly
            Assert.HasCount(1, list);
        }

        [TestMethod]
        public void FindTest()
        {
            ObservableList<TestItem> list = [new(8), new(3), new(22)];
            ObservableList<TestItem> emptyList = [];

            Assert.AreEqual(3, list.Find(i => i.Value == 3).Value);
            Assert.IsNull(list.Find(i => i.Value == 50));
            Assert.IsNull(emptyList.Find(i => i.Value == 50));
        }

        [TestMethod]
        public void ObservableListBatchNotificationsTest()
        {
            ObservableList<TestItem> list = [];
            int collectionChanges = 0;
            int propertyChanges = 0;
            list.CollectionChanged += (_, _) => collectionChanges++;
            ((INotifyPropertyChanged)list).PropertyChanged += (_, _) => propertyChanges++;

            list.AddRange(Enumerable.Range(0, 1000).Select(i => new TestItem(i)));

            Assert.AreEqual(1, collectionChanges);
            Assert.AreEqual(2, propertyChanges);

            collectionChanges = 0;
            propertyChanges = 0;
            list.RemoveAll(item => item.Value < 900);

            Assert.HasCount(100, list);
            Assert.AreEqual(1, collectionChanges);
            Assert.AreEqual(2, propertyChanges);
        }

        [TestMethod]
        public void FileClassFromDescriptor()
        {
            var descriptor = new FileDescriptor
            {
                ChangeTimeUtc = DateTime.Now,
                Length = 1024,
                Name = "example.txt",
            };
            var file = new FileClass(descriptor);

            Assert.AreEqual("example.txt", file.FullName);
            Assert.AreEqual(1024, file.Size.Value);
            Assert.AreEqual(descriptor.ChangeTimeUtc, file.ModifiedTime);
            Assert.AreEqual(AbstractFile.FileType.File, file.Type);

            var emptyDirDescriptor = new FileDescriptor
            {
                ChangeTimeUtc = DateTime.Now,
                Name = "Directory",
                IsDirectory = true,
            };
            var emptyDir = new FileClass(emptyDirDescriptor)
                { PathType = AbstractFile.FilePathType.Windows };

            Assert.AreEqual("Directory", emptyDir.FullName);
            Assert.AreEqual(emptyDirDescriptor.ChangeTimeUtc, emptyDir.ModifiedTime);
            Assert.IsTrue(emptyDir.IsDirectory);
            Assert.AreEqual(AbstractFile.FileType.Folder, emptyDir.Type);
            Assert.AreEqual(AbstractFile.FilePathType.Windows, emptyDir.PathType);
        }

        [TestMethod]
        public void BottomMostFoldersTest()
        {
            SyncFile parent = new("/root/parent", AbstractFile.FileType.Folder);
            SyncFile leaf = new("/root/parent/leaf", AbstractFile.FileType.Folder);
            SyncFile empty = new("/root/empty", AbstractFile.FileType.Folder);
            SyncFile file = new("/root/file.txt");
            parent.Children.Add(leaf);

            var result = FolderHelper.GetBottomMostFolders([parent, leaf, empty, file]).ToList();

            CollectionAssert.AreEquivalent(new[] { leaf, empty }, result);
        }

        [TestMethod]
        public void MergeToWindowsPathTest()
        {
            SyncFile source = new("/sdcard/Download/example.txt") { Size = 123, UnixTime = 456 };

            var target = SyncFile.MergeToWindowsPath(source, @"C:\Downloads");

            Assert.AreEqual(@"C:\Downloads\example.txt", target.FullPath);
            Assert.AreEqual(AbstractFile.FilePathType.Windows, target.PathType);
            Assert.AreEqual(source.Size, target.Size);
            Assert.AreEqual(source.UnixTime, target.UnixTime);
        }

        [TestMethod]
        public void SyncFileFromWindowsPathTest()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"ADB_Explorer_SyncFile_{Guid.NewGuid():N}");

            try
            {
                string nestedPath = Directory.CreateDirectory(Path.Combine(rootPath, "nested")).FullName;
                string emptyPath = Directory.CreateDirectory(Path.Combine(rootPath, "empty")).FullName;
                string rootFilePath = Path.Combine(rootPath, "root.bin");
                string nestedFilePath = Path.Combine(nestedPath, "nested.txt");
                File.WriteAllBytes(rootFilePath, [1, 2, 3, 4]);
                File.WriteAllText(nestedFilePath, "content");

                var root = SyncFile.FromWindowsPath(rootPath);
                var rootFile = root.Children.Single(file => file.FullPath == rootFilePath);
                var nested = root.Children.Single(file => file.FullPath == nestedPath);
                var empty = root.Children.Single(file => file.FullPath == emptyPath);
                var nestedFile = nested.Children.Single();

                Assert.IsTrue(root.IsDirectory);
                Assert.AreEqual(AbstractFile.FilePathType.Windows, root.PathType);
                Assert.AreEqual(4L, rootFile.Size.Value);
                Assert.IsNotNull(rootFile.UnixTime);
                Assert.AreEqual(AbstractFile.FilePathType.Windows, rootFile.PathType);
                Assert.IsTrue(nested.IsDirectory);
                Assert.AreEqual(nestedFilePath, nestedFile.FullPath);
                Assert.AreEqual(new FileInfo(nestedFilePath).Length, nestedFile.Size.Value);
                Assert.AreEqual(AbstractFile.FilePathType.Windows, nestedFile.PathType);
                Assert.IsTrue(empty.IsDirectory);
                Assert.IsEmpty(empty.Children);
                Assert.IsEmpty(SyncFile.FromWindowsPath(rootPath, false).Children);
            }
            finally
            {
                if (Directory.Exists(rootPath))
                    Directory.Delete(rootPath, true);
            }
        }

        [TestMethod]
        public void SyncParallelismLimitsTest()
        {
            Assert.AreEqual(1, FileSyncOperation.CalculateMaxDegreeOfParallelism(
                false, AbstractDevice.DeviceType.Local, 32, 100));
            Assert.AreEqual(4, FileSyncOperation.CalculateMaxDegreeOfParallelism(
                true, AbstractDevice.DeviceType.Local, 32, 100));
            Assert.AreEqual(2, FileSyncOperation.CalculateMaxDegreeOfParallelism(
                true, AbstractDevice.DeviceType.Remote, 32, 100));
            Assert.AreEqual(2, FileSyncOperation.CalculateMaxDegreeOfParallelism(
                true, AbstractDevice.DeviceType.WSA, 32, 100));
            Assert.AreEqual(2, FileSyncOperation.CalculateMaxDegreeOfParallelism(
                true, AbstractDevice.DeviceType.Emulator, 32, 100));
            Assert.AreEqual(1, FileSyncOperation.CalculateMaxDegreeOfParallelism(
                true, AbstractDevice.DeviceType.Local, 32, 1));
        }

        [TestMethod]
        public void LogicalDeviceFromDeviceDataTest()
        {
            DeviceData source = new()
            {
                Serial = "device-1",
                State = DeviceState.Online,
                Product = "product",
                Model = "Test_Model",
                Name = "test_device",
                Features = ["shell_v2", "cmd"],
                TransportId = "7",
            };

            var device = LogicalDevice.New(source);

            Assert.AreEqual("device-1", device.ID);
            Assert.AreEqual("Test Model", device.Name);
            Assert.AreEqual(AbstractDevice.DeviceStatus.Ok, device.Status);
            Assert.AreSame(source, device.DeviceData);
        }

        [TestMethod]
        public void VisibleDeviceSnapshotIsFilteredAndSorted()
        {
            var newDevice = new NewDeviceViewModel(new());
            var hiddenConnectService = new ConnectServiceViewModel(
                new ConnectService("service", "192.168.0.2", "5555"), false);
            var logicalDevice = new LogicalDeviceViewModel(LogicalDevice.New(new DeviceData
            {
                Serial = "device-1",
                State = DeviceState.Online,
                Model = "Device",
            }), false);
            DeviceViewModel[] source = [newDevice, hiddenConnectService, logicalDevice];

            var visible = DeviceHelper.GetVisibleDevices(source);

            CollectionAssert.AreEqual(
                new DeviceViewModel[] { logicalDevice, newDevice },
                visible);
            CollectionAssert.AreEqual(
                new DeviceViewModel[] { newDevice, hiddenConnectService, logicalDevice },
                source);
        }

        [TestMethod]
        public void SyncBatchTargetPathTest()
        {
            SyncFile windowsRoot = new(@"C:\Source", AbstractFile.FileType.Folder)
            {
                PathType = AbstractFile.FilePathType.Windows,
            };
            SyncFile windowsFile = new(@"C:\Source\example.txt")
            {
                PathType = AbstractFile.FilePathType.Windows,
            };
            SyncFile androidTarget = new("/sdcard/Download", AbstractFile.FileType.Folder);

            Assert.AreEqual("/sdcard/Download/example.txt", FileSyncOperation.ResolveTargetPath(
                windowsRoot, androidTarget, windowsFile));

            SyncFile androidRoot = new("/sdcard/Source", AbstractFile.FileType.Folder);
            SyncFile androidFile = new("/sdcard/Source/example.txt");
            SyncFile windowsTarget = new(@"C:\Downloads", AbstractFile.FileType.Folder)
            {
                PathType = AbstractFile.FilePathType.Windows,
            };

            Assert.AreEqual(@"C:\Downloads\example.txt", FileSyncOperation.ResolveTargetPath(
                androidRoot, windowsTarget, androidFile));
            Assert.AreEqual("/sdcard/renamed.txt", FileSyncOperation.ResolveTargetPath(
                androidFile, new SyncFile("/sdcard/renamed.txt"), androidFile));
        }

        [TestMethod]
        public void SyncProgressAggregationTest()
        {
            var totals = FileSyncOperation.CalculateProgressTotals(0, 0, 0, 50, 0, 50);
            Assert.AreEqual(50L, totals.TransferredBytes);
            Assert.AreEqual(1, totals.ActiveCount);
            Assert.AreEqual(50d, totals.ActivePercentage);

            totals = FileSyncOperation.CalculateProgressTotals(
                totals.TransferredBytes,
                totals.ActiveCount,
                totals.ActivePercentage,
                50,
                50,
                100);
            Assert.AreEqual(100L, totals.TransferredBytes);
            Assert.AreEqual(0, totals.ActiveCount);
            Assert.AreEqual(0d, totals.ActivePercentage);

            totals = FileSyncOperation.CalculateProgressTotals(
                totals.TransferredBytes,
                totals.ActiveCount,
                totals.ActivePercentage,
                -100,
                100,
                0);
            Assert.AreEqual(0L, totals.TransferredBytes);
            Assert.AreEqual(0, totals.ActiveCount);
        }

        [TestMethod]
        public void SyncProgressSourceThrottleTest()
        {
            long lastProgressUpdate = 0;
            int reports = 0;

            for (long timestamp = 100; timestamp <= 1_100; timestamp++)
            {
                if (FileSyncOperation.ShouldReportProgress(timestamp, 50, ref lastProgressUpdate))
                    reports++;
            }

            Assert.AreEqual(11, reports);
            Assert.IsTrue(FileSyncOperation.ShouldReportProgress(1_101, 100, ref lastProgressUpdate));
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void SyncProgressLargeBatchPerformanceTest()
        {
            const int fileCount = 10_000;
            SyncFile root = new("/sdcard/Download", AbstractFile.FileType.Folder);
            root.Children.AddRange(Enumerable.Range(0, fileCount)
                .Select(i => new SyncFile($"/sdcard/Download/file_{i}.bin")));
            var updates = Enumerable.Range(0, fileCount)
                .Select(i => new AdbSyncProgressInfo($"/sdcard/Download/file_{i}.bin", 100, 100, 1))
                .ToArray();

            var stopwatch = Stopwatch.StartNew();
            root.AddUpdates(updates);
            stopwatch.Stop();

            Console.WriteLine($"Applied {fileCount:N0} progress updates in {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.IsLessThan(500d, stopwatch.Elapsed.TotalMilliseconds);
            Assert.IsTrue(root.Children.All(file => file.CurrentPercentage == 100));
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void SyncFileLargeTreeDetachPerformanceTest()
        {
            const int fileCount = 100_000;
            SyncFile root = new("/sdcard/Download", AbstractFile.FileType.Folder);
            root.Children.AddRange(Enumerable.Range(0, fileCount)
                .Select(i => new SyncFile($"/sdcard/Download/file_{i}.bin")));

            var stopwatch = Stopwatch.StartNew();
            root.ClearAll();
            stopwatch.Stop();

            Console.WriteLine($"Detached a {fileCount:N0}-item sync tree in {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.IsLessThan(100d, stopwatch.Elapsed.TotalMilliseconds);
            Assert.IsEmpty(root.Children);
            Assert.IsEmpty(root.ProgressUpdates);
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void SyncFileLargeWindowsTreePerformanceTest()
        {
            const int directoryCount = 20;
            const int filesPerDirectory = 100;
            string rootPath = Path.Combine(Path.GetTempPath(), $"ADB_Explorer_SyncTree_{Guid.NewGuid():N}");

            try
            {
                for (int directoryIndex = 0; directoryIndex < directoryCount; directoryIndex++)
                {
                    string directory = Directory.CreateDirectory(
                        Path.Combine(rootPath, $"folder_{directoryIndex}")).FullName;
                    for (int fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
                        File.WriteAllBytes(Path.Combine(directory, $"file_{fileIndex}.bin"), []);
                }

                var stopwatch = Stopwatch.StartNew();
                var root = SyncFile.FromWindowsPath(rootPath);
                stopwatch.Stop();
                var descendants = root.AllChildren().ToList();

                Console.WriteLine($"Built a {directoryCount * filesPerDirectory:N0}-file Windows tree in {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
                Assert.IsLessThan(2_000d, stopwatch.Elapsed.TotalMilliseconds);
                Assert.AreEqual(directoryCount * filesPerDirectory, descendants.Count(file => !file.IsDirectory));
                Assert.IsTrue(descendants.All(file => file.PathType is AbstractFile.FilePathType.Windows));
            }
            finally
            {
                if (Directory.Exists(rootPath))
                    Directory.Delete(rootPath, true);
            }
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void WindowsFolderHashPerformanceTest()
        {
            const int directoryCount = 10;
            const int filesPerDirectory = 50;
            string rootPath = Path.Combine(Path.GetTempPath(), $"ADB_Explorer_HashTree_{Guid.NewGuid():N}");

            try
            {
                for (int directoryIndex = 0; directoryIndex < directoryCount; directoryIndex++)
                {
                    string directory = Directory.CreateDirectory(
                        Path.Combine(rootPath, $"folder_{directoryIndex}")).FullName;
                    for (int fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
                        File.WriteAllText(Path.Combine(directory, $"file_{fileIndex}.txt"), $"{directoryIndex}:{fileIndex}");
                }

                var stopwatch = Stopwatch.StartNew();
                var hashes = Security.CalculateWindowsFolderHash(rootPath);
                stopwatch.Stop();

                Console.WriteLine($"Hashed {directoryCount * filesPerDirectory:N0} files in {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
                Assert.IsLessThan(2_000d, stopwatch.Elapsed.TotalMilliseconds);
                Assert.HasCount(directoryCount * filesPerDirectory, hashes);
                Assert.IsTrue(hashes.Keys.All(path => path.Contains('/') && !path.Contains('\\')));
            }
            finally
            {
                if (Directory.Exists(rootPath))
                    Directory.Delete(rootPath, true);
            }
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void SyncFileLargeAndroidTreePerformanceTest()
        {
            const int directoryCount = 100;
            const int filesPerDirectory = 100;
            List<(string, long?, double?)> tree = new(directoryCount * (filesPerDirectory + 1));
            for (int directoryIndex = 0; directoryIndex < directoryCount; directoryIndex++)
            {
                string directory = $"/root/folder_{directoryIndex}";
                tree.Add((directory, null, null));
                for (int fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
                    tree.Add(($"{directory}/file_{fileIndex}.bin", fileIndex, 1_700_000_000));
            }

            FileClass rootFile = new("root", "/root", AbstractFile.FileType.Folder, loadIcon: false);
            var stopwatch = Stopwatch.StartNew();
            SyncFile root = new(rootFile, tree);
            stopwatch.Stop();
            var descendants = root.AllChildren().ToList();

            Console.WriteLine($"Built a {directoryCount * filesPerDirectory:N0}-file Android tree in {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.IsLessThan(1_000d, stopwatch.Elapsed.TotalMilliseconds);
            Assert.HasCount(directoryCount, root.Children);
            Assert.AreEqual(directoryCount, descendants.Count(file => file.IsDirectory));
            Assert.AreEqual(directoryCount * filesPerDirectory, descendants.Count(file => !file.IsDirectory));
            Assert.IsTrue(descendants.Where(file => !file.IsDirectory).All(file => file.Size is not null));
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void FileGroupLargeDescriptorPerformanceTest()
        {
            const int descriptorCount = 10_000;
            var descriptors = Enumerable.Range(0, descriptorCount)
                .Select(i => new FileDescriptor
                {
                    Name = $"folder/file_{i}.bin",
                    Length = i,
                    ChangeTimeUtc = DateTime.UnixEpoch,
                })
                .ToArray();

            var stopwatch = Stopwatch.StartNew();
            FileGroup group = new(descriptors);
            stopwatch.Stop();

            int expectedLength = sizeof(UInt32) + descriptorCount * Marshal.SizeOf<FILEDESCRIPTOR>();
            Console.WriteLine($"Serialized {descriptorCount:N0} virtual file descriptors in {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.IsLessThan(1_000d, stopwatch.Elapsed.TotalMilliseconds);
            Assert.HasCount(expectedLength, group.GroupDescriptorBytes);
            Assert.HasCount(descriptorCount, group.FileDescriptors);
        }

        [TestMethod]
        public void StructureSerializationValidationTest()
        {
            const long value = 0x1234567890;
            byte[] bytes = NativeMethods.BytesFromStructure(value);

            Assert.AreEqual(value, NativeMethods.StructureFromBytes<long>(bytes));
            Assert.ThrowsExactly<ArgumentException>(() =>
                NativeMethods.StructureFromBytes<long>(bytes.Take(bytes.Length - 1)));
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void ObservableListLargeBatchPerformanceTest()
        {
            const int itemCount = 50_000;
            ObservableList<TestItem> list = [];
            int collectionChanges = 0;
            list.CollectionChanged += (_, _) => collectionChanges++;
            var items = Enumerable.Range(0, itemCount).Select(i => new TestItem(i)).ToArray();

            var stopwatch = Stopwatch.StartNew();
            list.AddRange(items);
            stopwatch.Stop();
            double addMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            list.RemoveAll(item => item.Value % 2 == 0);
            stopwatch.Stop();
            double removeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            Console.WriteLine($"Added {itemCount:N0} items in {addMilliseconds:F1} ms and removed half in {removeMilliseconds:F1} ms");
            Assert.IsLessThan(1_000d, addMilliseconds);
            Assert.IsLessThan(1_000d, removeMilliseconds);
            Assert.HasCount(itemCount / 2, list);
            Assert.AreEqual(2, collectionChanges);
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void DeviceServiceMergePerformanceTest()
        {
            const int serviceCount = 5_000;
            ObservableList<DeviceViewModel> current = [];
            current.AddRange(Enumerable.Range(0, serviceCount)
                .Select(i => (DeviceViewModel)new PairingServiceViewModel(
                    new PairingService($"service_{i}", $"10.0.{i / 256}.{i % 256}", "37000"), false)));
            var incoming = Enumerable.Range(0, serviceCount)
                .Select(i => (ServiceDeviceViewModel)new PairingServiceViewModel(
                    new PairingService($"service_{i}", $"10.0.{i / 256}.{i % 256}", "37000"), false))
                .ToArray();

            var stopwatch = Stopwatch.StartNew();
            Devices.UpdateServices(current, incoming);
            stopwatch.Stop();

            Console.WriteLine($"Merged {serviceCount:N0} stable services in {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
            Assert.IsLessThan(250d, stopwatch.Elapsed.TotalMilliseconds);
            Assert.HasCount(serviceCount, current);
        }

        [TestMethod]
        public void FileOperationQueueConcurrencyGateTest()
        {
            Assert.IsTrue(FileOperationQueue.ShouldWaitForRunningGroup(true, null, 1));
            Assert.IsTrue(FileOperationQueue.ShouldWaitForRunningGroup(
                false, FileOperation.OperationType.Move, 1));
            Assert.IsFalse(FileOperationQueue.ShouldWaitForRunningGroup(
                true, FileOperation.OperationType.Move, 1));
            Assert.IsTrue(FileOperationQueue.ShouldWaitForRunningGroup(
                true, FileOperation.OperationType.Move, 4));
            Assert.IsFalse(FileOperationQueue.ShouldWaitForRunningGroup(true, null, 0));
        }
    }
}
