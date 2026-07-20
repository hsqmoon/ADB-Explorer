using ADB_Explorer.Models;
using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ADB_Test;

[TestClass]
public class RealTransferPerformanceTests
{
    [STATestMethod]
    public async Task FileSyncOperationThroughputRemainsWithinFivePercentOfItsTransport()
    {
        string deviceId = Environment.GetEnvironmentVariable("ADB_EXPLORER_TRANSFER_DEVICE");
        string sourcePath = Environment.GetEnvironmentVariable("ADB_EXPLORER_TRANSFER_SOURCE");
        string targetPath = Environment.GetEnvironmentVariable("ADB_EXPLORER_TRANSFER_TARGET");
        if (string.IsNullOrWhiteSpace(deviceId)
            || string.IsNullOrWhiteSpace(sourcePath)
            || string.IsNullOrWhiteSpace(targetPath))
        {
            Assert.Inconclusive("Set the ADB_EXPLORER_TRANSFER_* variables to run the real-device throughput test.");
        }
        if (!File.Exists(sourcePath))
            Assert.Fail($"Transfer source does not exist: {sourcePath}");
        if (!Regex.IsMatch(targetPath, "^/[A-Za-z0-9_./-]+$"))
            Assert.Fail("Transfer target contains unsupported shell characters.");

        var endpoint = new IPEndPoint(IPAddress.Loopback, 5037);
        var deviceData = (await new AdbClient(endpoint).GetDevicesAsync())
            .Single(device => device.Serial == deviceId);
        var logicalDevice = new LogicalDeviceViewModel(LogicalDevice.New(deviceData), false);
        var adbDevice = new ADBService.AdbDevice(logicalDevice);
        List<double> transportRates = [];
        List<double> operationRates = [];
        long sourceLength = new FileInfo(sourcePath).Length;

        try
        {
            for (int iteration = 0; iteration < 3; iteration++)
            {
                bool canceled = false;
                using (var service = new SyncService(endpoint, deviceData))
                using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var stopwatch = Stopwatch.StartNew();
                    service.Push(
                        stream,
                        targetPath,
                        UnixFileStatus.AllPermissions | UnixFileStatus.Regular,
                        DateTime.Now,
                        null,
                        false,
                        in canceled);
                    stopwatch.Stop();
                    transportRates.Add(sourceLength / stopwatch.Elapsed.TotalSeconds);
                }

                var source = SyncFile.FromWindowsPath(sourcePath);
                var target = new SyncFile(targetPath);
                var operation = FileSyncOperation.PushFile(
                    source,
                    target,
                    adbDevice,
                    Dispatcher.CurrentDispatcher);
                operation.Start();
                await operation.Completion.WaitAsync(TimeSpan.FromMinutes(2));
                var elapsed = operation.TransferEnd - operation.TransferStart;
                Assert.IsGreaterThan(0, elapsed.TotalSeconds);
                operationRates.Add(sourceLength / elapsed.TotalSeconds);
            }
        }
        finally
        {
            await new AdbCommandClient().ExecuteShellAsync(
                logicalDevice,
                $"rm -f -- '{targetPath}'",
                CancellationToken.None);
        }

        transportRates.Sort();
        operationRates.Sort();
        double transportMedian = transportRates[transportRates.Count / 2];
        double operationMedian = operationRates[operationRates.Count / 2];
        TestContext.WriteLine($"Transport median: {transportMedian / 1048576:F2} MiB/s");
        TestContext.WriteLine($"FileSyncOperation median: {operationMedian / 1048576:F2} MiB/s");
        Assert.IsGreaterThanOrEqualTo(transportMedian * 0.95, operationMedian);
    }

    public TestContext TestContext { get; set; }
}
